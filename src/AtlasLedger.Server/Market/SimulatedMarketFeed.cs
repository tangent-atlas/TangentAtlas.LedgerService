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
    using AtlasLedger.Server.Events;
    using AtlasLedger.Server.Holdings;
    using AtlasLedger.Server.Switches;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Stands in for the market data feed: prices every symbol any tenant holds with a random walk (a ticker starting from its last trade), and now and then sends
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

        private readonly Dictionary<(string Scheme, string Code, string Currency), decimal> prices = new Dictionary<(string Scheme, string Code, string Currency), decimal>();

        // Events never change, so each is read once.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid TenantId, Guid EventId), LedgerEvent> decoded = new System.Collections.Concurrent.ConcurrentDictionary<(Guid TenantId, Guid EventId), LedgerEvent>();

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
            // Who holds what, read under each tenant's lock and released before publishing.  A ticker the feed hasn't
            // priced yet starts from its asset's last purchase or sale, so a tenant loaded with history isn't valued at $100 a share.
            var holders = new Dictionary<(string Scheme, string Code, string Currency), List<LedgerPartition>>();
            var openingPrices = new Dictionary<(string Scheme, string Code, string Currency), decimal>();
            foreach (var partition in this.ledger.Partitions)
            {
                lock (partition.SyncRoot)
                {
                    // The last purchase or sale of each asset: a trade item's amount ÷ (quantity × factors), or a fill's price.
                    var lastTrades = new Dictionary<Guid, (DateTimeOffset EffectiveTime, decimal Price)>();
                    void Consider(Guid assetId, DateTimeOffset time, decimal price)
                    {
                        if (!lastTrades.TryGetValue(assetId, out var latest) || time > latest.EffectiveTime)
                        {
                            lastTrades[assetId] = (time, price);
                        }
                    }

                    var events = partition.Events.Select(row => this.decoded.GetOrAdd((partition.TenantId, row.EventId), _ => EventRows.Read(row))).ToArray();
                    var legs = events.OfType<OrderPlacedEvent>().SelectMany(order => order.Legs.Select(leg => (order.OrderId, leg.LegId, leg.AssetId))).ToDictionary(item => (item.OrderId, item.LegId), item => item.AssetId);
                    foreach (var value in events)
                    {
                        if (value is TradeEvent trade)
                        {
                            foreach (var item in trade.Items)
                            {
                                var asset = partition.FindAsset(item.AssetId);
                                var units = asset is null ? 0m : item.Quantity * asset.QuantityFactor * asset.PriceFactor;
                                if (asset is { AssetType: not AssetType.Cash } && units != 0m && item.Amount is { } amount)
                                {
                                    Consider(item.AssetId, trade.Envelope.EffectiveTime, Math.Abs(amount / units));
                                }
                            }
                        }
                        else if (value is ExecutionReportedEvent fill && legs.TryGetValue((fill.OrderId, fill.LegId), out var assetId))
                        {
                            Consider(assetId, fill.ReportTime, fill.Price);
                        }
                    }

                    var quoteCurrencies = partition.Assets.ToDictionary(asset => asset.AssetId, asset => asset.QuoteCurrency);
                    foreach (var symbol in partition.Symbols)
                    {
                        // A currency's price comes from the initial prices, not the market; a symbol of anything but an
                        // asset has no quote currency to be priced in.
                        if (symbol.Scheme == SymbolScheme.Currency || !quoteCurrencies.TryGetValue(symbol.NodeId, out var currency))
                        {
                            continue;
                        }

                        var key = (symbol.Scheme, symbol.Code, currency);
                        if (symbol.Scheme == SymbolScheme.Ticker && lastTrades.TryGetValue(symbol.NodeId, out var lastTrade))
                        {
                            openingPrices.TryAdd(key, Math.Round(lastTrade.Price, 2));
                        }

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
                var last = this.prices.TryGetValue(key, out var previous) ? previous : openingPrices.GetValueOrDefault(key, OpeningPrice);
                last = Math.Max(0.01m, Math.Round(last * (1m + ((decimal)Random.Shared.NextDouble() - 0.5m) / 100m), 2));
                this.prices[key] = last;
                var isBad = isBadPrint && key == badSymbol;
                var price = new Price { Scheme = key.Scheme, Code = key.Code, Currency = key.Currency, Last = isBad ? 0m : last, AsOf = now };
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
