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
        public static readonly IReadOnlySet<EventType> Exchanges = new HashSet<EventType> { EventType.Trade };

        /// <summary>
        /// The order in which lines are processed.
        /// </summary>
        /// <param name="posted">A posted line.</param>
        /// <returns>The sort key.</returns>
        public static (DateTimeOffset EffectiveTime, long SourceSequence, long Sequence, Guid EventId, int ItemNumber) ProcessingOrder(PostedLine posted)
        {
            ArgumentNullException.ThrowIfNull(posted);
            return (posted.EffectiveTime, posted.SourceSequence ?? long.MinValue, posted.Sequence, posted.EventId, posted.ItemNumber);
        }

        /// <summary>
        /// The movements an account-activity event posts: its items, in its account (a journal's leave its account and arrive
        /// at the other).  An override sets holdings rather than changing them.
        /// </summary>
        /// <param name="activity">The event.</param>
        /// <returns>The movements.</returns>
        public static IReadOnlyList<PostedLine> Lines(AccountActivityEvent activity)
        {
            ArgumentNullException.ThrowIfNull(activity);
            var envelope = activity.Envelope;
            var effect = activity is PositionOverrideEvent ? LineEffect.SetTo : LineEffect.Change;
            var kind = activity is CorrectionEvent ? EventType.Correction : envelope.Type;
            var isExternal = activity is CashMovementEvent or JournalEvent or OpeningBalanceEvent or ReceiveAndDeliverEvent { Reason: DeliveryReason.TransferIn or DeliveryReason.TransferOut };
            var lines = new List<PostedLine>();
            foreach (var item in activity.Items)
            {
                lines.Add(new PostedLine(envelope.EventId, lines.Count + 1, activity.AccountId, item.AssetId, effect, item.Quantity, item.Amount, kind, envelope.EffectiveTime, envelope.SourceSequence, envelope.Sequence, isExternal));
                if (activity is JournalEvent journal)
                {
                    lines[^1] = lines[^1] with { Quantity = -item.Quantity, Amount = -item.Amount };
                    lines.Add(new PostedLine(envelope.EventId, lines.Count + 1, journal.ToAccountId, item.AssetId, effect, item.Quantity, item.Amount, kind, envelope.EffectiveTime, envelope.SourceSequence, envelope.Sequence, isExternal));
                }
            }

            return lines;
        }

        /// <summary>
        /// Folds the posted lines of one holding (an account's quantity of one asset).
        /// </summary>
        /// <param name="account">The account.</param>
        /// <param name="asset">The asset.</param>
        /// <param name="lines">The holding's posted lines, in any order.</param>
        /// <returns>The holding's state.</returns>
        public static PositionState Position(AccountFact account, AssetFact asset, IEnumerable<PostedLine> lines)
        {
            ArgumentNullException.ThrowIfNull(account);
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
                var line = posted;
                count++;
                last = posted.EffectiveTime;
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

                // Given: cost leaves at the average, kept to the ledger's money precision (four places) so the division leaves
                // no residue.  Giving more than is held (a record out of step with the broker's) leaves no cost behind rather
                // than a negative one.
                var held = quantity;
                quantity += line.Quantity;
                cost = held > 0m && quantity > 0m ? Math.Round(cost * quantity / held, 4, MidpointRounding.ToEven) : 0m;
            }

            return new PositionState(account.AccountId, asset.AssetId, asset.AssetType, account.Currency, asset.QuoteCurrency, asset.QuantityFactor, asset.PriceFactor, quantity, isCostKnown ? cost : null, isOverridden, last, count);
        }

        /// <summary>
        /// The price to use for an asset, in its quote currency: the market's latest price (for a currency, its constant from
        /// the initial prices), else the price of its last purchase or sale.
        /// </summary>
        /// <param name="asset">The asset.</param>
        /// <param name="market">The latest market price, if there is one.</param>
        /// <param name="exchanges">The posted lines of purchases and sales of the asset, in any account of the tenant.</param>
        /// <returns>The price, or null if nothing has priced the asset yet.</returns>
        public static EffectivePrice? Price(AssetFact asset, PriceFact? market, IEnumerable<PostedLine> exchanges)
        {
            ArgumentNullException.ThrowIfNull(asset);
            if (market is not null && market.Last > 0m)
            {
                return new EffectivePrice(asset.AssetId, market.Last, asset.QuoteCurrency, market.AsOf, PriceSource.Market);
            }

            var last = exchanges
                .Where(posted => posted.Quantity != 0m && posted.Amount is not null)
                .OrderByDescending(ProcessingOrder)
                .FirstOrDefault();
            return last is null ? null : new EffectivePrice(asset.AssetId, QuotedPrice(asset, last), asset.QuoteCurrency, last.EffectiveTime, PriceSource.LastTrade);
        }

        /// <summary>
        /// The quoted price a line was exchanged at: its amount ÷ (quantity × QuantityFactor × PriceFactor).
        /// </summary>
        /// <param name="asset">The asset.</param>
        /// <param name="line">A line with a quantity and an amount.</param>
        /// <returns>The price, as quoted.</returns>
        public static decimal QuotedPrice(AssetFact asset, PostedLine line)
        {
            ArgumentNullException.ThrowIfNull(asset);
            ArgumentNullException.ThrowIfNull(line);
            var units = line.Quantity * asset.QuantityFactor * asset.PriceFactor;
            return units == 0m || line.Amount is not { } amount ? 0m : Math.Abs(amount / units);
        }

        /// <summary>
        /// The market value of a quantity at a quoted price, in the account's currency: quantity × QuantityFactor × price ×
        /// PriceFactor × the price of the quote currency in the account's currency.  The same formula for every asset.
        /// </summary>
        /// <param name="quantity">The quantity.</param>
        /// <param name="quantityFactor">The units one quantity stands for.</param>
        /// <param name="price">The quoted price, in the quote currency.</param>
        /// <param name="priceFactor">The units a quoted price stands for.</param>
        /// <param name="currencyRate">The price of one unit of the quote currency in the account's currency.</param>
        /// <returns>The value, in the account's currency.</returns>
        public static decimal MarketValue(decimal quantity, decimal quantityFactor, decimal price, decimal priceFactor, decimal currencyRate) =>
            quantity * quantityFactor * price * priceFactor * currencyRate;

        /// <summary>
        /// A holding's market value in its account's currency, or null when there is no price or no rate to convert it with:
        /// a value is never assumed.
        /// </summary>
        /// <param name="position">The holding.</param>
        /// <param name="price">The asset's effective price, if any.</param>
        /// <param name="rates">The rates, by (from, to) currency.</param>
        /// <returns>The value, or null.</returns>
        public static decimal? MarketValue(PositionState position, EffectivePrice? price, IReadOnlyDictionary<(string From, string To), decimal> rates)
        {
            ArgumentNullException.ThrowIfNull(position);
            ArgumentNullException.ThrowIfNull(rates);
            return price is not null && rates.TryGetValue((position.QuoteCurrency, position.AccountCurrency), out var rate)
                ? MarketValue(position.Quantity, position.QuantityFactor, price.Price, position.PriceFactor, rate)
                : null;
        }

        /// <summary>
        /// Whether an exchange balances: at least two items, every item valued, and the values summing to zero (to the cent).
        /// </summary>
        /// <param name="items">The event's items.</param>
        /// <returns>True if it balances.</returns>
        public static bool IsBalanced(IReadOnlyCollection<ActivityItem> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            return items.Count >= 2
                && items.All(item => item.Amount is not null)
                && Math.Abs(items.Sum(item => item.Amount!.Value)) < 0.005m;
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
