// <copyright file="OrderBookTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias server;

namespace AtlasLedger.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using AtlasLedger.Model;
    using server::AtlasLedger.Model;
    using server::AtlasLedger.Model.Primary;
    using server::AtlasLedger.Server.Holdings;
    using server::AtlasLedger.Server.Orders;
    using Xunit;

    /// <summary>
    /// The order system's arithmetic, from events: dividing fills among allocations, the states of orders and broker orders
    /// (legs, replacements, cancels), and what fills post to holdings.
    /// </summary>
    public sealed class OrderBookTests
    {
        private static readonly DateTimeOffset Close = new DateTimeOffset(2026, 11, 6, 21, 0, 0, TimeSpan.Zero);

        private static readonly Guid AccountA = Guid.Parse("00000000-0000-0000-0000-00000000000a");

        private static readonly Guid AccountB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

        private static readonly Guid AccountC = Guid.Parse("00000000-0000-0000-0000-00000000000c");

        private static readonly Guid Stock = Guid.NewGuid();

        private static readonly Guid Option = Guid.NewGuid();

        private static long sequence;

        [Fact]
        public void FillsAreDividedInProportionToWhatEachAllocationStillNeeds()
        {
            // 60 for A and 40 for B, filled 30, 30 and 40: each fill 3 to 2, and both end exactly filled.
            var allocations = new[] { new AllocationItem(AccountA, 60m), new AllocationItem(AccountB, 40m) };
            var divided = OrderBook.AllocateFills(allocations, [Fill(Guid.Empty, Guid.Empty, 30m), Fill(Guid.Empty, Guid.Empty, 30m), Fill(Guid.Empty, Guid.Empty, 40m)]);

            Assert.Equal([(AccountA, 18m), (AccountB, 12m)], divided[0].Shares.Select(share => (share.AccountId, share.Quantity)));
            Assert.Equal([(AccountA, 18m), (AccountB, 12m)], divided[1].Shares.Select(share => (share.AccountId, share.Quantity)));
            Assert.Equal([(AccountA, 24m), (AccountB, 16m)], divided[2].Shares.Select(share => (share.AccountId, share.Quantity)));
            Assert.All(divided, fill => Assert.Equal(0m, fill.Unallocated));
        }

        [Fact]
        public void LeftoverUnitsGoToTheLargestFractionsThenTheLowerAccount()
        {
            // 10 each for three accounts, filled 10: 3.33 each, so 3 each and the one left over to A (all fractions equal).
            var allocations = new[] { new AllocationItem(AccountC, 10m), new AllocationItem(AccountB, 10m), new AllocationItem(AccountA, 10m) };
            var divided = OrderBook.AllocateFills(allocations, [Fill(Guid.Empty, Guid.Empty, 10m)]);
            Assert.Equal([(AccountA, 4m), (AccountB, 3m), (AccountC, 3m)], divided[0].Shares.Select(share => (share.AccountId, share.Quantity)));

            // The same answer whatever order the allocations come in.
            var shuffled = OrderBook.AllocateFills(allocations.Reverse(), [Fill(Guid.Empty, Guid.Empty, 10m)]);
            Assert.Equal(divided[0].Shares.Select(share => (share.AccountId, share.Quantity)), shuffled[0].Shares.Select(share => (share.AccountId, share.Quantity)));
        }

        [Fact]
        public void AFillBeyondWhatTheAllocationsNeedIsLeftUnallocated()
        {
            var divided = OrderBook.AllocateFills([new AllocationItem(AccountA, 5m)], [Fill(Guid.Empty, Guid.Empty, 3m), Fill(Guid.Empty, Guid.Empty, 4m)]);
            Assert.Equal(3m, divided[0].Shares.Single().Quantity);
            Assert.Equal(2m, divided[1].Shares.Single().Quantity);
            Assert.Equal(2m, divided[1].Unallocated);
        }

        [Fact]
        public void FeesFollowTheQuantitiesToTheCent()
        {
            var divided = OrderBook.AllocateFills([new AllocationItem(AccountA, 2m), new AllocationItem(AccountB, 1m)], [Fill(Guid.Empty, Guid.Empty, 3m, fees: 1m)]);
            Assert.Equal([(AccountA, 0.67m), (AccountB, 0.33m)], divided[0].Shares.Select(share => (share.AccountId, share.Fees)));
            Assert.Equal(1m, divided[0].Shares.Sum(share => share.Fees));
        }

        [Fact]
        public void AFillPostsTheAssetAndTheCashWithTheFeesInTheCash()
        {
            var dollar = new CashFact(new CashDto { AssetId = Guid.NewGuid(), CashType = CashType.Currency, CurrencyCode = "USD" });
            var asset = new AssetFact(new AssetDto { AssetId = Stock, AssetType = AssetType.Equity, QuoteCurrency = "USD", QuantityFactor = 1m, PriceFactor = 1m });
            var currencies = new Dictionary<Guid, string> { [AccountA] = "USD" };
            var cash = new Dictionary<string, CashFact> { ["USD"] = dollar };
            var assets = new Dictionary<Guid, AssetFact> { [Stock] = asset };

            // A purchase: 10 shares cost $501, and $501 leaves.
            var buy = Order(Instruction.Buy, (AccountA, 10m));
            var fill = Fill(buy.OrderId, Guid.NewGuid(), 10m, price: 50m, fees: 1m);
            var bought = OrderBook.Lines(buy, [fill], assets, cash, currencies);
            Assert.Equal([(Stock, 10m, 501m), (dollar.AssetId, -501m, -501m)], bought.Select(line => (line.AssetId, line.Quantity, line.Amount!.Value)));
            Assert.All(bought, line => Assert.Equal((fill.EventId, EventType.Trade), (line.EventId, line.Kind)));
            Assert.Equal(0m, bought.Sum(line => line.Amount!.Value));

            // A sale: 10 shares leave, and $499 arrives.  An account kept in another currency posts nothing.
            var sell = Order(Instruction.Sell, (AccountA, 10m), (AccountB, 5m));
            var sold = OrderBook.Lines(sell, [Fill(sell.OrderId, Guid.NewGuid(), 15m, price: 50m, fees: 1.5m)], assets, cash, new Dictionary<Guid, string> { [AccountA] = "USD", [AccountB] = "CAD" });
            Assert.Equal([(Stock, -10m, -499m), (dollar.AssetId, 499m, 499m)], sold.Select(line => (line.AssetId, line.Quantity, line.Amount!.Value)));
        }

        [Fact]
        public void TheStatesFollowTheEvents()
        {
            var order = Order(Instruction.Buy, (AccountA, 100m));
            var first = Sent(order, 60m);
            var second = Sent(order, 40m);
            Assert.Equal(OrderStatus.New, OrderBook.State(order, [], null).Status);
            var pending = OrderBook.State(order, [first], null);
            Assert.Equal((OrderStatus.Working, 60m, 40m), (pending.Status, pending.Legs[0].Placed, pending.Legs[0].Unplaced));
            Assert.Equal(BrokerOrderStatus.Pending, pending.BrokerOrders.Single().Status);
            Assert.Equal(BrokerOrderStatus.Working, OrderBook.State(order, [first, Accepted(first)], null).BrokerOrders.Single().Status);

            // Part filled, then the second broker order rejected: 25 filled, 35 still working, 40 back to unplaced.
            var partFill = Fill(order.OrderId, first.BrokerOrderId, 25m, price: 10m);
            var partly = OrderBook.State(order, [first, second, Accepted(first), partFill, Rejected(second)], null);
            Assert.Equal((OrderStatus.PartiallyFilled, 25m, 60m, 40m), (partly.Status, partly.Legs[0].Filled, partly.Legs[0].Placed, partly.Legs[0].Unplaced));
            Assert.Equal([BrokerOrderStatus.PartiallyFilled, BrokerOrderStatus.Rejected], partly.BrokerOrders.Select(state => state.Status));
            Assert.Equal((25m, 75m), (partly.Legs[0].Allocations.Single().Filled, partly.Legs[0].Allocations.Single().Remaining));

            // The rest of the first filled at another price: nothing is working.
            var done = OrderBook.State(order, [first, second, Accepted(first), partFill, Fill(order.OrderId, first.BrokerOrderId, 35m, price: 12m), Rejected(second)], null);
            var leg = done.BrokerOrders.First().Legs.Single();
            Assert.Equal((BrokerOrderStatus.Filled, 60m, 0m), (done.BrokerOrders.First().Status, leg.Filled, leg.Open));
            Assert.Equal(((25m * 10m) + (35m * 12m)) / 60m, leg.AveragePrice);
            Assert.False(done.IsWorking);

            // Cancelled with nothing working: the order is done; what filled stands.
            var cancel = Stamp(new CancelRequestedEvent { OrderId = order.OrderId });
            var cancelled = OrderBook.State(order, [first, Accepted(first), partFill, cancel, Stamp(new OrderCancelledEvent { OrderId = order.OrderId, BrokerOrderId = first.BrokerOrderId, ReportTime = Close })], null);
            Assert.Equal((OrderStatus.Cancelled, 25m, true), (cancelled.Status, cancelled.Legs[0].Filled, cancelled.IsFinished));
        }

        [Fact]
        public void AReplacedOrderIsDoneAndItsReplacementCarriesTheChain()
        {
            var original = Order(Instruction.Buy, (AccountA, 100m));
            var replacement = Stamp(new OrderReplacedEvent
            {
                OrderId = Guid.NewGuid(),
                Replaces = original.OrderId,
                Origin = OrderOrigin.Manual,
                Legs = [new OrderLeg(1, Stock, Instruction.Buy, [new AllocationItem(AccountA, 80m)])],
            }, correlationId: original.OrderId);

            var old = OrderBook.State(original, [], replacement);
            Assert.Equal((OrderStatus.Replaced, replacement.OrderId, true), (old.Status, old.ReplacedBy!.Value, old.IsFinished));
            var current = OrderBook.State(replacement, [], null);
            Assert.Equal((original.OrderId, original.OrderId, 80m), (current.Replaces!.Value, current.ChainId, current.Legs.Single().Quantity));
        }

        [Fact]
        public void AnOrderWithTwoLegsIsFilledWhenBothAre()
        {
            // A spread: buy the stock, sell a call on it, for two accounts.
            var order = Stamp(new OrderPlacedEvent
            {
                OrderId = Guid.NewGuid(),
                Origin = OrderOrigin.Manual,
                Legs =
                [
                    new OrderLeg(1, Stock, Instruction.Buy, [new AllocationItem(AccountA, 200m), new AllocationItem(AccountB, 100m)], PositionEffect.Opening),
                    new OrderLeg(2, Option, Instruction.SellToOpen, [new AllocationItem(AccountA, 2m), new AllocationItem(AccountB, 1m)], PositionEffect.Opening),
                ],
            });
            var sent = Stamp(new BrokerOrderSentEvent { OrderId = order.OrderId, BrokerOrderId = Guid.NewGuid(), AccountId = AccountA, Legs = [new BrokerLeg(1, 300m), new BrokerLeg(2, 3m)], OrderType = OrderType.Market, TimeInForce = TimeInForce.Day });
            var stockFilled = Fill(order.OrderId, sent.BrokerOrderId, 300m, legId: 1);
            var half = OrderBook.State(order, [sent, stockFilled], null);
            Assert.Equal((OrderStatus.PartiallyFilled, BrokerOrderStatus.PartiallyFilled), (half.Status, half.BrokerOrders.Single().Status));
            Assert.Equal((OrderSide.Buy, OrderSide.Sell), (half.Legs[0].Side, half.Legs[1].Side));

            var full = OrderBook.State(order, [sent, stockFilled, Fill(order.OrderId, sent.BrokerOrderId, 3m, price: 4m, legId: 2)], null);
            Assert.Equal((OrderStatus.Filled, 2m, 1m), (full.Status, full.Legs[1].Allocations[0].Filled, full.Legs[1].Allocations[1].Filled));
        }

        private static OrderPlacedEvent Order(Instruction instruction, params (Guid AccountId, decimal Quantity)[] allocations) =>
            Stamp(new OrderPlacedEvent
            {
                OrderId = Guid.NewGuid(),
                Origin = OrderOrigin.Rebalance,
                Legs = [new OrderLeg(1, Stock, instruction, allocations.Select(item => new AllocationItem(item.AccountId, item.Quantity)).ToArray())],
            });

        private static BrokerOrderSentEvent Sent(OrderPlacedEvent order, decimal quantity) =>
            Stamp(new BrokerOrderSentEvent { OrderId = order.OrderId, BrokerOrderId = Guid.NewGuid(), AccountId = AccountA, Legs = [new BrokerLeg(1, quantity)], OrderType = OrderType.Market, TimeInForce = TimeInForce.Day });

        private static OrderAcceptedEvent Accepted(BrokerOrderSentEvent sent) =>
            Stamp(new OrderAcceptedEvent { OrderId = sent.OrderId, BrokerOrderId = sent.BrokerOrderId, ReportTime = Close });

        private static OrderRejectedEvent Rejected(BrokerOrderSentEvent sent) =>
            Stamp(new OrderRejectedEvent { OrderId = sent.OrderId, BrokerOrderId = sent.BrokerOrderId, ReportTime = Close });

        private static ExecutionReportedEvent Fill(Guid orderId, Guid brokerOrderId, decimal quantity, decimal price = 100m, decimal fees = 0m, int legId = 1) =>
            Stamp(new ExecutionReportedEvent { OrderId = orderId, BrokerOrderId = brokerOrderId, ReportTime = Close, ExecutionId = Guid.NewGuid().ToString("N"), LegId = legId, Quantity = quantity, Price = price, Fees = fees });

        // Gives an event an envelope, as if read from the log in the order made.
        private static T Stamp<T>(T value, Guid? correlationId = null)
            where T : LedgerEvent =>
            value with { Envelope = new EventEnvelope(Guid.NewGuid(), EventCodec.TypeOf(value), 1, System.Threading.Interlocked.Increment(ref sequence), EventSource.Simulator, Guid.NewGuid().ToString("N"), Close, Close, null, correlationId, null) };
    }
}
