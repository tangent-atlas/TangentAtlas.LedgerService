// <copyright file="ThermostatTests.cs" company="Tangent Atlas">
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
    using server::AtlasLedger.Server.Jobs;
    using server::AtlasLedger.Server.Orders;
    using Xunit;

    /// <summary>
    /// The thermostat: a fixed-weights model under a volatility target invests the target over its mix's measured
    /// volatility, at most all of it.
    /// </summary>
    public sealed class ThermostatTests
    {
        private static readonly Guid ModelId = Guid.NewGuid();

        private static readonly Guid Spy = Guid.NewGuid();

        private static readonly Guid Qqq = Guid.NewGuid();

        private static readonly Guid Dollar = Guid.NewGuid();

        private static readonly DateOnly Start = new(2026, 9, 1);

        [Fact]
        public void TheShareIsTheTargetOverTheMeasuredVolatility()
        {
            var prices = Zigzag(100m, 0.02m, 30);
            var share = Measure(Model(0.15m), [Weight(Spy, 1m)], Closes("SPY", prices));

            var expected = Thermostat.VolatilityOf(Returns(prices)[^21..]);
            Assert.Null(share.Reason);
            Assert.Equal(Math.Round((decimal)expected, 6), share.Volatility);
            Assert.Equal(Math.Round(0.15m / (decimal)expected, 6, MidpointRounding.ToZero), share.Share);
            Assert.InRange(share.Share!.Value, 0.01m, 0.99m);
            Assert.Equal(Start.AddDays(29), share.Through);
        }

        [Fact]
        public void ACalmMixIsFullyInvested()
        {
            var share = Measure(Model(0.15m), [Weight(Spy, 1m)], Closes("SPY", Zigzag(100m, 0.001m, 30)));

            Assert.Equal(1m, share.Share);
        }

        [Fact]
        public void TooFewClosesMeansWaiting()
        {
            var share = Measure(Model(0.15m), [Weight(Spy, 1m)], Closes("SPY", Zigzag(100m, 0.02m, 10)));

            Assert.Null(share.Share);
            Assert.Contains("needs 21 daily returns", share.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void ASplitIsNotADrop()
        {
            var prices = Zigzag(100m, 0.02m, 30);
            var split = prices.Select((price, index) => index >= 25 ? price / 2m : price).ToArray();
            var plain = Measure(Model(0.15m), [Weight(Spy, 1m)], Closes("SPY", prices));
            var halved = Measure(
                Model(0.15m),
                [Weight(Spy, 1m)],
                Closes("SPY", split),
                splits: [new SplitFact(new Split { Scheme = SymbolScheme.Ticker, Code = "SPY", Date = Start.AddDays(25), Ratio = 2m, AsOf = DateTimeOffset.UtcNow })]);

            Assert.Equal(plain.Share, halved.Share);
        }

        [Fact]
        public void ADividendIsNotADrop()
        {
            var prices = Zigzag(100m, 0.02m, 30);
            var paid = prices.Select((price, index) => index >= 25 ? price - 1m : price).ToArray();
            var dividend = new DividendFact(new Dividend { Scheme = SymbolScheme.Ticker, Code = "SPY", Currency = "USD", ExDate = Start.AddDays(25), Amount = 1m, AsOf = DateTimeOffset.UtcNow });
            var withDividend = Measure(Model(0.15m), [Weight(Spy, 1m)], Closes("SPY", paid), dividends: [dividend]);
            var without = Measure(Model(0.15m), [Weight(Spy, 1m)], Closes("SPY", paid));

            // Counted, the dividend leaves the day's return near the others; uncounted it would look like a 1% drop.
            Assert.NotEqual(without.Volatility, withDividend.Volatility);
            Assert.InRange(Math.Abs(withDividend.Volatility!.Value - Measure(Model(0.15m), [Weight(Spy, 1m)], Closes("SPY", prices)).Volatility!.Value), 0m, 0.01m);
        }

        [Fact]
        public void TwoHoldingsThatOffsetEachOtherAreCalm()
        {
            var up = Zigzag(100m, 0.02m, 30);
            var down = Zigzag(100m, -0.02m, 30);
            var share = Measure(Model(0.15m), [Weight(Spy, 0.5m), Weight(Qqq, 0.5m)], Closes("SPY", up).Concat(Closes("QQQ", down)));

            Assert.Equal(1m, share.Share);
        }

        [Fact]
        public void CashInTheWeightsIsLeftOutOfTheMix()
        {
            var prices = Zigzag(100m, 0.02m, 30);
            var alone = Measure(Model(0.15m), [Weight(Spy, 1m)], Closes("SPY", prices));
            var withCash = Measure(Model(0.15m), [Weight(Spy, 0.6m), Weight(Dollar, 0.4m)], Closes("SPY", prices));

            Assert.Equal(alone.Share, withCash.Share);
        }

        [Fact]
        public void ScaledWeightsLeaveTheRestInCash()
        {
            var scaled = Rebalancing.Scale(new Dictionary<Guid, decimal> { [Spy] = 0.6m, [Qqq] = 0.4m }, 0.5m);

            Assert.Equal(0.3m, scaled[Spy]);
            Assert.Equal(0.2m, scaled[Qqq]);
        }

        [Fact]
        public void ThePreviewHoldsTheTargetAndFullyInvestedIsTheMixItself()
        {
            var prices = Zigzag(100m, 0.02m, 300);
            var history = new TickerHistory("SPY", prices.Select((price, index) => (Start.AddDays(index), price)).ToDictionary(), new Dictionary<DateOnly, decimal>(), new Dictionary<DateOnly, decimal>());
            var rates = new Dictionary<DateOnly, decimal> { [Start] = 0.04m };

            // Fully invested: every day's return is SPY's, and the end is the compounded returns on the start.
            var full = Preview.Run([(history, 100m)], null, 21, rates, Start.AddDays(40), 1_000_000m);
            var returns = Returns(prices).Skip(39).ToArray();
            Assert.Null(full.Problem);
            Assert.Equal(1m, full.AverageInvested);
            Assert.Equal(returns.Length, full.Figures.Returns);
            Assert.Equal(Math.Round(1_000_000m * (decimal)returns.Aggregate(1d, (product, item) => product * (1d + item)), 2), full.EndingValue);

            // At 15%: the share is the target over SPY's (steady) volatility, so the preview runs at 15%.
            var held = Preview.Run([(history, 100m)], 0.15m, 21, rates, Start.AddDays(40), 1_000_000m);
            Assert.InRange(held.Figures.Volatility!.Value, 0.145m, 0.155m);   // the window's mix of up and down days shifts the measure slightly
            Assert.InRange(held.AverageInvested!.Value, 0.4m, 0.6m);
            Assert.NotEqual(full.EndingValue, held.EndingValue);

            // A target above SPY's volatility is fully invested: the same as no target.
            var high = Preview.Run([(history, 100m)], 1m, 21, rates, Start.AddDays(40), 1_000_000m);
            Assert.Equal(full.EndingValue, high.EndingValue);
        }

        [Fact]
        public void ThePreviewNeedsAWindowOfHistoryBeforeItStarts()
        {
            var prices = Zigzag(100m, 0.02m, 30);
            var history = new TickerHistory("SPY", prices.Select((price, index) => (Start.AddDays(index), price)).ToDictionary(), new Dictionary<DateOnly, decimal>(), new Dictionary<DateOnly, decimal>());

            var result = Preview.Run([(history, 100m)], 0.15m, 21, new Dictionary<DateOnly, decimal>(), Start, 1_000_000m);

            // From the start asked for, the window comes first: the first return is the 24th day's (21 returns known by the close before it).
            Assert.Equal(Start.AddDays(23), result.From);
            Assert.Equal(7, result.Figures.Returns);
        }

        private static ModelFact Model(decimal target) =>
            new(new ModelDto { ModelId = ModelId, Name = "SPY at 15%", ModelKind = ModelKind.FixedWeights, CashRule = CashRule.VolatilityTarget, VolatilityTarget = target, VolatilityWindow = 21, Band = 0.005m });

        private static WeightFact Weight(Guid assetId, decimal fraction) =>
            new(new WeightDto { ModelId = ModelId, AssetId = assetId, Fraction = fraction });

        private static CashShare Measure(ModelFact model, IEnumerable<WeightFact> weights, IEnumerable<ClosingPriceFact> closes, IEnumerable<DividendFact>? dividends = null, IEnumerable<SplitFact>? splits = null) =>
            CashShares.Measure(model, weights, Assets(), Symbols(), closes, dividends ?? [], splits ?? []);

        private static AssetFact[] Assets() =>
        [
            new(new AssetDto { AssetId = Spy, AssetType = AssetType.Fund, QuoteCurrency = "USD", QuantityFactor = 1m, PriceFactor = 1m }),
            new(new AssetDto { AssetId = Qqq, AssetType = AssetType.Fund, QuoteCurrency = "USD", QuantityFactor = 1m, PriceFactor = 1m }),
            new(new AssetDto { AssetId = Dollar, AssetType = AssetType.Cash, QuoteCurrency = "USD", QuantityFactor = 1m, PriceFactor = 1m }),
        ];

        private static SymbolFact[] Symbols() =>
        [
            new(new SymbolDto { Scheme = SymbolScheme.Ticker, Code = "SPY", NodeId = Spy }),
            new(new SymbolDto { Scheme = SymbolScheme.Ticker, Code = "QQQ", NodeId = Qqq }),
            new(new SymbolDto { Scheme = SymbolScheme.Currency, Code = "USD", NodeId = Dollar }),
        ];

        private static IEnumerable<ClosingPriceFact> Closes(string ticker, IReadOnlyList<decimal> prices) =>
            prices.Select((price, index) => new ClosingPriceFact(new ClosingPrice { Scheme = SymbolScheme.Ticker, Code = ticker, Currency = "USD", Date = Start.AddDays(index), Close = price, AsOf = MarketCalendar.CloseTime(Start.AddDays(index)) }));

        /// <summary>Prices moving up and down by a fraction each day, alternately, from a start.</summary>
        private static decimal[] Zigzag(decimal start, decimal step, int count)
        {
            var prices = new decimal[count];
            prices[0] = start;
            for (var index = 1; index < count; index++)
            {
                prices[index] = Math.Round(prices[index - 1] * (index % 2 == 1 ? 1m + step : 1m - step), 6);
            }

            return prices;
        }

        private static double[] Returns(IReadOnlyList<decimal> prices) =>
            prices.Zip(prices.Skip(1), (before, after) => (double)(after / before) - 1d).ToArray();
    }
}
