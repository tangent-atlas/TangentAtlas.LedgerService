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
    /// Adds up a holding's transaction lines into its state.  Every asset is held the same way (cash is an asset like any
    /// other), so one fold serves them all.  The simplest version, by design: every time the rule fires it folds the holding's
    /// whole history, in processing order, and a benchmark decides later whether anything cleverer is needed.
    /// </summary>
    /// <remarks>
    /// Processing order is each transaction's effective time, then the source's own sequence, then the ledger's sequence
    /// (the transaction's row version, fixed at insert because transactions are append-only), then the line's number, so a
    /// restart folds the same lines in the same order and lands on the same state.
    /// </remarks>
    public static class HoldingFold
    {
        /// <summary>
        /// The kinds of transaction that exchange one asset for another, whose lines must balance.
        /// </summary>
        public static readonly IReadOnlySet<TransactionKind> Exchanges = new HashSet<TransactionKind> { TransactionKind.Buy, TransactionKind.Sell };

        /// <summary>
        /// The order in which lines are processed.
        /// </summary>
        /// <param name="posted">A posted line.</param>
        /// <returns>The sort key.</returns>
        public static (DateTimeOffset EffectiveTime, long SourceSequence, long RowVersion, int LineNumber) ProcessingOrder(PostedLine posted)
        {
            ArgumentNullException.ThrowIfNull(posted);
            var transaction = posted.Transaction;
            return (transaction.EffectiveTime, transaction.SourceSequence ?? long.MinValue, transaction.RowVersion, posted.Line.LineNumber);
        }

        /// <summary>
        /// Folds the posted lines of one holding (an account's quantity of one asset).
        /// </summary>
        /// <param name="accountId">The account.</param>
        /// <param name="asset">The asset.</param>
        /// <param name="lines">The holding's posted lines, in any order.</param>
        /// <returns>The holding's state.</returns>
        public static PositionState Position(Guid accountId, AssetFact asset, IEnumerable<PostedLine> lines)
        {
            ArgumentNullException.ThrowIfNull(asset);
            ArgumentNullException.ThrowIfNull(lines);
            var quantity = 0m;
            var cost = 0m;
            var isCostKnown = true;
            var isOverridden = false;
            var last = DateTimeOffset.MinValue;
            var count = 0;
            foreach (var posted in lines.OrderBy(ProcessingOrder))
            {
                var line = posted.Line;
                count++;
                last = posted.Transaction.EffectiveTime;
                if (line.Effect == LineEffect.SetTo)
                {
                    // An override sets the quantity and keeps the cost already there.
                    quantity = line.Quantity;
                    isOverridden = true;
                    continue;
                }

                if (line.Quantity >= 0m)
                {
                    // Received: its cost is the line's amount (zero for a split; unknown when an opening balance has none).
                    quantity += line.Quantity;
                    if (line.Amount is { } amount)
                    {
                        cost += amount;
                    }
                    else if (line.Quantity != 0m)
                    {
                        isCostKnown = false;
                    }

                    continue;
                }

                // Given: cost leaves at the average.  Giving more than is held (a record out of step with the broker's)
                // leaves no cost behind rather than a negative one.
                var held = quantity;
                quantity += line.Quantity;
                cost = held > 0m && quantity > 0m ? cost * (quantity / held) : 0m;
            }

            return new PositionState(accountId, asset.AssetId, asset.AssetType, asset.QuoteCurrency, asset.QuantityFactor, asset.PriceFactor, quantity, isCostKnown ? cost : null, isOverridden, last, count);
        }

        /// <summary>
        /// The price to use for an asset, in its quote currency: an override in force, else the market's latest price (for a
        /// currency, its constant from the initial prices), else the price of its last purchase or sale.
        /// </summary>
        /// <param name="asset">The asset.</param>
        /// <param name="overrides">The asset's price overrides in force now.</param>
        /// <param name="market">The latest market price, if there is one.</param>
        /// <param name="exchanges">The posted lines of purchases and sales of the asset, in any account of the tenant.</param>
        /// <returns>The price, or null if nothing has priced the asset yet.</returns>
        public static EffectivePrice? Price(AssetFact asset, IEnumerable<PriceOverrideFact> overrides, PriceFact? market, IEnumerable<PostedLine> exchanges)
        {
            ArgumentNullException.ThrowIfNull(asset);
            var priceOverride = overrides
                .OrderByDescending(item => (item.EffectiveTime, item.SourceSequence ?? long.MinValue, item.RowVersion))
                .FirstOrDefault();
            if (priceOverride is not null)
            {
                return new EffectivePrice(asset.AssetId, priceOverride.Price, asset.QuoteCurrency, priceOverride.EffectiveTime, PriceSource.Override);
            }

            if (market is not null && market.Last > 0m)
            {
                return new EffectivePrice(asset.AssetId, market.Last, asset.QuoteCurrency, market.AsOf, PriceSource.Market);
            }

            var last = exchanges
                .Where(posted => posted.Line.Quantity != 0m && posted.Line.Amount is not null)
                .OrderByDescending(ProcessingOrder)
                .FirstOrDefault();
            return last is null ? null : new EffectivePrice(asset.AssetId, QuotedPrice(asset, last.Line), asset.QuoteCurrency, last.Transaction.EffectiveTime, PriceSource.LastTrade);
        }

        /// <summary>
        /// The quoted price a line was exchanged at: its amount ÷ (quantity × QuantityFactor × PriceFactor).
        /// </summary>
        /// <param name="asset">The asset.</param>
        /// <param name="line">A line with a quantity and an amount.</param>
        /// <returns>The price, as quoted.</returns>
        public static decimal QuotedPrice(AssetFact asset, TransactionLineFact line)
        {
            ArgumentNullException.ThrowIfNull(asset);
            ArgumentNullException.ThrowIfNull(line);
            var units = line.Quantity * asset.QuantityFactor * asset.PriceFactor;
            return units == 0m || line.Amount is not { } amount ? 0m : Math.Abs(amount / units);
        }

        /// <summary>
        /// The market value of a quantity at a quoted price, in the valuation currency: quantity × QuantityFactor × price ×
        /// PriceFactor × the price of the quote currency in the valuation currency.  The same formula for every asset.
        /// </summary>
        /// <param name="quantity">The quantity.</param>
        /// <param name="quantityFactor">The units one quantity stands for.</param>
        /// <param name="price">The quoted price, in the quote currency.</param>
        /// <param name="priceFactor">The units a quoted price stands for.</param>
        /// <param name="currencyRate">The price of one unit of the quote currency in the valuation currency.</param>
        /// <returns>The value, in the valuation currency.</returns>
        public static decimal MarketValue(decimal quantity, decimal quantityFactor, decimal price, decimal priceFactor, decimal currencyRate) =>
            quantity * quantityFactor * price * priceFactor * currencyRate;

        /// <summary>
        /// Whether an exchange balances: at least two lines, every line valued, and the values summing to zero (to the cent).
        /// </summary>
        /// <param name="lines">The transaction's lines.</param>
        /// <returns>True if it balances.</returns>
        public static bool IsBalanced(IReadOnlyCollection<TransactionLineFact> lines)
        {
            ArgumentNullException.ThrowIfNull(lines);
            return lines.Count >= 2
                && lines.All(line => line.Amount is not null)
                && Math.Abs(lines.Sum(line => line.Amount!.Value)) < 0.005m;
        }

        /// <summary>
        /// Whether a temporary entry is in force at a time: from its effective time until (not including) its expiry.
        /// </summary>
        /// <param name="effectiveTime">When it takes effect.</param>
        /// <param name="expiresTime">When it expires; null never expires.</param>
        /// <param name="now">The time.</param>
        /// <returns>True if it is in force.</returns>
        public static bool IsInForce(DateTimeOffset effectiveTime, DateTimeOffset? expiresTime, DateTimeOffset now) =>
            effectiveTime <= now && (expiresTime is null || now < expiresTime.Value);
    }
}
