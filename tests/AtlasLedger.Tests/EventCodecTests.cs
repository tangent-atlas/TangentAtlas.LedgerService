// <copyright file="EventCodecTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Tests
{
    using System;
    using System.Linq;
    using AtlasLedger.Model;
    using Xunit;

    /// <summary>
    /// Event payloads: every kind is written and read back exactly, and anything else is refused, never guessed.
    /// </summary>
    public sealed class EventCodecTests
    {
        private static readonly Guid Account = Guid.NewGuid();

        private static readonly Guid Asset = Guid.NewGuid();

        public static TheoryData<LedgerEvent> Samples =>
        [
            new OpeningBalanceEvent { AccountId = Account, TradeDate = new DateOnly(2026, 11, 2), Items = [new ActivityItem(Asset, 10m, null)] },
            new CashMovementEvent { AccountId = Account, TradeDate = new DateOnly(2026, 11, 2), Method = CashMethod.Wire, Items = [new ActivityItem(Asset, 1_000m, 1_000m)] },
            new TradeEvent { AccountId = Account, TradeDate = new DateOnly(2026, 11, 2), SettlementDate = new DateOnly(2026, 11, 3), OrderId = Guid.NewGuid(), ExecutionId = "x-1", Items = [new ActivityItem(Asset, 3m, 301m, 100m, PositionEffect.Opening), new ActivityItem(Guid.NewGuid(), -300m, -300m), new ActivityItem(Guid.NewGuid(), -1m, -1m, FeeType: FeeType.Commission)] },
            new IncomeEvent { AccountId = Account, TradeDate = new DateOnly(2026, 11, 2), IncomeType = IncomeType.Interest, PaidByAssetId = Asset, Items = [new ActivityItem(Asset, 5m, 5m)] },
            new FeeEvent { AccountId = Account, TradeDate = new DateOnly(2026, 11, 2), Items = [new ActivityItem(Asset, -2m, -2m, FeeType: FeeType.AccountFee)] },
            new ReceiveAndDeliverEvent { AccountId = Account, TradeDate = new DateOnly(2026, 11, 2), Reason = DeliveryReason.Split, Items = [new ActivityItem(Asset, 10m, 0m)] },
            new JournalEvent { AccountId = Account, ToAccountId = Guid.NewGuid(), TradeDate = new DateOnly(2026, 11, 2), Items = [new ActivityItem(Asset, 7m, 700m)] },
            new AdjustmentEvent { AccountId = Account, TradeDate = new DateOnly(2026, 11, 2), Reason = "Broker's correction.", Items = [new ActivityItem(Asset, -1m, null)] },
            new PositionOverrideEvent { AccountId = Account, TradeDate = new DateOnly(2026, 11, 2), ExpiresTime = DateTimeOffset.UnixEpoch, Items = [new ActivityItem(Asset, 2_500m, null)] },
            new CorrectionEvent { AccountId = Account, TradeDate = new DateOnly(2026, 11, 2), Corrects = Guid.NewGuid(), Items = [] },
            new OrderPlacedEvent { OrderId = Guid.NewGuid(), Origin = OrderOrigin.Rule, RuleName = "Thermostat", Link = new OrderLink(OrderLinkType.OneCancelsOther, [Guid.NewGuid()]), Legs = [new OrderLeg(1, Asset, Instruction.SellShort, [new AllocationItem(Account, 5m)], PositionEffect.Opening)] },
            new OrderReplacedEvent { OrderId = Guid.NewGuid(), Replaces = Guid.NewGuid(), Origin = OrderOrigin.Manual, Legs = [new OrderLeg(1, Asset, Instruction.Buy, [new AllocationItem(Account, 5m)])] },
            new CancelRequestedEvent { OrderId = Guid.NewGuid() },
            new BrokerOrderSentEvent { OrderId = Guid.NewGuid(), BrokerOrderId = Guid.NewGuid(), AccountId = Account, Legs = [new BrokerLeg(1, 5m)], OrderType = OrderType.Limit, LimitPrice = 99.5m, TimeInForce = TimeInForce.GoodTillCancelled },
            new OrderAcceptedEvent { OrderId = Guid.NewGuid(), BrokerOrderId = Guid.NewGuid(), ReportTime = DateTimeOffset.UnixEpoch },
            new ExecutionReportedEvent { OrderId = Guid.NewGuid(), BrokerOrderId = Guid.NewGuid(), ReportTime = DateTimeOffset.UnixEpoch, ExecutionId = "x-2", LegId = 2, Quantity = 5m, Price = 99.4m, Fees = 0.01m },
            new OrderCancelledEvent { OrderId = Guid.NewGuid(), BrokerOrderId = Guid.NewGuid(), ReportTime = DateTimeOffset.UnixEpoch },
            new OrderRejectedEvent { OrderId = Guid.NewGuid(), BrokerOrderId = Guid.NewGuid(), ReportTime = DateTimeOffset.UnixEpoch, Text = "No price." },
            new OrderExpiredEvent { OrderId = Guid.NewGuid(), BrokerOrderId = Guid.NewGuid(), ReportTime = DateTimeOffset.UnixEpoch },
        ];

        [Theory]
        [MemberData(nameof(Samples))]
        public void EveryKindReadsBackExactly(LedgerEvent value)
        {
            var envelope = Envelope(EventCodec.TypeOf(value));
            var read = EventCodec.Decode(envelope, EventCodec.Encode(value));
            Assert.Equal(value.GetType(), read.GetType());
            Assert.Equal(EventCodec.Encode(value), EventCodec.Encode(read));   // the same payload, field for field
            Assert.Same(envelope, read.Envelope);
            Assert.DoesNotContain("mustBalance", EventCodec.Encode(value), StringComparison.Ordinal);   // derived, never written
        }

        [Fact]
        public void EveryKindHasASample()
        {
            var sampled = Samples.Select(row => EventCodec.TypeOf(row.Data)).ToHashSet();
            Assert.Equal(Enum.GetValues<EventType>().Order(), sampled.Order());
        }

        [Fact]
        public void AnythingElseIsRefused()
        {
            Assert.Throws<FormatException>(() => EventCodec.Decode(Envelope((EventType)999), "{}"));                      // an unknown kind
            Assert.Throws<FormatException>(() => EventCodec.Decode(Envelope(EventType.Trade) with { Version = 2 }, "{}"));  // an unknown version
            Assert.Throws<FormatException>(() => EventCodec.Decode(Envelope(EventType.CancelRequested), "{"));             // not JSON
            Assert.Throws<FormatException>(() => EventCodec.Decode(Envelope(EventType.CancelRequested), "{\"brokerOrderId\":null}"));   // its order missing
            Assert.Throws<FormatException>(() => EventCodec.Decode(Envelope(EventType.Trade), $"{{\"accountId\":\"{Account}\",\"tradeDate\":\"2026-11-02\",\"items\":[{{\"assetId\":\"{Asset}\",\"quantity\":1}}]}}"));   // an item's amount missing
        }

        private static EventEnvelope Envelope(EventType type) =>
            new EventEnvelope(Guid.NewGuid(), type, EventCodec.CurrentVersion, 1, EventSource.Operator, "test", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, null);
    }
}
