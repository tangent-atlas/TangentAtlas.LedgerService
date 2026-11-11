// <copyright file="OrderBookTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias server;

namespace AtlasLedger.Tests
{
    using System;
    using System.Linq;
    using AtlasLedger.Model;
    using server::AtlasLedger.Model;
    using server::AtlasLedger.Model.Primary;
    using server::AtlasLedger.Server.Holdings;
    using server::AtlasLedger.Server.Orders;
    using Xunit;

    /// <summary>
    /// The order system's arithmetic: dividing fills among allocations, the states of orders and broker orders, and the
    /// transactions that book a fill.
    /// </summary>
    public sealed class OrderBookTests
    {
        private static readonly DateTimeOffset Close = new DateTimeOffset(2026, 11, 6, 21, 0, 0, TimeSpan.Zero);

        private static readonly Guid AccountA = Guid.Parse("00000000-0000-0000-0000-00000000000a");

        private static readonly Guid AccountB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

        private static readonly Guid AccountC = Guid.Parse("00000000-0000-0000-0000-00000000000c");

        private static readonly OrderFact Buy = new OrderFact(new OrderDto { OrderId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Side = OrderSide.Buy, Origin = OrderOrigin.Rebalance, EnteredTime = Close });

        private static long sequence;

        [Fact]
        public void FillsAreDividedInProportionToWhatEachAllocationStillNeeds()
        {
            // 60 for A and 40 for B, filled 30, 30 and 40: each fill 3 to 2, and both end exactly filled.
            var allocations = new[] { Allocation(AccountA, 60m), Allocation(AccountB, 40m) };
            var fills = new[] { Fill(30m), Fill(30m), Fill(40m) };
            var divided = OrderBook.AllocateFills(allocations, fills);

            Assert.Equal([(AccountA, 18m), (AccountB, 12m)], divided[0].Shares.Select(share => (share.AccountId, share.Quantity)));
            Assert.Equal([(AccountA, 18m), (AccountB, 12m)], divided[1].Shares.Select(share => (share.AccountId, share.Quantity)));
            Assert.Equal([(AccountA, 24m), (AccountB, 16m)], divided[2].Shares.Select(share => (share.AccountId, share.Quantity)));
            Assert.All(divided, fill => Assert.Equal(0m, fill.Unallocated));
        }

        [Fact]
        public void LeftoverUnitsGoToTheLargestFractionsThenTheLowerAccount()
        {
            // 10 each for three accounts, filled 10: 3.33 each, so 3 each and the one left over to A (all fractions equal).
            var allocations = new[] { Allocation(AccountC, 10m), Allocation(AccountB, 10m), Allocation(AccountA, 10m) };
            var divided = OrderBook.AllocateFills(allocations, [Fill(10m)]);
            Assert.Equal([(AccountA, 4m), (AccountB, 3m), (AccountC, 3m)], divided[0].Shares.Select(share => (share.AccountId, share.Quantity)));

            // Whole units only, and the same answer whatever order the allocations and fills come in.
            var shuffled = OrderBook.AllocateFills(allocations.Reverse(), [Fill(10m)]);
            Assert.Equal(divided[0].Shares.Select(share => (share.AccountId, share.Quantity)), shuffled[0].Shares.Select(share => (share.AccountId, share.Quantity)));
        }

        [Fact]
        public void AFillBeyondWhatTheAllocationsNeedIsLeftUnallocated()
        {
            var divided = OrderBook.AllocateFills([Allocation(AccountA, 5m)], [Fill(3m), Fill(4m)]);
            Assert.Equal(3m, divided[0].Shares.Single().Quantity);
            Assert.Equal(2m, divided[1].Shares.Single().Quantity);
            Assert.Equal(2m, divided[1].Unallocated);
        }

        [Fact]
        public void FeesFollowTheQuantitiesToTheCent()
        {
            var divided = OrderBook.AllocateFills([Allocation(AccountA, 2m), Allocation(AccountB, 1m)], [Fill(3m, fees: 1m)]);
            Assert.Equal([(AccountA, 0.67m), (AccountB, 0.33m)], divided[0].Shares.Select(share => (share.AccountId, share.Fees)));
            Assert.Equal(1m, divided[0].Shares.Sum(share => share.Fees));
        }

        [Fact]
        public void AFillIsBookedAsABalancedExchangeWithTheFeesInIt()
        {
            var asset = new AssetFact(new AssetDto { AssetId = Buy.AssetId, AssetType = AssetType.Equity, QuoteCurrency = CurrencyCode.UnitedStatesDollar, QuantityFactor = 1m, PriceFactor = 1m });
            var cash = new CashFact(new CashDto { AssetId = Guid.NewGuid(), CashType = CashType.Currency, CurrencyCode = CurrencyCode.UnitedStatesDollar });
            var fill = Fill(10m, price: 50m, fees: 1m);

            // A purchase: 10 shares cost $501, and $501 leaves.
            var (transaction, received, given) = OrderBook.Booking(Buy, asset, cash, fill, new FillShare(AccountA, 10m, 1m));
            Assert.Equal(TransactionKind.Buy, transaction.Kind);
            Assert.Equal((10m, 501m), (received.Quantity, received.Amount!.Value));
            Assert.Equal((-501m, -501m), (given.Quantity, given.Amount!.Value));
            Assert.True(HoldingFold.IsBalanced([Line(received), Line(given)]));
            Assert.Equal(OrderBook.BookingId(fill.ExecutionReportId, AccountA), transaction.TransactionId);   // the same fill and account, the same key

            // A sale: 10 shares leave, and $499 arrives.
            var sell = new OrderFact(new OrderDto { OrderId = Guid.NewGuid(), AssetId = Buy.AssetId, Side = OrderSide.Sell, Origin = OrderOrigin.Manual, EnteredTime = Close });
            var (sale, gave, got) = OrderBook.Booking(sell, asset, cash, fill, new FillShare(AccountA, 10m, 1m));
            Assert.Equal(TransactionKind.Sell, sale.Kind);
            Assert.Equal((-10m, -499m), (gave.Quantity, gave.Amount!.Value));
            Assert.Equal((499m, 499m), (got.Quantity, got.Amount!.Value));
            Assert.True(HoldingFold.IsBalanced([Line(gave), Line(got)]));
        }

