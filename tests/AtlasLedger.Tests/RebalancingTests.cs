// <copyright file="RebalancingTests.cs" company="Tangent Atlas">
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
    /// The arithmetic of a rebalance: when it is due, which weights are in force, and the trades it plans.
    /// </summary>
    public sealed class RebalancingTests
    {
        // 3:50 pm in New York on Monday, November 9, 2026 (standard time).
        private static readonly DateTimeOffset Monday = new DateTimeOffset(2026, 11, 9, 20, 50, 0, TimeSpan.Zero);

        private static readonly AccountFact Account = new AccountFact(new AccountDto { AccountId = Guid.NewGuid(), AccountRole = AccountRole.ManagedAccount, AccountType = AccountType.Brokerage, Currency = CurrencyCode.UnitedStatesDollar, ModelId = Guid.NewGuid() });

        private static readonly AssetFact Dollar = Asset(AssetType.Cash);

        private static readonly AssetFact Alpha = Asset(AssetType.Equity);

        private static readonly AssetFact Beta = Asset(AssetType.Equity);

        private static readonly IReadOnlyDictionary<(string From, string To), decimal> Rates = new Dictionary<(string From, string To), decimal> { [("USD", "USD")] = 1m };

        [Fact]
        public void TheTradingDayIsInNewYork()
        {
            Assert.Equal(new DateOnly(2026, 11, 9), Rebalancing.TradingDay(Monday.AddHours(3)));   // 11:50 pm in New York, still Monday
        }

        [Fact]
        public void AnAccountsWeightsAreItsModels()
        {
            var (joe10, other) = (Guid.NewGuid(), Guid.NewGuid());
            var weights = new[] { Weight(joe10, Alpha, 0.3m), Weight(joe10, Beta, 0.2m), Weight(other, Alpha, 0.9m) };
            Assert.Equal([(Alpha.AssetId, 0.3m), (Beta.AssetId, 0.2m)], Rebalancing.WeightsOf(weights, joe10).OrderBy(item => item.Value).Reverse().Select(item => (item.Key, item.Value)));
            Assert.Empty(Rebalancing.WeightsOf(weights, Guid.NewGuid()));   // a model with no weights
        }

        [Fact]
        public void FromCashItBuysEachTargetInWholeShares()
        {
            var plan = Plan(
                new Dictionary<Guid, decimal> { [Alpha.AssetId] = 0.3m, [Beta.AssetId] = 0.2m },
                [Position(Dollar, 1_000_000m)],
                new Dictionary<Guid, decimal> { [Alpha.AssetId] = 100m, [Beta.AssetId] = 33.33m });

            Assert.NotNull(plan);
            Assert.Equal(
                new[] { (Alpha.AssetId, OrderSide.Buy, 3_000m), (Beta.AssetId, OrderSide.Buy, 6_000m) }.OrderBy(trade => trade.AssetId),
                plan.Select(trade => (trade.AssetId, trade.Side, trade.Quantity)));   // 200,000 ÷ 33.33 = 6,000.6
        }

        [Fact]
        public void ItSellsWhatTheTargetsLeaveOutAndSkipsTradesInsideTheBand()
        {
            // $1,000,000 in all: Alpha 3,010 shares ($301,000, target $300,000: a $1,000 trade, inside a 0.5% band), Beta held but left out.
            var plan = Plan(
                new Dictionary<Guid, decimal> { [Alpha.AssetId] = 0.3m },
                [Position(Dollar, 689_000m), Position(Alpha, 3_010m), Position(Beta, 300m)],
                new Dictionary<Guid, decimal> { [Alpha.AssetId] = 100m, [Beta.AssetId] = 33.33m },
                band: 0.005m);

            Assert.Equal([(Beta.AssetId, OrderSide.Sell, 300m)], plan!.Select(trade => (trade.AssetId, trade.Side, trade.Quantity)));
        }

        [Fact]
        public void AModelWithNoWeightsIsAllCash()
        {
            var plan = Plan(
                new Dictionary<Guid, decimal>(),
                [Position(Dollar, 700_000m), Position(Alpha, 3_000m)],
                new Dictionary<Guid, decimal> { [Alpha.AssetId] = 100m });

            Assert.Equal([(Alpha.AssetId, OrderSide.Sell, 3_000m)], plan!.Select(trade => (trade.AssetId, trade.Side, trade.Quantity)));
        }

        [Fact]
        public void OpenOrdersCountAsAlreadyBought()
        {
            var plan = Plan(
                new Dictionary<Guid, decimal> { [Alpha.AssetId] = 0.3m },
                [Position(Dollar, 1_000_000m)],
                new Dictionary<Guid, decimal> { [Alpha.AssetId] = 100m },
                open: new Dictionary<Guid, decimal> { [Alpha.AssetId] = 2_000m });

            Assert.Equal([(Alpha.AssetId, OrderSide.Buy, 1_000m)], plan!.Select(trade => (trade.AssetId, trade.Side, trade.Quantity)));
        }

        [Fact]
        public void ItWaitsForACurrentPriceOfEverythingItTrades()
        {
            var weights = new Dictionary<Guid, decimal> { [Alpha.AssetId] = 0.3m };
            Assert.Null(Plan(weights, [Position(Dollar, 1_000_000m)], new Dictionary<Guid, decimal>()));   // no price
            Assert.Null(Plan(weights, [Position(Dollar, 1_000_000m)], new Dictionary<Guid, decimal> { [Alpha.AssetId] = 100m }, asOf: Monday.AddDays(-1)));   // yesterday's
            Assert.Null(Plan(weights, [Position(Dollar, 1_000_000m)], new Dictionary<Guid, decimal> { [Alpha.AssetId] = 100m }, source: PriceSource.LastTrade));
        }

        private static IReadOnlyList<PlannedTrade>? Plan(
            IReadOnlyDictionary<Guid, decimal> weights,
            PositionState[] positions,
            IReadOnlyDictionary<Guid, decimal> prices,
            decimal band = 0m,
            IReadOnlyDictionary<Guid, decimal>? open = null,
            PriceSource source = PriceSource.Market,
            DateTimeOffset? asOf = null)
        {
            var effective = prices
                .Select(item => new EffectivePrice(item.Key, item.Value, "USD", asOf ?? Monday, source))
                .Append(new EffectivePrice(Dollar.AssetId, 1m, "USD", Monday.AddDays(-30), PriceSource.Market))
                .ToDictionary(price => price.AssetId);
            return Rebalancing.Plan(
                Account,
                weights,
                positions,
                new[] { Dollar, Alpha, Beta }.ToDictionary(asset => asset.AssetId),
                effective,
                Rates,
                open ?? new Dictionary<Guid, decimal>(),
                band,
                Rebalancing.StartOf(Rebalancing.TradingDay(Monday)));
        }

        private static AssetFact Asset(AssetType type) =>
            new AssetFact(new AssetDto { AssetId = Guid.NewGuid(), AssetType = type, QuoteCurrency = "USD", QuantityFactor = 1m, PriceFactor = 1m });

        private static PositionState Position(AssetFact asset, decimal quantity) =>
            new PositionState(Account.AccountId, asset.AssetId, asset.AssetType, "USD", "USD", 1m, 1m, quantity, null, false, Monday, 1);

        private static WeightFact Weight(Guid modelId, AssetFact asset, decimal fraction) =>
            new WeightFact(new WeightDto { ModelId = modelId, AssetId = asset.AssetId, Fraction = fraction });
    }
}
