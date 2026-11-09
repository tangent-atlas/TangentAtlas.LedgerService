// <copyright file="HoldingFold.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Holdings
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;

    /// <summary>
    /// Adds up a holding's events into its state.  The simplest version, by design (event-stream design, decision 5): every
    /// time the rule fires it folds the holding's whole history, in processing order, and a benchmark decides later whether
    /// anything cleverer is needed.
    /// </summary>
    /// <remarks>
    /// Processing order is each event's effective time, then the source's own sequence, then the ledger's sequence (the
    /// event's row version, fixed at insert because events are append-only), so a restart folds the same events in the same
    /// order and lands on the same state.
    /// </remarks>
    public static class HoldingFold
    {
        /// <summary>
        /// The order in which events are processed.
        /// </summary>
        /// <param name="effectiveTime">When the event took effect.</param>
        /// <param name="sourceSequence">The source's own order for events at the same time, if it has one.</param>
        /// <param name="rowVersion">The ledger's sequence when the event was recorded.</param>
        /// <returns>The sort key.</returns>
        public static (DateTimeOffset EffectiveTime, long SourceSequence, long RowVersion) ProcessingOrder(DateTimeOffset effectiveTime, long? sourceSequence, long rowVersion) =>
            (effectiveTime, sourceSequence ?? long.MinValue, rowVersion);

        /// <summary>
        /// Folds the events of one holding (an account's position in one security).
        /// </summary>
        /// <param name="accountId">The account.</param>
        /// <param name="securityId">The security.</param>
        /// <param name="openings">The holding's opening balances.</param>
        /// <param name="trades">The holding's trades.</param>
        /// <param name="splits">The security's splits.</param>
        /// <param name="overrides">The holding's overrides that are in force now (expired ones are left out by the caller).</param>
        /// <returns>The holding's state.</returns>
        public static PositionState Position(
            Guid accountId,
            Guid securityId,
            IEnumerable<OpeningBalanceFact> openings,
            IEnumerable<TradeFact> trades,
            IEnumerable<SplitFact> splits,
            IEnumerable<PositionOverrideFact> overrides)
        {
            var entries = new List<(DateTimeOffset EffectiveTime, long SourceSequence, long RowVersion, Action<PositionBuilder> Apply)>();
            foreach (var opening in openings)
            {
                entries.Add(Entry(opening.EffectiveTime, opening.SourceSequence, opening.RowVersion, position => position.Open(opening.Quantity, opening.CostBasis)));
            }

            foreach (var trade in trades)
            {
                entries.Add(Entry(trade.EffectiveTime, trade.SourceSequence, trade.RowVersion, position => position.Trade(trade.Side, trade.Quantity, trade.Price, trade.Fees)));
            }

            foreach (var split in splits)
            {
                entries.Add(Entry(split.EffectiveTime, split.SourceSequence, split.RowVersion, position => position.Split(split.Numerator, split.Denominator)));
            }

            foreach (var item in overrides)
            {
                entries.Add(Entry(item.EffectiveTime, item.SourceSequence, item.RowVersion, position => position.Override(item.Quantity)));
            }

            var builder = new PositionBuilder();
            foreach (var entry in entries.OrderBy(entry => (entry.EffectiveTime, entry.SourceSequence, entry.RowVersion)))
            {
                entry.Apply(builder);
                builder.LastEffectiveTime = entry.EffectiveTime;
            }

            return new PositionState(accountId, securityId, builder.Quantity, builder.IsCostKnown ? builder.Cost : null, builder.IsOverridden, builder.LastEffectiveTime, entries.Count);
        }

        /// <summary>
        /// Folds the cash of one account: its cash entries and the cash side of its trades.  Order doesn't change a sum, so
        /// cash needs no sorting; it is kept as a fold for when it does (a reset to an exact balance, say).
        /// </summary>
        /// <param name="accountId">The account.</param>
        /// <param name="entries">The account's cash entries.</param>
        /// <param name="trades">The account's trades.</param>
        /// <returns>The account's cash.</returns>
        public static CashState Cash(Guid accountId, IEnumerable<CashEntryFact> entries, IEnumerable<TradeFact> trades)
        {
            var count = 0;
            var cash = 0m;
            var last = DateTimeOffset.MinValue;
            foreach (var entry in entries)
            {
                cash += entry.Amount;
                last = Max(last, entry.EffectiveTime);
                count++;
            }

            foreach (var trade in trades)
            {
                cash += trade.NetAmount;
                last = Max(last, trade.EffectiveTime);
                count++;
            }

            return new CashState(accountId, cash, last, count);
        }

        /// <summary>
        /// The price to use for a security: an override in force, else the market's latest price, else the last trade's.
        /// </summary>
        /// <param name="securityId">The security.</param>
        /// <param name="overrides">The security's price overrides that are in force now.</param>
        /// <param name="market">The latest market price, if there is one.</param>
        /// <param name="trades">The security's trades, in any account of the tenant.</param>
        /// <returns>The price, or null if nothing has priced the security yet.</returns>
        public static EffectivePrice? Price(Guid securityId, IEnumerable<PriceOverrideFact> overrides, PriceFact? market, IEnumerable<TradeFact> trades)
        {
            var priceOverride = overrides
                .OrderByDescending(item => ProcessingOrder(item.EffectiveTime, item.SourceSequence, item.RowVersion))
                .FirstOrDefault();
            if (priceOverride is not null)
            {
                return new EffectivePrice(securityId, priceOverride.Price, priceOverride.EffectiveTime, PriceSource.Override);
            }

            if (market is not null && market.Last > 0m)
            {
                return new EffectivePrice(securityId, market.Last, market.AsOf, PriceSource.Market);
            }

            var lastTrade = trades
                .OrderByDescending(trade => ProcessingOrder(trade.EffectiveTime, trade.SourceSequence, trade.RowVersion))
                .FirstOrDefault();
            return lastTrade is null ? null : new EffectivePrice(securityId, lastTrade.Price, lastTrade.EffectiveTime, PriceSource.LastTrade);
        }

        /// <summary>
        /// Whether a temporary entry is in force at a time: from its effective time until (not including) its expiry.
        /// </summary>
        /// <param name="effectiveTime">When it takes effect.</param>
        /// <param name="expiresTime">When it expires.</param>
        /// <param name="now">The time.</param>
        /// <returns>True if it is in force.</returns>
        public static bool IsInForce(DateTimeOffset effectiveTime, DateTimeOffset expiresTime, DateTimeOffset now) =>
            effectiveTime <= now && now < expiresTime;

        private static (DateTimeOffset, long, long, Action<PositionBuilder>) Entry(DateTimeOffset effectiveTime, long? sourceSequence, long rowVersion, Action<PositionBuilder> apply)
        {
            var (time, sequence, version) = ProcessingOrder(effectiveTime, sourceSequence, rowVersion);
            return (time, sequence, version, apply);
        }

        private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

        /// <summary>
        /// A holding part-way through its fold.  Cost is the total paid, fees included; a sale removes cost at the average
        /// (rolled-up positions; tax lots come in the second pass).
        /// </summary>
        private sealed class PositionBuilder
        {
            public decimal Quantity { get; private set; }

            public decimal Cost { get; private set; }

            public bool IsCostKnown { get; private set; } = true;

            public bool IsOverridden { get; private set; }

            public DateTimeOffset LastEffectiveTime { get; set; } = DateTimeOffset.MinValue;

            public void Open(decimal quantity, decimal? costBasis)
            {
                this.Quantity += quantity;
                if (costBasis is { } cost)
                {
                    this.Cost += cost;
                }
                else
                {
                    this.IsCostKnown = false;
                }
            }

            public void Trade(TradeSide side, decimal quantity, decimal price, decimal fees)
            {
                if (side == TradeSide.Buy)
                {
                    this.Quantity += quantity;
                    this.Cost += (quantity * price) + fees;
                    return;
                }

                // A sale removes cost at the average.  Selling more than is held (a short, or a record out of step with
                // the broker's) leaves no cost behind rather than a negative one.
                var held = this.Quantity;
                this.Quantity -= quantity;
                this.Cost = held > 0m && this.Quantity > 0m ? this.Cost * (this.Quantity / held) : 0m;
            }

            public void Split(decimal numerator, decimal denominator)
            {
                if (denominator != 0m)
                {
                    this.Quantity = this.Quantity * numerator / denominator;
                }
            }

            public void Override(decimal quantity)
            {
                this.Quantity = quantity;
                this.IsOverridden = true;
            }
        }
    }
}
