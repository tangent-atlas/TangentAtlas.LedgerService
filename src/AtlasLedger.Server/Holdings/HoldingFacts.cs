// <copyright file="HoldingFacts.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Holdings
{
    using System;
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

        /// <summary>No market price yet: the last trade's price.</summary>
        LastTrade,
    }

    /// <summary>
    /// A holding: what an account's events in one security add up to.  Derived state, held in the rules session and never
    /// persisted.
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="SecurityId">The security.</param>
    /// <param name="Quantity">The number of shares.</param>
    /// <param name="CostBasis">The total paid, fees included; null when an opening balance came without one.</param>
    /// <param name="IsOverridden">True when an operator's override set the quantity.</param>
    /// <param name="LastEffectiveTime">The effective time of the last event folded.</param>
    /// <param name="EventCount">How many events were folded.</param>
    public sealed record PositionState(Guid AccountId, Guid SecurityId, decimal Quantity, decimal? CostBasis, bool IsOverridden, DateTimeOffset LastEffectiveTime, int EventCount);

    /// <summary>
    /// An account's cash: its cash entries and the cash side of its trades.  Derived state.
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="Cash">The cash, in dollars.</param>
    /// <param name="LastEffectiveTime">The effective time of the last event folded.</param>
    /// <param name="EventCount">How many events were folded.</param>
    public sealed record CashState(Guid AccountId, decimal Cash, DateTimeOffset LastEffectiveTime, int EventCount);

    /// <summary>
    /// The price the rules use for a security.  Derived state: never persisted.
    /// </summary>
    /// <param name="SecurityId">The security.</param>
    /// <param name="Price">The price.</param>
    /// <param name="AsOf">When the price was set.</param>
    /// <param name="Source">Where it came from.</param>
    public sealed record EffectivePrice(Guid SecurityId, decimal Price, DateTimeOffset AsOf, PriceSource Source);

    /// <summary>
    /// A position override that is in force now.  Kept apart from the clock so that the clock moving on re-checks only the
    /// overrides, not every holding.
    /// </summary>
    /// <param name="Override">The override.</param>
    public sealed record PositionOverrideInForce(PositionOverrideFact Override);

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