        [Fact]
        public void TheStatesFollowTheReports()
        {
            var first = BrokerOrder(60m);
            var second = BrokerOrder(40m);
            var allocations = new[] { Allocation(AccountA, 100m) };
            var accepted = Report(first, ExecutionReportType.Accepted);

            Assert.Equal(OrderStatus.New, OrderBook.State(Buy, allocations, [], []).Status);
            var pending = OrderBook.State(Buy, allocations, [first], []);
            Assert.Equal((OrderStatus.Working, 60m, 40m), (pending.Status, pending.Placed, pending.Unplaced));
            Assert.Equal(BrokerOrderStatus.Pending, pending.BrokerOrders.Single().Status);
            Assert.Equal(BrokerOrderStatus.Working, OrderBook.State(Buy, allocations, [first], [accepted]).BrokerOrders.Single().Status);

            // Part filled, then the second broker order rejected: 25 filled, 35 still working, 40 back to unplaced.
            var partly = OrderBook.State(Buy, allocations, [first, second], [accepted, Fill(25m, price: 10m, brokerOrder: first), Report(second, ExecutionReportType.Rejected)]);
            Assert.Equal((OrderStatus.PartiallyFilled, 25m, 60m, 40m), (partly.Status, partly.Filled, partly.Placed, partly.Unplaced));
            Assert.Equal([BrokerOrderStatus.PartiallyFilled, BrokerOrderStatus.Rejected], partly.BrokerOrders.Select(state => state.Status));
            Assert.Equal((25m, 75m), (partly.Allocations.Single().Filled, partly.Allocations.Single().Remaining));

            // The rest of the first filled at another price, then cancelled what was left: nothing is working.
            var done = OrderBook.State(Buy, allocations, [first, second], [accepted, Fill(25m, price: 10m, brokerOrder: first), Fill(35m, price: 12m, brokerOrder: first), Report(second, ExecutionReportType.Rejected)]);
            var state = done.BrokerOrders.First();
            Assert.Equal((BrokerOrderStatus.Filled, 60m, 0m), (state.Status, state.Filled, state.Open));
            Assert.Equal(((25m * 10m) + (35m * 12m)) / 60m, state.AveragePrice);
            Assert.Equal((OrderStatus.PartiallyFilled, 60m, 40m), (done.Status, done.Filled, done.Unplaced));
        }

        private static AllocationFact Allocation(Guid accountId, decimal quantity) =>
            new AllocationFact(new AllocationDto { OrderId = Buy.OrderId, AccountId = accountId, Quantity = quantity });

        private static BrokerOrderFact BrokerOrder(decimal quantity) =>
            new BrokerOrderFact(new BrokerOrderDto { BrokerOrderId = Guid.NewGuid(), OrderId = Buy.OrderId, Broker = EventSource.Simulator, Quantity = quantity, OrderType = OrderType.Market, TimeInForce = TimeInForce.Day, EnteredTime = Close, RowVersion = Next() });

        private static ExecutionReportFact Fill(decimal quantity, decimal price = 100m, decimal fees = 0m, BrokerOrderFact? brokerOrder = null) =>
            new ExecutionReportFact(new ExecutionReportDto
            {
                ExecutionReportId = Guid.NewGuid(), BrokerOrderId = brokerOrder?.BrokerOrderId ?? Guid.Empty, ReportType = ExecutionReportType.Fill,
                Quantity = quantity, Price = price, Fees = fees, ReportTime = Close, Source = EventSource.Simulator, SourceId = Guid.NewGuid().ToString("N"), RowVersion = Next(),
            });

        private static ExecutionReportFact Report(BrokerOrderFact brokerOrder, ExecutionReportType type) =>
            new ExecutionReportFact(new ExecutionReportDto
            {
                ExecutionReportId = Guid.NewGuid(), BrokerOrderId = brokerOrder.BrokerOrderId, ReportType = type, ReportTime = Close,
                Source = EventSource.Simulator, SourceId = Guid.NewGuid().ToString("N"), RowVersion = Next(),
            });

        private static TransactionLineFact Line(TransactionLine line) =>
            new TransactionLineFact(new TransactionLineDto { TransactionId = line.TransactionId, LineNumber = line.LineNumber, AccountId = line.AccountId, AssetId = line.AssetId, Effect = line.Effect, Quantity = line.Quantity, Amount = line.Amount });

        private static long Next() => System.Threading.Interlocked.Increment(ref sequence);
    }
}
