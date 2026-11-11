// <copyright file="OrderBook.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Orders
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;

    /// <summary>
    /// Where a broker order stands, from the broker's execution reports.
    /// </summary>
    public enum BrokerOrderStatus
    {
        /// <summary>Recorded; no report from the broker yet.</summary>
        Pending,

        /// <summary>Accepted by the broker; nothing filled yet.</summary>
        Working,

        /// <summary>Some filled; the rest still working.</summary>
        PartiallyFilled,

        /// <summary>All filled.</summary>
        Filled,

        /// <summary>Cancelled; what was filled stays filled.</summary>
        Cancelled,

        /// <summary>Refused by the broker.</summary>
        Rejected,

        /// <summary>Its time ran out; what was filled stays filled.</summary>
        Expired,
    }

    /// <summary>
    /// Where an order stands, across its broker orders.
    /// </summary>
    public enum OrderStatus
    {
        /// <summary>Nothing working and nothing filled.</summary>
        New,

        /// <summary>Something working at the broker; nothing filled yet.</summary>
        Working,

        /// <summary>Some filled, not all.</summary>
        PartiallyFilled,

        /// <summary>All filled.</summary>
        Filled,
    }

    /// <summary>
    /// A broker order's state.  Derived from its execution reports; never persisted.
    /// </summary>
    /// <param name="BrokerOrderId">The broker order.</param>
    /// <param name="Quantity">The quantity sent.</param>
    /// <param name="Status">Where it stands.</param>
    /// <param name="Filled">The quantity filled.</param>
    /// <param name="AveragePrice">The average fill price, if anything filled.</param>
    /// <param name="Fees">The fees reported.</param>
    /// <param name="Open">The quantity still working at the broker (zero once filled, cancelled, rejected or expired).</param>
    public sealed record BrokerOrderState(Guid BrokerOrderId, decimal Quantity, BrokerOrderStatus Status, decimal Filled, decimal? AveragePrice, decimal Fees, decimal Open);

    /// <summary>
    /// An allocation's state: how much of its quantity has been booked to the account.
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="Quantity">The quantity allocated.</param>
    /// <param name="Filled">The quantity filled and booked to the account.</param>
    /// <param name="Remaining">The quantity still to fill.</param>
    /// <param name="Fees">The fees booked to the account: one amount, whatever the broker's fees are made of.</param>
    public sealed record AllocationState(Guid AccountId, decimal Quantity, decimal Filled, decimal Remaining, decimal Fees);

    /// <summary>
    /// An order's state: its allocations, its broker orders and what has been filled.  Derived; never persisted.
    /// </summary>
    /// <param name="OrderId">The order.</param>
    /// <param name="AssetId">The asset.</param>
    /// <param name="Side">Buy or sell.</param>
    /// <param name="Origin">What created it.</param>
    /// <param name="Quantity">The sum of its allocations.</param>
    /// <param name="Placed">The quantity at the broker: working, or filled.</param>
    /// <param name="Unplaced">The quantity not yet sent to a broker (or sent and cancelled, rejected or expired unfilled).</param>
    /// <param name="Filled">The quantity filled.</param>
    /// <param name="Unallocated">Filled beyond what the allocations needed (the broker filled more than was asked): not booked to any account.</param>
    /// <param name="Fees">The fees reported across its broker orders: one amount.</param>
    /// <param name="Status">Where it stands.</param>
    /// <param name="Allocations">Its allocations' states, by account.</param>
    /// <param name="BrokerOrders">Its broker orders' states, in the order they were recorded.</param>
    public sealed record OrderState(
        Guid OrderId,
        Guid AssetId,
        OrderSide Side,
        OrderOrigin Origin,
        decimal Quantity,
        decimal Placed,
        decimal Unplaced,
        decimal Filled,
        decimal Unallocated,
        decimal Fees,
        OrderStatus Status,
        IReadOnlyList<AllocationState> Allocations,
        IReadOnlyList<BrokerOrderState> BrokerOrders);

    /// <summary>
    /// One account's share of one fill.
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="Quantity">Its share of the fill's quantity.</param>
    /// <param name="Fees">Its share of the fill's fees.</param>
    public sealed record FillShare(Guid AccountId, decimal Quantity, decimal Fees);

    /// <summary>
    /// A fill divided among an order's allocations.
    /// </summary>
    /// <param name="Fill">The execution report.</param>
    /// <param name="Shares">Each account's share (accounts that got nothing are left out).</param>
    /// <param name="Unallocated">What the allocations no longer needed.</param>
    public sealed record AllocatedFill(ExecutionReportFact Fill, IReadOnlyList<FillShare> Shares, decimal Unallocated);

    /// <summary>
    /// The order system's arithmetic, as plain functions: the rules call them, the tests call them directly.  Every result
    /// depends only on the facts given, in the order the ledger recorded them, so a restart reaches the same answers.
    /// </summary>
    public static class OrderBook
    {
        /// <summary>
        /// The order in which reports are processed: as the ledger recorded them (row versions are fixed at insert, since
        /// reports are append-only), so a report that arrives late never changes how earlier fills were divided.
        /// </summary>
        /// <param name="report">A report.</param>
        /// <returns>The sort key.</returns>
        public static (long RowVersion, Guid ExecutionReportId) ProcessingOrder(ExecutionReportFact report)
        {
            ArgumentNullException.ThrowIfNull(report);
            return (report.RowVersion, report.ExecutionReportId);
        }

        /// <summary>
        /// Divides an order's fills among its allocations, by the published rule: each fill in the order recorded, in
        /// proportion to what each allocation still needs, in whole units rounded down; the units left over go one at a
        /// time to the largest fractions (ties: the lower account ID); a fraction of a unit left (a fractional fill) goes to
        /// the allocation needing the most.  No allocation gets more than it needs; a fill beyond what all of them need is
        /// left unallocated.  Fees follow the quantities, to the cent, the remainder to the largest share.
        /// </summary>
        /// <param name="allocations">The order's allocations.</param>
        /// <param name="fills">The order's fills, in any order.</param>
        /// <returns>Each fill, divided, in processing order.</returns>
        public static IReadOnlyList<AllocatedFill> AllocateFills(IEnumerable<AllocationFact> allocations, IEnumerable<ExecutionReportFact> fills)
        {
            ArgumentNullException.ThrowIfNull(allocations);
            ArgumentNullException.ThrowIfNull(fills);
            var remaining = allocations.OrderBy(allocation => allocation.AccountId).ToDictionary(allocation => allocation.AccountId, allocation => Math.Max(allocation.Quantity, 0m));
            var result = new List<AllocatedFill>();
            foreach (var fill in fills.Where(report => report.ReportType == ExecutionReportType.Fill).OrderBy(ProcessingOrder))
            {
                var quantity = Math.Max(fill.Quantity ?? 0m, 0m);
                var split = Split(quantity, remaining);
                foreach (var (accountId, share) in split)
                {
                    remaining[accountId] -= share;
                }

                var shares = SplitFees(fill.Fees ?? 0m, split);
                result.Add(new AllocatedFill(fill, shares, quantity - split.Sum(item => item.Quantity)));
            }

            return result;
        }

        /// <summary>
        /// Works out an order's state from its allocations, broker orders and their reports.
        /// </summary>
        /// <param name="order">The order.</param>
        /// <param name="allocations">Its allocations.</param>
        /// <param name="brokerOrders">Its broker orders.</param>
        /// <param name="reports">Their execution reports.</param>
        /// <returns>The state.</returns>
        public static OrderState State(OrderFact order, IEnumerable<AllocationFact> allocations, IEnumerable<BrokerOrderFact> brokerOrders, IEnumerable<ExecutionReportFact> reports)
        {
            ArgumentNullException.ThrowIfNull(order);
            var allocationList = allocations.OrderBy(allocation => allocation.AccountId).ToArray();
            var reportList = reports.ToArray();
            var brokerStates = brokerOrders
                .OrderBy(brokerOrder => (brokerOrder.RowVersion, brokerOrder.BrokerOrderId))
                .Select(brokerOrder => BrokerState(brokerOrder, reportList.Where(report => report.BrokerOrderId == brokerOrder.BrokerOrderId)))
                .ToArray();
            var allocated = AllocateFills(allocationList, reportList);
            var byAccount = allocated
                .SelectMany(fill => fill.Shares)
                .GroupBy(share => share.AccountId)
                .ToDictionary(group => group.Key, group => (Quantity: group.Sum(share => share.Quantity), Fees: group.Sum(share => share.Fees)));
            var allocationStates = allocationList
                .Select(allocation =>
                {
                    var (filled, fees) = byAccount.GetValueOrDefault(allocation.AccountId);
                    return new AllocationState(allocation.AccountId, allocation.Quantity, filled, allocation.Quantity - filled, fees);
                })
                .ToArray();

            var quantity = allocationList.Sum(allocation => allocation.Quantity);
            var filledTotal = brokerStates.Sum(state => state.Filled);
            var open = brokerStates.Sum(state => state.Open);
            var placed = open + filledTotal;
            var status = filledTotal >= quantity && quantity > 0m ? OrderStatus.Filled
                : filledTotal > 0m ? OrderStatus.PartiallyFilled
                : open > 0m ? OrderStatus.Working
                : OrderStatus.New;
            return new OrderState(
                order.OrderId,
                order.AssetId,
                order.Side,
                order.Origin,
                quantity,
                placed,
                Math.Max(quantity - placed, 0m),
                filledTotal,
                allocated.Sum(fill => fill.Unallocated),
                brokerStates.Sum(state => state.Fees),
                status,
                allocationStates,
                brokerStates);
        }

        /// <summary>
        /// Works out a broker order's state from its reports.
        /// </summary>
        /// <param name="brokerOrder">The broker order.</param>
        /// <param name="reports">Its reports, in any order.</param>
        /// <returns>The state.</returns>
        public static BrokerOrderState BrokerState(BrokerOrderFact brokerOrder, IEnumerable<ExecutionReportFact> reports)
        {
            ArgumentNullException.ThrowIfNull(brokerOrder);
            var list = reports.OrderBy(ProcessingOrder).ToArray();
            var fills = list.Where(report => report.ReportType == ExecutionReportType.Fill && report.Quantity is > 0m).ToArray();
            var filled = fills.Sum(fill => fill.Quantity!.Value);
            var value = fills.Sum(fill => fill.Quantity!.Value * (fill.Price ?? 0m));
            var fees = list.Sum(report => report.Fees ?? 0m);
            var ended = list.LastOrDefault(report => report.ReportType is ExecutionReportType.Cancelled or ExecutionReportType.Rejected or ExecutionReportType.Expired);
            var status = ended is not null ? ended.ReportType switch
                {
                    ExecutionReportType.Cancelled => BrokerOrderStatus.Cancelled,
                    ExecutionReportType.Rejected => BrokerOrderStatus.Rejected,
                    _ => BrokerOrderStatus.Expired,
                }
                : filled >= brokerOrder.Quantity ? BrokerOrderStatus.Filled
                : filled > 0m ? BrokerOrderStatus.PartiallyFilled
                : list.Any(report => report.ReportType == ExecutionReportType.Accepted) ? BrokerOrderStatus.Working
                : BrokerOrderStatus.Pending;
            var isOpen = status is BrokerOrderStatus.Pending or BrokerOrderStatus.Working or BrokerOrderStatus.PartiallyFilled;
            return new BrokerOrderState(
                brokerOrder.BrokerOrderId,
                brokerOrder.Quantity,
                status,
                filled,
                filled > 0m ? value / filled : null,
                fees,
                isOpen ? Math.Max(brokerOrder.Quantity - filled, 0m) : 0m);
        }

        /// <summary>
        /// The Buy or Sell transaction that books one account's share of a fill: an exchange of the asset for cash in the
        /// asset's quote currency, in two balanced lines.  The fees go into the exchange: a purchase's shares cost what
        /// was paid for them plus fees; a sale's proceeds are what was received less fees.  The transaction also carries
        /// the fees as one amount (<see cref="Transaction.Fees"/>), whatever the broker's fees are made of.
        /// </summary>
        /// <param name="order">The order.</param>
        /// <param name="asset">The order's asset.</param>
        /// <param name="cash">The cash asset of the asset's quote currency.</param>
        /// <param name="fill">The fill.</param>
        /// <param name="share">The account's share.</param>
        /// <returns>The transaction and its two lines.</returns>
        public static (Transaction Transaction, TransactionLine Received, TransactionLine Given) Booking(OrderFact order, AssetFact asset, CashFact cash, ExecutionReportFact fill, FillShare share)
        {
            ArgumentNullException.ThrowIfNull(order);
            ArgumentNullException.ThrowIfNull(asset);
            ArgumentNullException.ThrowIfNull(cash);
            ArgumentNullException.ThrowIfNull(fill);
            ArgumentNullException.ThrowIfNull(share);
            var transactionId = BookingId(fill.ExecutionReportId, share.AccountId);
            var gross = Math.Round(share.Quantity * asset.QuantityFactor * (fill.Price ?? 0m) * asset.PriceFactor, 2, MidpointRounding.AwayFromZero);
            var isBuy = order.Side == OrderSide.Buy;
            var cashAmount = isBuy ? -(gross + share.Fees) : gross - share.Fees;
            var transaction = new Transaction
            {
                TransactionId = transactionId,
                Kind = isBuy ? TransactionKind.Buy : TransactionKind.Sell,
                Source = EventSource.Rules,
                SourceId = transactionId.ToString("N"),
                EffectiveTime = fill.ReportTime,
                EnteredTime = fill.ReportTime,
                OrderId = order.OrderId,
                ExecutionReportId = fill.ExecutionReportId,
                Fees = share.Fees,
            };
            var assetLine = new TransactionLine
            {
                TransactionId = transactionId,
                LineNumber = 1,
                AccountId = share.AccountId,
                AssetId = asset.AssetId,
                Effect = LineEffect.Change,
                Quantity = isBuy ? share.Quantity : -share.Quantity,
                Amount = -cashAmount,
            };
            var cashLine = new TransactionLine
            {
                TransactionId = transactionId,
                LineNumber = 2,
                AccountId = share.AccountId,
                AssetId = cash.AssetId,
                Effect = LineEffect.Change,
                Quantity = cashAmount,
                Amount = cashAmount,
            };
            return (transaction, assetLine, cashLine);
        }

        /// <summary>
        /// The key of the transaction that books an account's share of a fill: the same fill and account always give the
        /// same key (a name-based UUID, version 8, SHA-256), so a cycle that books it again books nothing new.
        /// </summary>
        /// <param name="executionReportId">The fill.</param>
        /// <param name="accountId">The account.</param>
        /// <returns>The transaction's key.</returns>
        public static Guid BookingId(Guid executionReportId, Guid accountId)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"tangent-atlas/booking/{executionReportId:N}/{accountId:N}"));
            hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
            return new Guid(hash.AsSpan(0, 16), bigEndian: true);
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
