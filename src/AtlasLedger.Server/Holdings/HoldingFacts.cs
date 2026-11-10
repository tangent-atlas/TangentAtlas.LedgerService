// <copyright file="HoldingFacts.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Holdings
{
    using System;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;

    /// <summary>
    /// Where an effective price came from.
    /// </summary>
    public enum PriceSource
    {
        /// <summary>The market's latest price.</summary>
        Market,

        /// <summary>An operator's override, in force now.</summary>
        Override,

        /// <summary>No market price yet: the price of the last purchase or sale.</summary>
        LastTrade,
    }

    /// <summary>
    /// A transaction line that counts now: every line of an ordinary transaction, and the lines of an override while it is in
    /// force.  Derived state, in the rules session only.
    /// </summary>
    /// <param name="Line">The line.</param>
    /// <param name="Transaction">Its transaction.</param>
    public sealed record PostedLine(TransactionLineFact Line, TransactionFact Transaction);

    /// <summary>
    /// A holding: what the posted lines for an account and an asset add up to.  Derived state, never persisted.
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="AssetId">The asset.</param>
    /// <param name="AssetType">The asset's type.</param>
    /// <param name="QuoteCurrency">The currency the asset's price is quoted in.</param>
    /// <param name="QuantityFactor">The units one quantity stands for.</param>
    /// <param name="PriceFactor">The units a quoted price stands for.</param>
    /// <param name="Quantity">The quantity held: shares, units of currency, face value, contracts.</param>
    /// <param name="CostBasis">The cost of what is held, in the valuation currency; null when an opening balance came without one.</param>
    /// <param name="IsOverridden">True when an override in force set the quantity.</param>
    /// <param name="LastEffectiveTime">The effective time of the last line folded.</param>
    /// <param name="LineCount">How many lines were folded.</param>
    public sealed record PositionState(
        Guid AccountId,
        Guid AssetId,
        AssetType AssetType,
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
    /// The price of one unit of a currency in the valuation currency (USD in USD is 1, from the initial prices): what turns
    /// a value in an asset's quote currency into the valuation currency.  Derived state: never persisted.
    /// </summary>
    /// <param name="CurrencyCode">The currency.</param>
    /// <param name="Rate">Its price in the valuation currency.</param>
    public sealed record CurrencyRate(string CurrencyCode, decimal Rate);

    /// <summary>
    /// A price override that is in force now.
    /// </summary>
    /// <param name="Override">The override.</param>
    public sealed record PriceOverrideInForce(PriceOverrideFact Override);

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
