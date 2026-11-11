// <copyright file="OrderGenerator.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Orders
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using AtlasLedger.Server.Holdings;
    using AtlasLedger.Server.Switches;
    using Bellerophon.Rules;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Settings for the order generator.
    /// </summary>
    public sealed class TradingOptions
    {
        /// <summary>
        /// The settings section.
        /// </summary>
        public const string SectionName = "Trading";

        /// <summary>
        /// Gets or sets when the order generator runs, each weekday, in New York: default 3:45 pm.
        /// </summary>
        public TimeOnly TimeOfDay { get; set; } = new TimeOnly(15, 45);
    }

    /// <summary>
    /// What one run of the order generator did for a tenant.
    /// </summary>
    /// <param name="Orders">Orders written: one per asset and side, aggregating every account that wants it.</param>
    /// <param name="BrokerOrders">Broker orders written: one per account in each order.</param>
    /// <param name="Waiting">Accounts left out because a price was missing or stale.</param>
    /// <param name="Reason">Why nothing was written, when nothing could be.</param>
    public sealed record GeneratedOrders(int Orders, int BrokerOrders, int Waiting, string? Reason);

    /// <summary>
    /// Turns the rules' proposed allocations into orders: at the scheduled time each weekday while its switch
    /// (<see cref="ServiceSwitchboard.Trading"/>) is on, or when an operator asks.  For each tenant it takes the proposals
    /// of the rules' latest snapshot and writes, in one transaction, an order per asset and side (aggregating every account
    /// that wants it), the allocations that went into it, and a market day broker order per account.  The order router
    /// sends them.
    /// </summary>
    /// <remarks>
    /// The proposals subtract open orders, so once these orders are seen by the rules, the next proposals no longer ask
    /// for them.  The one race is running again before the rules have seen the last run's orders; so a run works only from a
    /// snapshot of the rules that has applied the tenant's ledger through the last run's commit.
    /// </remarks>
    public sealed partial class OrderGenerator : BackgroundService
    {
        private static readonly TimeZoneInfo Exchange = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        private readonly Ledger ledger;

        private readonly RulesEngineHost host;

        private readonly HoldingsStore store;

        private readonly ServiceSwitch serviceSwitch;

        private readonly TradingOptions options;

        private readonly TimeProvider timeProvider;

        private readonly ILogger<OrderGenerator> logger;

        private readonly ConcurrentDictionary<Guid, long> lastCommits = new ConcurrentDictionary<Guid, long>();

        private readonly ConcurrentDictionary<Guid, object> tenantLocks = new ConcurrentDictionary<Guid, object>();

        /// <summary>
        /// Initializes a new instance of the <see cref="OrderGenerator"/> class.
        /// </summary>
        /// <param name="ledger">The ledger.</param>
        /// <param name="host">The rules engines, for how far each has applied its tenant's ledger.</param>
        /// <param name="store">The rules' snapshots, holding the proposals.</param>
        /// <param name="switchboard">The services' switches.</param>
        /// <param name="options">The settings.</param>
        /// <param name="timeProvider">The clock.</param>
        /// <param name="logger">The log.</param>
        public OrderGenerator(Ledger ledger, RulesEngineHost host, HoldingsStore store, ServiceSwitchboard switchboard, IOptions<TradingOptions> options, TimeProvider timeProvider, ILogger<OrderGenerator> logger)
        {
            ArgumentNullException.ThrowIfNull(switchboard);
            ArgumentNullException.ThrowIfNull(options);
            this.ledger = ledger;
            this.host = host;
            this.store = store;
            this.serviceSwitch = switchboard[ServiceSwitchboard.Trading];
            this.options = options.Value;
            this.timeProvider = timeProvider;
            this.logger = logger;
        }

        /// <summary>
        /// The next scheduled run after a time: the time of day in New York, on the next weekday it hasn't passed.
        /// </summary>
        /// <param name="after">The time.</param>
        /// <param name="timeOfDay">The time of day, in New York.</param>
        /// <returns>The next run.</returns>
        public static DateTimeOffset NextRun(DateTimeOffset after, TimeOnly timeOfDay)
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, Exchange).DateTime);
            while (true)
            {
                var local = day.ToDateTime(timeOfDay);
                var run = new DateTimeOffset(local, Exchange.GetUtcOffset(local));
                if (run > after && day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                {
                    return run;
                }

                day = day.AddDays(1);
            }
        }

        /// <summary>
        /// Writes a tenant's orders from the rules' current proposals.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <returns>What was written.</returns>
        public GeneratedOrders Generate(Guid tenantId)
        {
            if (this.ledger.FindPartition(tenantId) is not { } partition)
            {
                return new GeneratedOrders(0, 0, 0, "No such tenant.");
            }

            lock (this.tenantLocks.GetOrAdd(tenantId, _ => new object()))
            {
                // Only from a snapshot that has seen this generator's last orders: otherwise it would order them again.
                if (this.host.FindEngine(tenantId) is not { } engine)
                {
                    return new GeneratedOrders(0, 0, 0, "The rules engine isn't running for this tenant.");
                }

                if (engine.AppliedSequence < this.lastCommits.GetValueOrDefault(tenantId))
                {
                    return new GeneratedOrders(0, 0, 0, "The rules haven't seen the last orders yet; try again in a moment.");
                }

                if (this.store.Find(tenantId) is not { } snapshot)
                {
                    return new GeneratedOrders(0, 0, 0, "The rules haven't finished a cycle for this tenant yet.");
                }

                var waiting = snapshot.Proposals.Count(proposal => proposal.IsWaiting);
                var trades = snapshot.Proposals
                    .SelectMany(proposal => proposal.Trades.Select(trade => (proposal.AccountId, Trade: trade)))
                    .GroupBy(item => (item.Trade.AssetId, item.Trade.Side))
                    .OrderBy(group => group.Key.AssetId)
                    .ThenBy(group => group.Key.Side)
                    .ToArray();
                if (trades.Length == 0)
                {
                    return new GeneratedOrders(0, 0, waiting, null);
                }

                var now = this.timeProvider.GetUtcNow();
                var brokerOrders = 0;
                using var transaction = partition.BeginTransaction();
                foreach (var group in trades)
                {
                    var orderId = Guid.CreateVersion7(now);
                    transaction.InsertOrder(new Order { OrderId = orderId, AssetId = group.Key.AssetId, Side = group.Key.Side, Origin = OrderOrigin.Rebalance, EnteredTime = now });
                    foreach (var (accountId, trade) in group.OrderBy(item => item.AccountId))
                    {
                        transaction.InsertAllocation(new Allocation { OrderId = orderId, AccountId = accountId, Quantity = trade.Quantity });
                        transaction.InsertBrokerOrder(new BrokerOrder
                        {
                            BrokerOrderId = Guid.CreateVersion7(now),
                            OrderId = orderId,
                            AccountId = accountId,
                            Quantity = trade.Quantity,
                            OrderType = OrderType.Market,
                            TimeInForce = TimeInForce.Day,
                            EnteredTime = now,
                        });
                        brokerOrders++;
                    }
                }

                this.lastCommits[tenantId] = transaction.Commit();
                this.LogGenerated(tenantId, trades.Length, brokerOrders, waiting);
                return new GeneratedOrders(trades.Length, brokerOrders, waiting, null);
            }
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

                    var next = NextRun(this.timeProvider.GetUtcNow(), this.options.TimeOfDay);
                    this.LogScheduled(next);
                    using (var change = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                    {
                        var switched = this.serviceSwitch.WaitForChangeAsync(change.Token);
                        var due = Task.Delay(next - this.timeProvider.GetUtcNow(), this.timeProvider, change.Token);
                        var first = await Task.WhenAny(switched, due).ConfigureAwait(false);
                        await change.CancelAsync().ConfigureAwait(false);
                        if (first != due || !this.serviceSwitch.IsOn)
                        {
                            continue;   // switched off (or on again): reconsider
                        }
                    }

                    foreach (var partition in this.ledger.Partitions.ToArray())
                    {
                        if (!partition.IsFaulted)
                        {
                            var result = this.Generate(partition.TenantId);
                            if (result.Reason is { } reason)
                            {
                                this.LogSkipped(partition.TenantId, reason);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Order generator: next run at {Next}.")]
        private partial void LogScheduled(DateTimeOffset next);

        [LoggerMessage(Level = LogLevel.Information, Message = "Order generator, tenant {TenantId}: {Orders} order(s), {BrokerOrders} broker order(s); {Waiting} account(s) waiting for prices.")]
        private partial void LogGenerated(Guid tenantId, int orders, int brokerOrders, int waiting);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Order generator, tenant {TenantId}: nothing written: {Reason}")]
        private partial void LogSkipped(Guid tenantId, string reason);
    }
}
