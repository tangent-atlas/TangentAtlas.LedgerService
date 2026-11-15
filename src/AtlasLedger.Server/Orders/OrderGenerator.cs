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
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using AtlasLedger.Server.Events;
    using AtlasLedger.Server.Holdings;
    using Bellerophon.Rules;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// What one run of the order generator did for a tenant.
    /// </summary>
    /// <param name="Orders">Orders written: one per asset and side, aggregating every account that wants it.</param>
    /// <param name="BrokerOrders">Broker orders written: one per account in each order.</param>
    /// <param name="Waiting">Accounts left out because a price was missing or stale.</param>
    /// <param name="Reason">Why nothing was written, when nothing could be.</param>
    public sealed record GeneratedOrders(int Orders, int BrokerOrders, int Waiting, string? Reason);

    /// <summary>
    /// Turns the rules' proposed allocations into orders: when a tenant's GenerateOrders job runs (its schedule, while the
    /// trading switch is on), or when an operator asks.  For each tenant it takes the proposals
    /// of the rules' latest snapshot and writes, in one transaction, an order per asset and side (aggregating every account
    /// that wants it), the allocations that went into it, and a market day broker order per account.  The order router
    /// sends them.  Everything is written as events: an OrderPlaced per asset and side, a BrokerOrderSent per account.
    /// </summary>
    /// <remarks>
    /// The proposals subtract open orders, so once these orders are seen by the rules, the next proposals no longer ask
    /// for them.  The one race is running again before the rules have seen the last run's orders; so a run works only from a
    /// snapshot of the rules that has applied the tenant's ledger through the last run's commit.
    /// </remarks>
    public sealed partial class OrderGenerator
    {
        private readonly Ledger ledger;

        private readonly RulesEngineHost host;

        private readonly HoldingsStore store;

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
        /// <param name="timeProvider">The clock.</param>
        /// <param name="logger">The log.</param>
        public OrderGenerator(Ledger ledger, RulesEngineHost host, HoldingsStore store, TimeProvider timeProvider, ILogger<OrderGenerator> logger)
        {
            this.ledger = ledger;
            this.host = host;
            this.store = store;
            this.timeProvider = timeProvider;
            this.logger = logger;
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
                var transactionId = Guid.CreateVersion7(now);
                using var transaction = partition.BeginTransaction();
                foreach (var group in trades)
                {
                    var orderId = Guid.CreateVersion7(now);
                    var accounts = group.OrderBy(item => item.AccountId).ToArray();
                    var instruction = group.Key.Side == OrderSide.Buy ? Instruction.Buy : Instruction.Sell;
                    var placed = new OrderPlacedEvent
                    {
                        OrderId = orderId,
                        Origin = OrderOrigin.Rebalance,
                        Legs = [new OrderLeg(1, group.Key.AssetId, instruction, accounts.Select(item => new AllocationItem(item.AccountId, item.Trade.Quantity)).ToArray())],
                    };
                    transaction.InsertEvent(EventRows.Row(placed, orderId, EventSource.Rules, orderId.ToString("N"), now, correlationId: orderId, transactionId: transactionId));
                    foreach (var (accountId, trade) in accounts)
                    {
                        var brokerOrderId = Guid.CreateVersion7(now);
                        var sent = new BrokerOrderSentEvent
                        {
                            OrderId = orderId,
                            BrokerOrderId = brokerOrderId,
                            AccountId = accountId,
                            Legs = [new BrokerLeg(1, trade.Quantity)],
                            OrderType = OrderType.Market,
                            TimeInForce = TimeInForce.Day,
                        };
                        transaction.InsertEvent(EventRows.Row(sent, brokerOrderId, EventSource.Rules, brokerOrderId.ToString("N"), now, correlationId: orderId, causationId: orderId, transactionId: transactionId));
                        brokerOrders++;
                    }
                }

                this.lastCommits[tenantId] = transaction.Commit();
                this.LogGenerated(tenantId, trades.Length, brokerOrders, waiting);
                return new GeneratedOrders(trades.Length, brokerOrders, waiting, null);
            }
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Order generator, tenant {TenantId}: {Orders} order(s), {BrokerOrders} broker order(s); {Waiting} account(s) waiting for prices.")]
        private partial void LogGenerated(Guid tenantId, int orders, int brokerOrders, int waiting);

    }
}
