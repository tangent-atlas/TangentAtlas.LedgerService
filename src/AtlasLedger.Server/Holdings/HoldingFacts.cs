// <copyright file="HoldingFacts.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Holdings
{
    using System;
    using System.Collections.Generic;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;

    /// <summary>
    /// Where an effective price came from.
    /// </summary>
    public enum PriceSource
    {
        /// <summary>The market's latest price.</summary>
        Market,

        /// <summary>No market price yet: the price of the last purchase or sale.</summary>
        LastTrade,
    }

    /// <summary>
    /// One movement of one asset in one account, as an event posts it: what the holdings fold.  Derived from account-activity
    /// events (their items), and from fills of our own orders (booked by the published allocation rule); never stored.
    /// </summary>
    /// <param name="EventId">The event it comes from.</param>
    /// <param name="ItemNumber">Its place among that event's movements.</param>
    /// <param name="AccountId">The account.</param>
    /// <param name="AssetId">The asset.</param>
    /// <param name="Effect">Change the holding, or set it (an override).</param>
    /// <param name="Quantity">How much, signed.</param>
    /// <param name="Amount">Its value in the account's currency, signed; null if not known.</param>
    /// <param name="Kind">The kind of event (a trade's price can stand in for a missing market price).</param>
    /// <param name="EffectiveTime">When it happened.</param>
    /// <param name="SourceSequence">Its order at its source, among events at the same time.</param>
    /// <param name="Sequence">The commit that wrote the event.</param>
    public sealed record PostedLine(
        Guid EventId,
        int ItemNumber,
        Guid AccountId,
        Guid AssetId,
        LineEffect Effect,
        decimal Quantity,
        decimal? Amount,
        EventType Kind,
        DateTimeOffset EffectiveTime,
        long? SourceSequence,
        long Sequence);

    /// <summary>
    /// The movements one event posts (or one order's fills), as one fact: the holdings rules flatten them.
    /// </summary>
    /// <param name="Key">What posted them: an event, or an order's fills.</param>
    /// <param name="Lines">The movements.</param>
    public sealed record PostedLines(Guid Key, IReadOnlyList<PostedLine> Lines);

    /// <summary>
    /// Something wrong with an event in the log: it can't be read, names what doesn't exist, or doesn't balance.  It
    /// stays in the log (nothing in it is ever changed) and is left out of what is derived; a correction fixes it.
    /// </summary>
    /// <param name="EventId">The event.</param>
    /// <param name="Problem">What is wrong.</param>
    public sealed record EventProblem(Guid EventId, string Problem);


    /// <summary>
    /// A holding: what the posted lines for an account and an asset add up to.  Derived state, never persisted.
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="AssetId">The asset.</param>
    /// <param name="AssetType">The asset's type.</param>
    /// <param name="AccountCurrency">The currency the account is kept in: the holding's cost and market value are in it.</param>
    /// <param name="QuoteCurrency">The currency the asset's price is quoted in.</param>
    /// <param name="QuantityFactor">The units one quantity stands for.</param>
    /// <param name="PriceFactor">The units a quoted price stands for.</param>
    /// <param name="Quantity">The quantity held: shares, units of currency, face value, contracts.</param>
    /// <param name="CostBasis">The cost of what is held, in the account's currency; null when an opening balance came without one.</param>
    /// <param name="IsOverridden">True when an override in force set the quantity.</param>
    /// <param name="LastEffectiveTime">The effective time of the last line folded.</param>
    /// <param name="LineCount">How many lines were folded.</param>
    public sealed record PositionState(
        Guid AccountId,
        Guid AssetId,
        AssetType AssetType,
        string AccountCurrency,
        string QuoteCurrency,
        decimal QuantityFactor,
        decimal PriceFactor,
        decimal Quantity,
        decimal? CostBasis,
        bool IsOverridden,
        DateTimeOffset LastEffectiveTime,
        int LineCount);

    /// <summary>
    /// The price the rules use for an asset, as quoted (per share, per 100 of face value, per option contract's share), in
    /// the asset's quote currency.  Derived state: never persisted.
    /// </summary>
    /// <param name="AssetId">The asset.</param>
    /// <param name="Price">The price.</param>
    /// <param name="Currency">The currency it is in: the asset's quote currency.</param>
    /// <param name="AsOf">When the price was set.</param>
    /// <param name="Source">Where it came from.</param>
    public sealed record EffectivePrice(Guid AssetId, decimal Price, string Currency, DateTimeOffset AsOf, PriceSource Source);

    /// <summary>
    /// The price of one unit of a currency in another: what turns a value in an asset's quote currency into its account's
    /// currency.  A currency in itself is 1, from the initial prices; between currencies, from the feed.  Derived state:
    /// never persisted.
    /// </summary>
    /// <param name="FromCurrency">The currency priced.</param>
    /// <param name="ToCurrency">The currency its price is in.</param>
    /// <param name="Rate">The price of one unit of <paramref name="FromCurrency"/> in <paramref name="ToCurrency"/>.</param>
    public sealed record CurrencyRate(string FromCurrency, string ToCurrency, decimal Rate);

    /// <summary>
    /// What has been paid into an account less what has been taken out: its deposits less its withdrawals, in the account's
    /// currency.  Derived; never persisted.  (Opening balances are not counted: they bring holdings, not money paid in.)
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="NetContributions">Deposits less withdrawals.</param>
    public sealed record AccountFlow(Guid AccountId, decimal NetContributions);

    /// <summary>
    /// The time, for rules that need it (overrides expire).  One per session: every clock fact equals every other, so the
    /// session updates it in place.  It moves on once a minute, so the rules that read it fire at most once a minute.
    /// </summary>
    public sealed class ClockFact : IEquatable<ClockFact>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClockFact"/> class.
        /// </summary>
        /// <param name="now">The time.</param>
        public ClockFact(DateTimeOffset now)
        {
            this.Now = now;
        }

        /// <summary>
        /// Gets the time, to the minute.
        /// </summary>
        public DateTimeOffset Now { get; }

        /// <inheritdoc/>
        public bool Equals(ClockFact? other) => other is not null;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is ClockFact;

        /// <inheritdoc/>
        public override int GetHashCode() => typeof(ClockFact).GetHashCode();
    }
}
