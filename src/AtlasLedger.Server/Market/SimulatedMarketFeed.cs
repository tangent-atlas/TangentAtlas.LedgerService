// <copyright file="SimulatedMarketFeed.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Market
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using AtlasLedger.Server.Switches;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Stands in for the market data feed: prices every symbol any tenant holds with a random walk, and now and then sends
    /// a bad print.  It runs only while its switch (<see cref="ServiceSwitchboard.MarketFeed"/>) is on.
    /// </summary>
    /// <remarks>
    /// A real feed replaces this class and nothing else.  Prices go straight into each holding tenant's lock-free price
    /// store: no transaction, no API, never persisted.  The rules engine reads them conflated, so the tick rate does not
    /// drive the cost of compliance.
    /// </remarks>
    internal sealed partial class SimulatedMarketFeed : BackgroundService
    {
        private const decimal OpeningPrice = 100m;

        private readonly Ledger ledger;

        private readonly MarketFeedOptions options;

        private readonly ServiceSwitch serviceSwitch;

        private readonly TimeProvider timeProvider;

        private readonly ILogger<SimulatedMarketFeed> logger;

        private readonly Dictionary<(string Scheme, string Code), decimal> prices = new Dictionary<(string Scheme, string Code), decimal>();

        /// <summary>
        /// Initializes a new instance of the <see cref="SimulatedMarketFeed"/> class.
        /// </summary>
        /// <param name="ledger">The ledger.</param>
        /// <param name="options">The feed's settings.</param>
        /// <param name="switchboard">The services' switches.</param>
        /// <param name="logger">The logger.</param>
        public SimulatedMarketFeed(Ledger ledger, IOptions<MarketFeedOptions> options, ServiceSwitchboard switchboard, ILogger<SimulatedMarketFeed> logger)
        {
            this.ledger = ledger;
            this.options = options.Value;
            this.serviceSwitch = switchboard[ServiceSwitchboard.MarketFeed];
            this.timeProvider = TimeProvider.System;
            this.logger = logger;
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (!this.serviceSwitch.IsOn)
                    {
                        await this.serviceSwitch.WaitForChangeAsync(stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    this.LogSwitchedOn(this.serviceSwitch.OnUntil);
                    await this.RunWhileOnAsync(stoppingToken).ConfigureAwait(false);
                    this.LogSwitchedOff();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// Ticks until the switch is off; it is checked every tick, so the feed stops within one tick interval.
        /// </summary>
        private async Task RunWhileOnAsync(CancellationToken stoppingToken)
        {
            var nextBadPrint = this.timeProvider.GetUtcNow() + this.options.BadPrintInterval;
            using var timer = new PeriodicTimer(this.options.TickInterval, this.timeProvider);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false) && this.serviceSwitch.IsOn)
            {
                var now = this.timeProvider.GetUtcNow();
                var isBadPrint = this.options.BadPrintInterval > TimeSpan.Zero && now >= nextBadPrint;
                if (this.Tick(now, isBadPrint))
                {
                    nextBadPrint = now + this.options.BadPrintInterval;
                }
            }
        }

        /// <summary>
        /// Prices every held symbol once.
        /// </summary>
        /// <returns>True if a bad print was sent.</returns>
        private bool Tick(DateTimeOffset now, bool isBadPrint)
        {
            // Who holds what, read under each tenant's lock and released before publishing.
            var holders = new Dictionary<(string Scheme, string Code), List<LedgerPartition>>();
            foreach (var partition in this.ledger.Partitions)
            {
                lock (partition.SyncRoot)
                {
                    foreach (var symbol in partition.Symbols)
                    {
                        var key = (symbol.Scheme, symbol.Code);
                        if (!holders.TryGetValue(key, out var list))
                        {
                            holders[key] = list = new List<LedgerPartition>();
                        }

                        list.Add(partition);
                    }
                }
            }

            if (holders.Count == 0)
            {
                return false;
            }

            var badSymbol = isBadPrint ? holders.Keys.ElementAt(Random.Shared.Next(holders.Count)) : default;
            foreach (var (key, partitions) in holders)
            {
                var last = this.prices.TryGetValue(key, out var previous) ? previous : OpeningPrice;
                last = Math.Max(0.01m, Math.Round(last * (1m + ((decimal)Random.Shared.NextDouble() - 0.5m) / 100m), 2));
                this.prices[key] = last;
                var isBad = isBadPrint && key == badSymbol;
                var price = new Price { Scheme = key.Scheme, Code = key.Code, Last = isBad ? 0m : last, AsOf = now };
                foreach (var partition in partitions)
                {
                    partition.PublishPrice(price);
                }
            }

            if (isBadPrint)
            {
                this.LogBadPrint(badSymbol.Scheme, badSymbol.Code);
            }

            return isBadPrint;
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Market feed: switched on until {OnUntil}.")]
        private partial void LogSwitchedOn(DateTimeOffset? onUntil);

        [LoggerMessage(Level = LogLevel.Information, Message = "Market feed: switched off.")]
        private partial void LogSwitchedOff();

        [LoggerMessage(Level = LogLevel.Warning, Message = "Market feed: bad print for {Scheme}:{Code} (price 0); corrected on the next tick.")]
        private partial void LogBadPrint(string scheme, string code);
    }
}
