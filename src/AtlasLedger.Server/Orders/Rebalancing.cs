// <copyright file="Rebalancing.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Orders
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using AtlasLedger.Server.Holdings;

    /// <summary>
    /// One trade a rebalance wants: whole units of one asset, bought or sold.
    /// </summary>
    /// <param name="AssetId">The asset.</param>
    /// <param name="Side">Buy or sell.</param>
    /// <param name="Quantity">How many units.</param>
    public sealed record PlannedTrade(Guid AssetId, OrderSide Side, decimal Quantity);

    /// <summary>
    /// The arithmetic of a rebalance, as plain functions: the rule calls them, the tests call them directly.
    /// </summary>
    /// <remarks>
    /// The published rule: the account's value is every holding at its price (cash included), in the account's currency.
    /// Each asset's target is its weight times that value, in whole units rounded down; an asset held but not in the
    /// weights has a target of none.  What is held and what open orders will still bring or take is subtracted, and a
    /// trade smaller than the band (a fraction of the account's value) is skipped.  Cash is what is left: it is never
    /// traded for itself.  A rebalance waits until every asset it needs has a current price (the market's today, or an
    /// operator's override in force); it never trades on a stale price.
    /// </remarks>
    public static class Rebalancing
    {
        private static readonly TimeZoneInfo Exchange = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        /// <summary>
        /// The trading day a time falls on, in New York.
        /// </summary>
        /// <param name="time">The time.</param>
        /// <returns>The day.</returns>
        public static DateOnly TradingDay(DateTimeOffset time) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, Exchange).DateTime);

        /// <summary>
        /// When a trading day starts, as a time: midnight in New York.
        /// </summary>
        /// <param name="day">The day.</param>
        /// <returns>The time.</returns>
        public static DateTimeOffset StartOf(DateOnly day)
        {
            var midnight = day.ToDateTime(TimeOnly.MinValue);
            return new DateTimeOffset(midnight, Exchange.GetUtcOffset(midnight));
        }

        /// <summary>
        /// Whether an account's rebalance is due at a time: on or after its time of day in New York, on a weekday unless the
        /// setting says every day.  (Market holidays are not known yet: on one, the broker refuses or holds the orders.)
        /// </summary>
        /// <param name="now">The time.</param>
        /// <param name="setting">The account's setting.</param>
        /// <returns>True if due.</returns>
        public static bool IsDue(DateTimeOffset now, RebalanceSettingFact setting)
        {
            ArgumentNullException.ThrowIfNull(setting);
            var local = TimeZoneInfo.ConvertTime(now, Exchange);
            var isWeekday = local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
            return (isWeekday || !setting.WeekdaysOnly) && (local.Hour * 60) + local.Minute >= setting.TimeOfDay;
        }

        /// <summary>
        /// The weights in force on a day: the latest date on or before it.
        /// </summary>
        /// <param name="targets">An account's target weights, of every date.</param>
        /// <param name="day">The trading day.</param>
        /// <returns>Each asset's weight; empty if none is in force yet.</returns>
        public static IReadOnlyDictionary<Guid, decimal> WeightsInForce(IEnumerable<TargetWeightFact> targets, DateOnly day)
        {
            ArgumentNullException.ThrowIfNull(targets);
            var list = targets.Where(target => target.EffectiveDate <= day).ToArray();
            if (list.Length == 0)
            {
                return new Dictionary<Guid, decimal>();
            }

            var latest = list.Max(target => target.EffectiveDate);
            return list.Where(target => target.EffectiveDate == latest).ToDictionary(target => target.AssetId, target => target.Weight);
        }

        /// <summary>
        /// Plans an account's rebalance.
        /// </summary>
        /// <param name="account">The account.</param>
        /// <param name="weights">The weights in force.</param>
        /// <param name="positions">The account's holdings.</param>
        /// <param name="assets">The tenant's assets, by ID.</param>
        /// <param name="prices">The effective prices, by asset.</param>
        /// <param name="rates">The rates between currencies.</param>
        /// <param name="open">What open orders will still change, by asset: positive to buy, negative to sell.</param>
        /// <param name="band">The smallest trade, as a fraction of the account's value.</param>
        /// <param name="notBefore">A market price older than this is stale (the start of the trading day).</param>
        /// <returns>The trades (possibly none), or null when it must wait: a price is missing or stale, or the account is worth nothing.</returns>
        public static IReadOnlyList<PlannedTrade>? Plan(
            AccountFact account,
            IReadOnlyDictionary<Guid, decimal> weights,
            IEnumerable<PositionState> positions,
            IReadOnlyDictionary<Guid, AssetFact> assets,
            IReadOnlyDictionary<Guid, EffectivePrice> prices,
            IReadOnlyDictionary<(string From, string To), decimal> rates,
            IReadOnlyDictionary<Guid, decimal> open,
            decimal band,
            DateTimeOffset notBefore)
        {
            ArgumentNullException.ThrowIfNull(account);
            ArgumentNullException.ThrowIfNull(weights);
            ArgumentNullException.ThrowIfNull(positions);
            ArgumentNullException.ThrowIfNull(assets);
            ArgumentNullException.ThrowIfNull(prices);
            ArgumentNullException.ThrowIfNull(rates);
            ArgumentNullException.ThrowIfNull(open);
            var held = positions.Where(position => position.AccountId == account.AccountId && position.Quantity != 0m).ToArray();

            // The account's value: every holding, cash included.  Any holding without a value means waiting.
            var value = 0m;
            foreach (var position in held)
            {
                if (HoldingFold.MarketValue(position, prices.GetValueOrDefault(position.AssetId), rates) is not { } worth)
                {
                    return null;
                }

                value += worth;
            }

            if (value <= 0m)
            {
                return null;
            }

            var quantities = held.ToDictionary(position => position.AssetId, position => position.Quantity);
            var traded = weights.Keys
                .Concat(held.Where(position => position.AssetType != AssetType.Cash).Select(position => position.AssetId))
                .Distinct()
                .Where(assetId => assets.TryGetValue(assetId, out var asset) && asset.AssetType != AssetType.Cash)
                .OrderBy(assetId => assetId)
                .ToArray();
            var trades = new List<PlannedTrade>();
            foreach (var assetId in traded)
            {
                var asset = assets[assetId];
                if (prices.GetValueOrDefault(assetId) is not { } price
                    || !IsCurrent(price, notBefore)
                    || !rates.TryGetValue((asset.QuoteCurrency, account.Currency), out var rate))
                {
                    return null;
                }

                var unit = price.Price * asset.QuantityFactor * asset.PriceFactor * rate;
                if (unit <= 0m)
                {
                    return null;
                }

                var target = Math.Floor(value * weights.GetValueOrDefault(assetId) / unit);
                var delta = target - quantities.GetValueOrDefault(assetId) - open.GetValueOrDefault(assetId);
                if (delta == 0m || Math.Abs(delta) * unit < band * value)
                {
                    continue;
                }

                trades.Add(new PlannedTrade(assetId, delta > 0m ? OrderSide.Buy : OrderSide.Sell, Math.Abs(delta)));
            }

            return trades;
        }

        /// <summary>
        /// What an account's open orders will still change, by asset: the part of each allocation not yet filled, of orders
        /// with something still working at a broker (bought positive, sold negative).
        /// </summary>
        /// <param name="accountId">The account.</param>
        /// <param name="orders">The tenant's order states.</param>
        /// <returns>The quantities.</returns>
        public static IReadOnlyDictionary<Guid, decimal> OpenQuantities(Guid accountId, IEnumerable<OrderState> orders)
        {
            ArgumentNullException.ThrowIfNull(orders);
            return orders
                .Where(order => order.BrokerOrders.Any(brokerOrder => brokerOrder.Open > 0m))
                .SelectMany(order => order.Allocations
                    .Where(allocation => allocation.AccountId == accountId)
                    .Select(allocation => (order.AssetId, Quantity: order.Side == OrderSide.Buy ? allocation.Remaining : -allocation.Remaining)))
                .GroupBy(item => item.AssetId)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));
        }

        /// <summary>
        /// The key of a rebalance's order or broker order for an asset: the same account, day and asset always give the same
        /// key (a name-based UUID, version 8, SHA-256).
        /// </summary>
        /// <param name="accountId">The account.</param>
        /// <param name="day">The trading day.</param>
        /// <param name="assetId">The asset.</param>
        /// <param name="what">"order" or "broker-order".</param>
        /// <returns>The key.</returns>
        public static Guid KeyFor(Guid accountId, DateOnly day, Guid assetId, string what)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"tangent-atlas/rebalance/{accountId:N}/{day:yyyy-MM-dd}/{assetId:N}/{what}"));
            hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
            return new Guid(hash.AsSpan(0, 16), bigEndian: true);
        }

        private static bool IsCurrent(EffectivePrice price, DateTimeOffset notBefore) =>
            price.Source == PriceSource.Override || (price.Source == PriceSource.Market && price.AsOf >= notBefore);
    }
}
