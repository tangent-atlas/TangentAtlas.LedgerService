// <copyright file="OrderBook.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Orders
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using AtlasLedger.Server.Holdings;

    /// <summary>
    /// Where a broker order stands, from the broker's answers.
    /// </summary>
    public enum BrokerOrderStatus
    {
        /// <summary>Sent (or about to be), with no answer yet.</summary>
        Pending,

        /// <summary>Accepted, nothing filled.</summary>
        Working,

        /// <summary>Some filled, the rest still working.</summary>
        PartiallyFilled,

        /// <summary>Every leg filled.</summary>
        Filled,

        /// <summary>Cancelled: what filled stands.</summary>
        Cancelled,

        /// <summary>Refused by the broker or the router.</summary>
        Rejected,

        /// <summary>Expired.</summary>
        Expired,
    }

    /// <summary>
    /// Where an order stands.
    /// </summary>
    public enum OrderStatus
    {
        /// <summary>Nothing working at a broker, nothing filled.</summary>
        New,

        /// <summary>Working at a broker, nothing filled.</summary>
        Working,

        /// <summary>Some filled.</summary>
        PartiallyFilled,

        /// <summary>Every leg filled.</summary>
        Filled,

        /// <summary>Replaced by another order: what it filled stands, the rest is the replacement's.</summary>
        Replaced,

        /// <summary>Cancelled, with nothing left working: what it filled stands.</summary>
        Cancelled,
    }

    /// <summary>One leg of a broker order.</summary>
    /// <param name="LegId">The order's leg.</param>
    /// <param name="Quantity">How much this broker order carries.</param>
    /// <param name="Filled">How much has filled.</param>
    /// <param name="AveragePrice">The average fill price, if any filled.</param>
    /// <param name="Open">How much is still working.</param>
    public sealed record BrokerLegState(int LegId, decimal Quantity, decimal Filled, decimal? AveragePrice, decimal Open);

    /// <summary>A broker order's state.</summary>
    /// <param name="BrokerOrderId">The broker order.</param>
    /// <param name="AccountId">The account it trades.</param>
    /// <param name="Status">Where it stands.</param>
    /// <param name="Legs">Its legs.</param>
    /// <param name="Fees">The fees reported, as one amount.</param>
    public sealed record BrokerOrderState(Guid BrokerOrderId, Guid AccountId, BrokerOrderStatus Status, IReadOnlyList<BrokerLegState> Legs, decimal Fees)
    {
        /// <summary>Gets a value indicating whether it is still working.</summary>
        public bool IsOpen => this.Status is BrokerOrderStatus.Pending or BrokerOrderStatus.Working or BrokerOrderStatus.PartiallyFilled;
    }

    /// <summary>One account's share of a leg.</summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="Quantity">Its share.</param>
    /// <param name="Filled">How much of it has filled.</param>
    /// <param name="Remaining">How much hasn't.</param>
    /// <param name="Fees">Its share of the fees.</param>
    public sealed record AllocationState(Guid AccountId, decimal Quantity, decimal Filled, decimal Remaining, decimal Fees);

    /// <summary>One leg of an order.</summary>
    /// <param name="LegId">Its number.</param>
    /// <param name="AssetId">The asset.</param>
    /// <param name="Instruction">What it does.</param>
    /// <param name="Side">Buy or sell.</param>
    /// <param name="Quantity">The sum of its allocations.</param>
    /// <param name="Placed">How much is working or filled at brokers.</param>
    /// <param name="Unplaced">How much isn't.</param>
    /// <param name="Filled">How much has filled.</param>
    /// <param name="Unallocated">Fills beyond what the allocations need.</param>
    /// <param name="Fees">Its fees.</param>
    /// <param name="Allocations">Each account's share.</param>
    public sealed record LegState(int LegId, Guid AssetId, Instruction Instruction, OrderSide Side, decimal Quantity, decimal Placed, decimal Unplaced, decimal Filled, decimal Unallocated, decimal Fees, IReadOnlyList<AllocationState> Allocations);

    /// <summary>An order's state, derived from its events.</summary>
    /// <param name="OrderId">The order.</param>
    /// <param name="ChainId">The first order of its replace chain.</param>
    /// <param name="Replaces">The order it replaced, if any.</param>
    /// <param name="ReplacedBy">The order that replaced it, if any.</param>
    /// <param name="Origin">What created it.</param>
    /// <param name="Link">How it is linked to other orders.</param>
    /// <param name="Status">Where it stands.</param>
    /// <param name="IsCancelRequested">Whether a cancel of it was asked for.</param>
    /// <param name="Fees">Its fees.</param>
    /// <param name="Legs">Its legs.</param>
    /// <param name="BrokerOrders">Its broker orders.</param>
    public sealed record OrderState(
        Guid OrderId,
        Guid ChainId,
        Guid? Replaces,
        Guid? ReplacedBy,
        OrderOrigin Origin,
        OrderLink? Link,
        OrderStatus Status,
        bool IsCancelRequested,
        decimal Fees,
        IReadOnlyList<LegState> Legs,
        IReadOnlyList<BrokerOrderState> BrokerOrders)
    {
        /// <summary>Gets a value indicating whether anything of it is working at a broker.</summary>
        public bool IsWorking => this.BrokerOrders.Any(brokerOrder => brokerOrder.IsOpen);

        /// <summary>Gets a value indicating whether it is finished: filled, replaced or cancelled.</summary>
        public bool IsFinished => this.Status is OrderStatus.Filled or OrderStatus.Replaced or OrderStatus.Cancelled;
    }

    /// <summary>One account's share of one fill.</summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="Quantity">Its quantity.</param>
    /// <param name="Fees">Its fees.</param>
    public sealed record FillShare(Guid AccountId, decimal Quantity, decimal Fees);

    /// <summary>A fill divided among a leg's allocations.</summary>
    /// <param name="Fill">The fill.</param>
    /// <param name="Shares">Each account's share.</param>
    /// <param name="Unallocated">What no allocation needed.</param>
    public sealed record AllocatedFill(ExecutionReportedEvent Fill, IReadOnlyList<FillShare> Shares, decimal Unallocated);

    /// <summary>
    /// The order system's arithmetic: an order's state from its events, and how fills divide among accounts and post to
    /// their holdings.  Pure functions of the events, so the same events always give the same answer.
    /// </summary>
    public static class OrderBook
    {
        /// <summary>
        /// The side a leg trades on.
        /// </summary>
        /// <param name="instruction">The leg's instruction.</param>
        /// <returns>Buy or sell.</returns>
        public static OrderSide SideOf(Instruction instruction) =>
            instruction is Instruction.Buy or Instruction.BuyToCover or Instruction.BuyToOpen or Instruction.BuyToClose ? OrderSide.Buy : OrderSide.Sell;

        /// <summary>
        /// The order events are processed in: the order the ledger committed them.
        /// </summary>
        /// <param name="value">An event.</param>
        /// <returns>The sort key.</returns>
        public static (long Sequence, Guid EventId) ProcessingOrder(LedgerEvent value)
        {
            ArgumentNullException.ThrowIfNull(value);
            return (value.Envelope.Sequence, value.EventId);
        }

        /// <summary>
        /// Divides a leg's fills among its allocations, by a published deterministic rule: fills in the order recorded; each
        /// in proportion to what each allocation still needs, in whole units rounded down; leftover units one at a time to the
        /// largest fractions, then the lower account ID; a fractional leftover to whoever needs the most; no allocation more
        /// than it needs (the excess is unallocated); fees following the quantities to the cent, the remainder to the largest
        /// share.
        /// </summary>
        /// <param name="allocations">The leg's allocations.</param>
        /// <param name="fills">The leg's fills.</param>
        /// <returns>Each fill, divided.</returns>
        public static IReadOnlyList<AllocatedFill> AllocateFills(IEnumerable<AllocationItem> allocations, IEnumerable<ExecutionReportedEvent> fills)
        {
            ArgumentNullException.ThrowIfNull(allocations);
            ArgumentNullException.ThrowIfNull(fills);
            var remaining = allocations
                .GroupBy(allocation => allocation.AccountId)
                .OrderBy(group => group.Key)
                .ToDictionary(group => group.Key, group => Math.Max(group.Sum(allocation => allocation.Quantity), 0m));
            var result = new List<AllocatedFill>();
            foreach (var fill in fills.OrderBy(ProcessingOrder))
            {
                var quantity = Math.Max(fill.Quantity, 0m);
                var split = Split(quantity, remaining);
                foreach (var (accountId, share) in split)
                {
                    remaining[accountId] -= share;
                }

                result.Add(new AllocatedFill(fill, SplitFees(fill.Fees, split), quantity - split.Sum(item => item.Quantity)));
            }

            return result;
        }

        /// <summary>
        /// An order's state from its events.
        /// </summary>
        /// <param name="order">The order.</param>
        /// <param name="events">Its other events: broker orders sent, the broker's answers, cancel requests.</param>
        /// <param name="replacedBy">The order that replaced it, if any.</param>
        /// <returns>Its state.</returns>
        public static OrderState State(OrderPlacedEvent order, IEnumerable<OrderEvent> events, OrderReplacedEvent? replacedBy)
        {
            ArgumentNullException.ThrowIfNull(order);
            ArgumentNullException.ThrowIfNull(events);
            var list = events.Where(item => item.OrderId == order.OrderId).OrderBy(ProcessingOrder).ToArray();
            var answers = list.OfType<BrokerAnswerEvent>().ToLookup(answer => answer.BrokerOrderId);
            var brokerStates = list.OfType<BrokerOrderSentEvent>()
                .Select(sent => BrokerState(sent, answers[sent.BrokerOrderId]))
                .ToArray();
            var fills = list.OfType<ExecutionReportedEvent>().ToArray();
            var legs = order.Legs.OrderBy(leg => leg.LegId).Select(leg =>
            {
                var allocated = AllocateFills(leg.Allocations, fills.Where(fill => fill.LegId == leg.LegId));
                var byAccount = allocated
                    .SelectMany(fill => fill.Shares)
                    .GroupBy(share => share.AccountId)
                    .ToDictionary(group => group.Key, group => (Quantity: group.Sum(share => share.Quantity), Fees: group.Sum(share => share.Fees)));
                var allocations = leg.Allocations.GroupBy(allocation => allocation.AccountId).OrderBy(group => group.Key).Select(group =>
                {
                    var quantity = group.Sum(allocation => allocation.Quantity);
                    var (filled, fees) = byAccount.GetValueOrDefault(group.Key);
                    return new AllocationState(group.Key, quantity, filled, quantity - filled, fees);
                }).ToArray();
                var brokerLegs = brokerStates.SelectMany(state => state.Legs.Where(brokerLeg => brokerLeg.LegId == leg.LegId)).ToArray();
                var filledTotal = brokerLegs.Sum(brokerLeg => brokerLeg.Filled);
                var placed = brokerLegs.Sum(brokerLeg => brokerLeg.Open) + filledTotal;
                return new LegState(
                    leg.LegId,
                    leg.AssetId,
                    leg.Instruction,
                    SideOf(leg.Instruction),
                    leg.Quantity,
                    placed,
                    Math.Max(leg.Quantity - placed, 0m),
                    filledTotal,
                    allocated.Sum(fill => fill.Unallocated),
                    allocated.SelectMany(fill => fill.Shares).Sum(share => share.Fees),
                    allocations);
            }).ToArray();

            var isCancelRequested = list.OfType<CancelRequestedEvent>().Any(cancel => cancel.BrokerOrderId is null);
            var anyFilled = legs.Any(leg => leg.Filled > 0m);
            var allFilled = legs.Length > 0 && legs.All(leg => leg.Quantity > 0m && leg.Filled >= leg.Quantity);
            var isWorking = brokerStates.Any(state => state.IsOpen);
            var status = replacedBy is not null ? OrderStatus.Replaced
                : allFilled ? OrderStatus.Filled
                : isCancelRequested && !isWorking ? OrderStatus.Cancelled
                : anyFilled ? OrderStatus.PartiallyFilled
                : isWorking ? OrderStatus.Working
                : OrderStatus.New;
            return new OrderState(
                order.OrderId,
                order.Envelope.CorrelationId ?? order.OrderId,
                (order as OrderReplacedEvent)?.Replaces,
                replacedBy?.OrderId,
                order.Origin,
                order.Link,
                status,
                isCancelRequested,
                brokerStates.Sum(state => state.Fees),
                legs,
                brokerStates);
        }

        /// <summary>
        /// A broker order's state from the broker's answers.
        /// </summary>
        /// <param name="sent">The broker order.</param>
        /// <param name="answers">The broker's answers about it.</param>
        /// <returns>Its state.</returns>
        public static BrokerOrderState BrokerState(BrokerOrderSentEvent sent, IEnumerable<BrokerAnswerEvent> answers)
        {
            ArgumentNullException.ThrowIfNull(sent);
            ArgumentNullException.ThrowIfNull(answers);
            var list = answers.OrderBy(ProcessingOrder).ToArray();
            var fills = list.OfType<ExecutionReportedEvent>().Where(fill => fill.Quantity > 0m).ToArray();
            var ended = list.LastOrDefault(answer => answer is OrderCancelledEvent or OrderRejectedEvent or OrderExpiredEvent);
            var legs = sent.Legs.Select(leg =>
            {
                var legFills = fills.Where(fill => fill.LegId == leg.LegId).ToArray();
                var filled = legFills.Sum(fill => fill.Quantity);
                var value = legFills.Sum(fill => fill.Quantity * fill.Price);
                return (Leg: leg, Filled: filled, Value: value);
            }).ToArray();
            var allFilled = legs.All(leg => leg.Filled >= leg.Leg.Quantity);
            var status = ended switch
            {
                OrderCancelledEvent => BrokerOrderStatus.Cancelled,
                OrderRejectedEvent => BrokerOrderStatus.Rejected,
                OrderExpiredEvent => BrokerOrderStatus.Expired,
                _ when allFilled => BrokerOrderStatus.Filled,
                _ when legs.Any(leg => leg.Filled > 0m) => BrokerOrderStatus.PartiallyFilled,
                _ when list.OfType<OrderAcceptedEvent>().Any() => BrokerOrderStatus.Working,
                _ => BrokerOrderStatus.Pending,
            };
            var isOpen = status is BrokerOrderStatus.Pending or BrokerOrderStatus.Working or BrokerOrderStatus.PartiallyFilled;
            return new BrokerOrderState(
                sent.BrokerOrderId,
                sent.AccountId,
                status,
                legs.Select(leg => new BrokerLegState(leg.Leg.LegId, leg.Leg.Quantity, leg.Filled, leg.Filled > 0m ? leg.Value / leg.Filled : null, isOpen ? Math.Max(leg.Leg.Quantity - leg.Filled, 0m) : 0m)).ToArray(),
                fills.Sum(fill => fill.Fees));
        }

        /// <summary>
        /// What an order's fills post to its accounts' holdings: for each fill of each leg, each account's share as two
        /// movements, the asset and the cash, the fees inside the cash (a purchase's shares cost the price plus fees; a sale's
        /// proceeds are net of them).  Derived, never stored: the fills are the log's record.  A share for an account kept in
        /// another currency than the asset's quote currency isn't posted (the rules raise an alert).
        /// </summary>
        /// <param name="order">The order.</param>
        /// <param name="fills">Its fills.</param>
        /// <param name="assets">Its legs' assets.</param>
        /// <param name="cash">The cash assets, by currency.</param>
        /// <param name="accountCurrencies">Each account's currency.</param>
        /// <returns>The movements.</returns>
        public static IReadOnlyList<PostedLine> Lines(
            OrderPlacedEvent order,
            IEnumerable<ExecutionReportedEvent> fills,
            IReadOnlyDictionary<Guid, AssetFact> assets,
            IReadOnlyDictionary<string, CashFact> cash,
            IReadOnlyDictionary<Guid, string> accountCurrencies)
        {
            ArgumentNullException.ThrowIfNull(order);
            ArgumentNullException.ThrowIfNull(fills);
            ArgumentNullException.ThrowIfNull(assets);
            ArgumentNullException.ThrowIfNull(cash);
            ArgumentNullException.ThrowIfNull(accountCurrencies);
            var fillList = fills.Where(fill => fill.OrderId == order.OrderId).ToArray();
            var lines = new List<PostedLine>();
            foreach (var leg in order.Legs)
            {
                if (!assets.TryGetValue(leg.AssetId, out var asset) || !cash.TryGetValue(asset.QuoteCurrency, out var money))
                {
                    continue;
                }

                var isBuy = SideOf(leg.Instruction) == OrderSide.Buy;
                foreach (var allocated in AllocateFills(leg.Allocations, fillList.Where(fill => fill.LegId == leg.LegId)))
                {
                    var fill = allocated.Fill;
                    var number = 0;
                    foreach (var share in allocated.Shares.Where(share => accountCurrencies.GetValueOrDefault(share.AccountId) == asset.QuoteCurrency))
                    {
                        var gross = Math.Round(share.Quantity * asset.QuantityFactor * fill.Price * asset.PriceFactor, 2, MidpointRounding.AwayFromZero);
                        var cashAmount = isBuy ? -(gross + share.Fees) : gross - share.Fees;
                        var envelope = fill.Envelope;
                        lines.Add(new PostedLine(fill.EventId, ++number, share.AccountId, asset.AssetId, LineEffect.Change, isBuy ? share.Quantity : -share.Quantity, -cashAmount, EventType.Trade, fill.ReportTime, envelope.SourceSequence, envelope.Sequence));
                        lines.Add(new PostedLine(fill.EventId, ++number, share.AccountId, money.AssetId, LineEffect.Change, cashAmount, cashAmount, EventType.Trade, fill.ReportTime, envelope.SourceSequence, envelope.Sequence));
                    }
                }
            }

            return lines;
        }

        private static List<(Guid AccountId, decimal Quantity)> Split(decimal quantity, IReadOnlyDictionary<Guid, decimal> remaining)
        {
            var needs = remaining.Where(item => item.Value > 0m).OrderBy(item => item.Key).ToArray();
            var total = needs.Sum(item => item.Value);
            var take = Math.Min(quantity, total);
            if (take <= 0m)
            {
                return new List<(Guid, decimal)>();
            }

            var exact = needs.Select(item => (item.Key, Need: item.Value, Exact: take * item.Value / total)).ToArray();
            var shares = exact.ToDictionary(item => item.Key, item => Math.Min(Math.Floor(item.Exact), item.Need));
            var left = take - shares.Values.Sum();

            // Whole units left over: one each to the largest fractions, then the lower account ID.
            foreach (var item in exact.OrderByDescending(item => item.Exact - Math.Floor(item.Exact)).ThenBy(item => item.Key))
            {
                if (left < 1m)
                {
                    break;
                }

                if (shares[item.Key] + 1m <= item.Need)
                {
                    shares[item.Key] += 1m;
                    left -= 1m;
                }
            }

            // A fraction of a unit left (a fractional fill), or units no one could take above: to whoever needs the most.
            foreach (var item in exact.OrderByDescending(item => item.Need - shares[item.Key]).ThenBy(item => item.Key))
            {
                if (left <= 0m)
                {
                    break;
                }

                var room = item.Need - shares[item.Key];
                var add = Math.Min(room, left);
                shares[item.Key] += add;
                left -= add;
            }

            return shares.Where(item => item.Value > 0m).OrderBy(item => item.Key).Select(item => (item.Key, item.Value)).ToList();
        }

        private static IReadOnlyList<FillShare> SplitFees(decimal fees, IReadOnlyList<(Guid AccountId, decimal Quantity)> split)
        {
            var total = split.Sum(item => item.Quantity);
            if (total <= 0m)
            {
                return Array.Empty<FillShare>();
            }

            var each = split.Select(item => (item.AccountId, item.Quantity, Fees: Math.Round(fees * item.Quantity / total, 2, MidpointRounding.ToZero))).ToList();
            var largest = each.OrderByDescending(item => item.Quantity).ThenBy(item => item.AccountId).First().AccountId;
            var remainder = fees - each.Sum(item => item.Fees);
            return each.Select(item => new FillShare(item.AccountId, item.Quantity, item.AccountId == largest ? item.Fees + remainder : item.Fees)).ToArray();
        }
    }
}
