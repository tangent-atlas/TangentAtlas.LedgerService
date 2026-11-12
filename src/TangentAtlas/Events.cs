// <copyright file="Events.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Model
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The kinds of event in the log.  Each has a fixed code, stored as a number in the Event table's EventType: never
    /// renumber, never reuse.  A new kind gets a new code; a changed payload gets a new version of the same kind.
    /// </summary>
    public enum EventType
    {
        /// <summary>Holdings as first known (<see cref="OpeningBalanceEvent"/>).</summary>
        OpeningBalance = 100,

        /// <summary>Cash in or out of an account (<see cref="CashMovementEvent"/>).</summary>
        CashMovement = 101,

        /// <summary>A booked execution (<see cref="TradeEvent"/>).</summary>
        Trade = 102,

        /// <summary>A dividend, interest or distribution (<see cref="IncomeEvent"/>).</summary>
        Income = 103,

        /// <summary>A fee charged to an account (<see cref="FeeEvent"/>).</summary>
        Fee = 104,

        /// <summary>Securities in or out without a trade (<see cref="ReceiveAndDeliverEvent"/>).</summary>
        ReceiveAndDeliver = 105,

        /// <summary>A movement between two of the tenant's accounts (<see cref="JournalEvent"/>).</summary>
        Journal = 106,

        /// <summary>A permanent change made by hand (<see cref="AdjustmentEvent"/>).</summary>
        Adjustment = 107,

        /// <summary>A temporary setting of holdings (<see cref="PositionOverrideEvent"/>).</summary>
        PositionOverride = 108,

        /// <summary>What an earlier event should have been (<see cref="CorrectionEvent"/>).</summary>
        Correction = 190,

        /// <summary>An order (<see cref="OrderPlacedEvent"/>).</summary>
        OrderPlaced = 200,

        /// <summary>An order that replaces another (<see cref="OrderReplacedEvent"/>).</summary>
        OrderReplaced = 201,

        /// <summary>A request to cancel (<see cref="CancelRequestedEvent"/>).</summary>
        CancelRequested = 202,

        /// <summary>Part of an order sent to a broker account (<see cref="BrokerOrderSentEvent"/>).</summary>
        BrokerOrderSent = 203,

        /// <summary>The broker accepted a broker order (<see cref="OrderAcceptedEvent"/>).</summary>
        OrderAccepted = 210,

        /// <summary>A fill (<see cref="ExecutionReportedEvent"/>).</summary>
        ExecutionReported = 211,

        /// <summary>The broker cancelled a broker order (<see cref="OrderCancelledEvent"/>).</summary>
        OrderCancelled = 212,

        /// <summary>The broker (or our router) refused a broker order (<see cref="OrderRejectedEvent"/>).</summary>
        OrderRejected = 213,

        /// <summary>A broker order expired (<see cref="OrderExpiredEvent"/>).</summary>
        OrderExpired = 214,
    }

    /// <summary>
    /// What a leg of an order does, in the broker's terms.
    /// </summary>
    public enum Instruction
    {
        /// <summary>Buy.</summary>
        Buy,

        /// <summary>Sell what is held.</summary>
        Sell,

        /// <summary>Sell what isn't held: open a short.</summary>
        SellShort,

        /// <summary>Buy back a short.</summary>
        BuyToCover,

        /// <summary>An option: buy to open.</summary>
        BuyToOpen,

        /// <summary>An option: buy to close.</summary>
        BuyToClose,

        /// <summary>An option: sell to open.</summary>
        SellToOpen,

        /// <summary>An option: sell to close.</summary>
        SellToClose,
    }

    /// <summary>
    /// Whether a movement opens a position, closes one, or is left to the broker.
    /// </summary>
    public enum PositionEffect
    {
        /// <summary>Opens (or adds to) a position.</summary>
        Opening,

        /// <summary>Closes (or reduces) a position.</summary>
        Closing,

        /// <summary>The broker decides.</summary>
        Automatic,
    }

    /// <summary>
    /// What a fee item is.  Reports total every kind into one generic amount; the kind is kept as the broker reported it.
    /// </summary>
    public enum FeeType
    {
        /// <summary>A commission.</summary>
        Commission,

        /// <summary>The SEC's fee on sales.</summary>
        SecFee,

        /// <summary>FINRA's trading activity fee.</summary>
        TafFee,

        /// <summary>The options regulatory fee.</summary>
        OptionRegulatoryFee,

        /// <summary>An account or service fee.</summary>
        AccountFee,

        /// <summary>Anything else.</summary>
        Other,
    }

    /// <summary>
    /// How cash moved in or out.
    /// </summary>
    public enum CashMethod
    {
        /// <summary>An ACH transfer.</summary>
        Ach,

        /// <summary>A wire.</summary>
        Wire,

        /// <summary>A check.</summary>
        Check,

        /// <summary>Anything else, or not known.</summary>
        Other,
    }

    /// <summary>
    /// What kind of income.
    /// </summary>
    public enum IncomeType
    {
        /// <summary>A dividend.</summary>
        Dividend,

        /// <summary>Interest.</summary>
        Interest,

        /// <summary>A capital-gain distribution.</summary>
        CapitalGain,
    }

    /// <summary>
    /// Why securities moved without a trade.
    /// </summary>
    public enum DeliveryReason
    {
        /// <summary>Transferred in from elsewhere.</summary>
        TransferIn,

        /// <summary>Transferred out to elsewhere.</summary>
        TransferOut,

        /// <summary>A split.</summary>
        Split,

        /// <summary>Another corporate action (merger, spin-off, exchange).</summary>
        CorporateAction,
    }

    /// <summary>
    /// How linked orders depend on each other.
    /// </summary>
    public enum OrderLinkType
    {
        /// <summary>One cancels the others: when any fills, the rest are cancelled.</summary>
        OneCancelsOther,

        /// <summary>One triggers the others: they are sent only once the first has filled.</summary>
        OneTriggersOther,
    }

    /// <summary>
    /// What every event carries besides its payload: the Event table's columns.
    /// </summary>
    /// <param name="EventId">Its key, supplied by its source.</param>
    /// <param name="Type">Its kind.</param>
    /// <param name="Version">Its payload's version.</param>
    /// <param name="Sequence">Its place in the log (its row version): each row written gets the next number, so this orders the
    /// log.  Commits themselves aren't recorded.</param>
    /// <param name="Source">Where it came from.</param>
    /// <param name="SourceId">Its identity there.</param>
    /// <param name="EffectiveTime">When it happened in the world.</param>
    /// <param name="EnteredTime">When it was recorded.</param>
    /// <param name="SourceSequence">Its order at its source, among events at the same time.</param>
    /// <param name="CorrelationId">What it belongs to (an order: the first of its replace chain).</param>
    /// <param name="CausationId">What caused it.</param>
    public sealed record EventEnvelope(
        Guid EventId,
        EventType Type,
        int Version,
        long Sequence,
        EventSource Source,
        string SourceId,
        DateTimeOffset EffectiveTime,
        DateTimeOffset EnteredTime,
        long? SourceSequence,
        Guid? CorrelationId,
        Guid? CausationId)
    {
        /// <summary>
        /// Gets an envelope for an event not read from the log.
        /// </summary>
        public static EventEnvelope None { get; } = new EventEnvelope(Guid.Empty, 0, 0, 0, EventSource.Operator, string.Empty, default, default, null, null, null);
    }

    /// <summary>
    /// An event: its type's own fields (the payload, stored as JSON), with the envelope it was read with.
    /// </summary>
    public abstract record LedgerEvent
    {
        /// <summary>
        /// Gets the envelope: the base columns it was read with.  Not part of the payload.
        /// </summary>
        [JsonIgnore]
        public EventEnvelope Envelope { get; init; } = EventEnvelope.None;

        /// <summary>
        /// Gets the event's key.
        /// </summary>
        [JsonIgnore]
        public Guid EventId => this.Envelope.EventId;
    }

    /// <summary>
    /// One movement of an asset in an account: an item of an account-activity event.  Cash is an asset like any other.
    /// </summary>
    /// <param name="AssetId">The asset.</param>
    /// <param name="Quantity">How much, signed: received positive, given negative.</param>
    /// <param name="Amount">Its value in the account's currency, signed like the quantity; null if not known (an opening
    /// balance without cost).</param>
    /// <param name="Price">The price it moved at, if any.</param>
    /// <param name="PositionEffect">Whether it opens or closes a position.</param>
    /// <param name="FeeType">Marks a fee item, and what kind.</param>
    public sealed record ActivityItem(Guid AssetId, decimal Quantity, decimal? Amount, decimal? Price = null, PositionEffect? PositionEffect = null, FeeType? FeeType = null);

    /// <summary>
    /// An event that changes an account's holdings.  Its items are its effects: nothing else is stored.
    /// </summary>
    public abstract record AccountActivityEvent : LedgerEvent
    {
        /// <summary>Gets the account.</summary>
        public required Guid AccountId { get; init; }

        /// <summary>Gets the day it was traded or happened.</summary>
        public required DateOnly TradeDate { get; init; }

        /// <summary>Gets the day it settles, if it settles later.</summary>
        public DateOnly? SettlementDate { get; init; }

        /// <summary>Gets what moved.</summary>
        public required IReadOnlyList<ActivityItem> Items { get; init; }

        /// <summary>
        /// Gets a value indicating whether its items must balance: an exchange within the books.  A movement across the
        /// books' edge (a deposit, income, a fee) has its other side outside them.
        /// </summary>
        [JsonIgnore]
        public virtual bool MustBalance => false;
    }

    /// <summary>Holdings as first known: for an account we only watch, from the broker's position snapshot.</summary>
    public sealed record OpeningBalanceEvent : AccountActivityEvent;

    /// <summary>Cash in (positive) or out (negative) of an account.</summary>
    public sealed record CashMovementEvent : AccountActivityEvent
    {
        /// <summary>Gets how it moved.</summary>
        public CashMethod Method { get; init; } = CashMethod.Other;
    }

    /// <summary>A booked execution: the asset and the cash, and any fees as items of their own or inside the cash.</summary>
    public sealed record TradeEvent : AccountActivityEvent
    {
        /// <summary>Gets the order it executed, if ours.</summary>
        public Guid? OrderId { get; init; }

        /// <summary>Gets the broker's execution identity.</summary>
        public string? ExecutionId { get; init; }

        /// <inheritdoc/>
        public override bool MustBalance => true;
    }

    /// <summary>Income paid into an account.</summary>
    public sealed record IncomeEvent : AccountActivityEvent
    {
        /// <summary>Gets what kind.</summary>
        public IncomeType IncomeType { get; init; } = IncomeType.Dividend;

        /// <summary>Gets the asset that paid it, if any.</summary>
        public Guid? PaidByAssetId { get; init; }
    }

    /// <summary>A fee charged to an account.</summary>
    public sealed record FeeEvent : AccountActivityEvent;

    /// <summary>Securities in or out without a trade: transfers in kind, splits, corporate actions.</summary>
    public sealed record ReceiveAndDeliverEvent : AccountActivityEvent
    {
        /// <summary>Gets why.</summary>
        public required DeliveryReason Reason { get; init; }
    }

    /// <summary>A movement between two of the tenant's accounts: the items leave <see cref="AccountActivityEvent.AccountId"/>
    /// and arrive at <see cref="ToAccountId"/>, so both sides are in one event.</summary>
    public sealed record JournalEvent : AccountActivityEvent
    {
        /// <summary>Gets the account the items arrive at.</summary>
        public required Guid ToAccountId { get; init; }
    }

    /// <summary>A permanent change made by hand.</summary>
    public sealed record AdjustmentEvent : AccountActivityEvent
    {
        /// <summary>Gets why.</summary>
        public string? Reason { get; init; }
    }

    /// <summary>Sets holdings (not changes them) until it expires: each item's quantity is the holding while in force.</summary>
    public sealed record PositionOverrideEvent : AccountActivityEvent
    {
        /// <summary>Gets when it stops applying, if ever.</summary>
        public DateTimeOffset? ExpiresTime { get; init; }

        /// <summary>Gets why.</summary>
        public string? Reason { get; init; }
    }

    /// <summary>What an earlier event should have been: the earlier one no longer counts, and these items stand instead (none,
    /// to void it).  The earlier event is never changed.</summary>
    public sealed record CorrectionEvent : AccountActivityEvent
    {
        /// <summary>Gets the event it corrects.</summary>
        public required Guid Corrects { get; init; }

        /// <summary>Gets why.</summary>
        public string? Reason { get; init; }
    }

    /// <summary>One account's share of a leg.</summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="Quantity">Its share.</param>
    public sealed record AllocationItem(Guid AccountId, decimal Quantity);

    /// <summary>One leg of an order: an asset, what to do with it, and each account's share.  A stock order has one leg; an
    /// option spread several.</summary>
    /// <param name="LegId">Its number in the order.</param>
    /// <param name="AssetId">The asset.</param>
    /// <param name="Instruction">What to do.</param>
    /// <param name="Allocations">Each account's share; the leg's quantity is their sum.</param>
    /// <param name="PositionEffect">Whether it opens or closes.</param>
    public sealed record OrderLeg(int LegId, Guid AssetId, Instruction Instruction, IReadOnlyList<AllocationItem> Allocations, PositionEffect? PositionEffect = null)
    {
        /// <summary>Gets the leg's quantity.</summary>
        [JsonIgnore]
        public decimal Quantity => this.Allocations.Sum(allocation => allocation.Quantity);
    }

    /// <summary>How an order is linked to others.</summary>
    /// <param name="Type">How they depend on each other.</param>
    /// <param name="OrderIds">The other orders.  For one-triggers-other, this order is the trigger.</param>
    public sealed record OrderLink(OrderLinkType Type, IReadOnlyList<Guid> OrderIds);

    /// <summary>An event about an order.  Its correlation ID is the first order of its replace chain.</summary>
    public abstract record OrderEvent : LedgerEvent
    {
        /// <summary>Gets the order.</summary>
        public required Guid OrderId { get; init; }
    }

    /// <summary>An order: an intention to trade, for one or more accounts, in one or more legs.</summary>
    public record OrderPlacedEvent : OrderEvent
    {
        /// <summary>Gets what created it.</summary>
        public required OrderOrigin Origin { get; init; }

        /// <summary>Gets the rule that created it, for a rule's order.</summary>
        public string? RuleName { get; init; }

        /// <summary>Gets its legs.</summary>
        public required IReadOnlyList<OrderLeg> Legs { get; init; }

        /// <summary>Gets how it is linked to other orders, if it is.</summary>
        public OrderLink? Link { get; init; }
    }

    /// <summary>An order replacing another: the other is no longer worked (what it filled stands), and this one is.</summary>
    public sealed record OrderReplacedEvent : OrderPlacedEvent
    {
        /// <summary>Gets the order it replaces.</summary>
        public required Guid Replaces { get; init; }
    }

    /// <summary>A request to cancel an order, or one of its broker orders.</summary>
    public sealed record CancelRequestedEvent : OrderEvent
    {
        /// <summary>Gets the broker order, or null for all of the order's.</summary>
        public Guid? BrokerOrderId { get; init; }
    }

    /// <summary>One broker leg's quantity.</summary>
    /// <param name="LegId">The order's leg.</param>
    /// <param name="Quantity">How much of it this broker order carries.</param>
    public sealed record BrokerLeg(int LegId, decimal Quantity);

    /// <summary>Part of an order, as sent to one broker account.  Its key is the identity the broker is given.</summary>
    public sealed record BrokerOrderSentEvent : OrderEvent
    {
        /// <summary>Gets the broker order.</summary>
        public required Guid BrokerOrderId { get; init; }

        /// <summary>Gets the account it trades.</summary>
        public required Guid AccountId { get; init; }

        /// <summary>Gets its legs.</summary>
        public required IReadOnlyList<BrokerLeg> Legs { get; init; }

        /// <summary>Gets its type.</summary>
        public required OrderType OrderType { get; init; }

        /// <summary>Gets its limit price, for a limit order.</summary>
        public decimal? LimitPrice { get; init; }

        /// <summary>Gets its stop price, for a stop order.</summary>
        public decimal? StopPrice { get; init; }

        /// <summary>Gets how long it lasts.</summary>
        public required TimeInForce TimeInForce { get; init; }
    }

    /// <summary>The broker's answer about a broker order.</summary>
    public abstract record BrokerAnswerEvent : OrderEvent
    {
        /// <summary>Gets the broker order.</summary>
        public required Guid BrokerOrderId { get; init; }

        /// <summary>Gets when the broker said it.</summary>
        public required DateTimeOffset ReportTime { get; init; }

        /// <summary>Gets what the broker said, in words.</summary>
        public string? Text { get; init; }
    }

    /// <summary>The broker accepted a broker order.</summary>
    public sealed record OrderAcceptedEvent : BrokerAnswerEvent;

    /// <summary>The broker cancelled a broker order: what it filled stands.</summary>
    public sealed record OrderCancelledEvent : BrokerAnswerEvent;

    /// <summary>The broker (or our router) refused a broker order.</summary>
    public sealed record OrderRejectedEvent : BrokerAnswerEvent;

    /// <summary>A broker order expired.</summary>
    public sealed record OrderExpiredEvent : BrokerAnswerEvent;

    /// <summary>A fill of one leg of a broker order: only a confirmed fill.</summary>
    public sealed record ExecutionReportedEvent : BrokerAnswerEvent
    {
        /// <summary>Gets the broker's execution identity.</summary>
        public required string ExecutionId { get; init; }

        /// <summary>Gets the leg filled.</summary>
        public int LegId { get; init; } = 1;

        /// <summary>Gets how much filled.</summary>
        public required decimal Quantity { get; init; }

        /// <summary>Gets the price.</summary>
        public required decimal Price { get; init; }

        /// <summary>Gets the fees, as one amount.</summary>
        public decimal Fees { get; init; }
    }

    /// <summary>
    /// Reads and writes event payloads: each kind's type and current version, and the JSON they are stored as.  Old versions
    /// will be read here too, each upgraded to the current one, so the rules only ever see the current version.
    /// </summary>
    public static class EventCodec
    {
        private static readonly JsonSerializerOptions Options = CreateOptions();

        private static readonly Dictionary<EventType, Type> Types = new Dictionary<EventType, Type>
        {
            [EventType.OpeningBalance] = typeof(OpeningBalanceEvent),
            [EventType.CashMovement] = typeof(CashMovementEvent),
            [EventType.Trade] = typeof(TradeEvent),
            [EventType.Income] = typeof(IncomeEvent),
            [EventType.Fee] = typeof(FeeEvent),
            [EventType.ReceiveAndDeliver] = typeof(ReceiveAndDeliverEvent),
            [EventType.Journal] = typeof(JournalEvent),
            [EventType.Adjustment] = typeof(AdjustmentEvent),
            [EventType.PositionOverride] = typeof(PositionOverrideEvent),
            [EventType.Correction] = typeof(CorrectionEvent),
            [EventType.OrderPlaced] = typeof(OrderPlacedEvent),
            [EventType.OrderReplaced] = typeof(OrderReplacedEvent),
            [EventType.CancelRequested] = typeof(CancelRequestedEvent),
            [EventType.BrokerOrderSent] = typeof(BrokerOrderSentEvent),
            [EventType.OrderAccepted] = typeof(OrderAcceptedEvent),
            [EventType.ExecutionReported] = typeof(ExecutionReportedEvent),
            [EventType.OrderCancelled] = typeof(OrderCancelledEvent),
            [EventType.OrderRejected] = typeof(OrderRejectedEvent),
            [EventType.OrderExpired] = typeof(OrderExpiredEvent),
        };

        private static readonly Dictionary<Type, EventType> Codes = Types.ToDictionary(item => item.Value, item => item.Key);

        /// <summary>
        /// The version every kind is written at.
        /// </summary>
        public const int CurrentVersion = 1;

        /// <summary>
        /// The kind of an event.
        /// </summary>
        /// <param name="value">The event.</param>
        /// <returns>Its kind.</returns>
        public static EventType TypeOf(LedgerEvent value)
        {
            ArgumentNullException.ThrowIfNull(value);
            return Codes.TryGetValue(value.GetType(), out var code)
                ? code
                : throw new ArgumentException($"{value.GetType().Name} is not an event kind.", nameof(value));
        }

        /// <summary>
        /// The payload of an event, as stored.
        /// </summary>
        /// <param name="value">The event.</param>
        /// <returns>Its JSON.</returns>
        public static string Encode(LedgerEvent value)
        {
            ArgumentNullException.ThrowIfNull(value);
            return JsonSerializer.Serialize(value, value.GetType(), Options);
        }

        /// <summary>
        /// Reads an event from its envelope and payload.
        /// </summary>
        /// <param name="envelope">The base columns.</param>
        /// <param name="payload">The JSON.</param>
        /// <returns>The event, with its envelope.</returns>
        /// <exception cref="FormatException">The kind or version is unknown, or the payload doesn't read as it.</exception>
        public static LedgerEvent Decode(EventEnvelope envelope, string payload)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            if (!Types.TryGetValue(envelope.Type, out var type))
            {
                throw new FormatException($"{(int)envelope.Type} is not an event kind.");
            }

            if (envelope.Version != CurrentVersion)
            {
                throw new FormatException($"Version {envelope.Version} of {envelope.Type} is not known (the current version is {CurrentVersion}).");
            }

            try
            {
                var value = (LedgerEvent?)JsonSerializer.Deserialize(payload, type, Options) ?? throw new FormatException("The payload is empty.");
                return value with { Envelope = envelope };
            }
            catch (JsonException exception)
            {
                throw new FormatException($"The payload doesn't read as {envelope.Type}: {exception.Message}", exception);
            }
        }

        private static JsonSerializerOptions CreateOptions()
        {
            // Nulls are written, not left out: a field missing from a payload is then always an error, never a null.
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                RespectNullableAnnotations = true,
                RespectRequiredConstructorParameters = true,
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
    }
}
