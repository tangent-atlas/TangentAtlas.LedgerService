// <copyright file="HoldingFoldTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias server;

namespace AtlasLedger.Tests
{
    using System;
    using System.Linq;
    using AtlasLedger.Model;
    using server::AtlasLedger.Model;
    using server::AtlasLedger.Model.Primary;
    using server::AtlasLedger.Server.Holdings;
    using Xunit;

    /// <summary>
    /// The fold that turns a holding's events into its state: processing order, the kinds of event, and prices.
    /// </summary>
    public sealed class HoldingFoldTests
    {
        private static readonly Guid Account = Guid.Parse("00000000-0000-0000-0000-00000000000a");

        private static readonly Guid Security = Guid.Parse("00000000-0000-0000-0000-00000000000b");

        private static readonly DateTimeOffset Morning = new DateTimeOffset(2026, 11, 6, 15, 0, 0, TimeSpan.Zero);

        private static long ledgerSequence;

        [Fact]
        public void PurchasesAddAndASaleRemovesCostAtTheAverage()
        {
            var position = Fold(trades: [Buy(100, 10m, at: Morning), Buy(100, 20m, at: Morning.AddHours(1)), Sell(50, 30m, at: Morning.AddHours(2))]);

            Assert.Equal(150m, position.Quantity);
            Assert.Equal(2_250m, position.CostBasis);   // 3,000 paid for 200; a quarter sold
            Assert.Equal(3, position.EventCount);
        }

        [Fact]
        public void EventsFoldInEffectiveTimeOrderNotTheOrderTheyArrived()
        {
            // The override (set to 100 at 10:00) was recorded first; a purchase effective at 9:00 arrived later.  The
            // purchase is folded first, so the override, later in effective time, has the last word.
            var setTo100 = Override(100, at: Morning.AddHours(1));
            var late = Buy(50, 10m, at: Morning);

            var position = Fold(trades: [late], overrides: [setTo100]);

            Assert.True(late.RowVersion > setTo100.RowVersion);
            Assert.Equal(100m, position.Quantity);
            Assert.True(position.IsOverridden);
        }

        [Fact]
        public void TiesBreakOnTheSourcesSequenceThenTheLedgers()
        {
            var overrideFirst = Override(100, at: Morning, sourceSequence: 1);
            var buySecond = Buy(10, 1m, at: Morning, sourceSequence: 2);
            Assert.Equal(110m, Fold(trades: [buySecond], overrides: [overrideFirst]).Quantity);

            var buyFirst = Buy(10, 1m, at: Morning, sourceSequence: 1);
            var overrideSecond = Override(100, at: Morning, sourceSequence: 2);
            Assert.Equal(100m, Fold(trades: [buyFirst], overrides: [overrideSecond]).Quantity);

            // No source sequence: the ledger's sequence decides, so the one recorded first is folded first.
            var recordedFirst = Buy(10, 1m, at: Morning);
            var recordedSecond = Override(100, at: Morning);
            Assert.Equal(100m, Fold(trades: [recordedFirst], overrides: [recordedSecond]).Quantity);
        }

        [Fact]
        public void ASplitMultipliesTheSharesHeldAtItsTimeAndKeepsTheCost()
        {
            var position = Fold(
                trades: [Buy(10, 100m, at: Morning), Buy(5, 50m, at: Morning.AddDays(2))],
                splits: [Split(2, 1, at: Morning.AddDays(1))]);

            Assert.Equal(25m, position.Quantity);   // 10 × 2, then 5 more
            Assert.Equal(1_250m, position.CostBasis);
        }

        [Fact]
        public void AnOpeningBalanceWithoutACostLeavesTheCostUnknown()
        {
            var known = Fold(openings: [Opening(10, 500m)], trades: [Buy(5, 10m, at: Morning.AddDays(1))]);
            var unknown = Fold(openings: [Opening(10, null)], trades: [Buy(5, 10m, at: Morning.AddDays(1))]);

            Assert.Equal(550m, known.CostBasis);
            Assert.Equal(15m, unknown.Quantity);
            Assert.Null(unknown.CostBasis);
        }

        [Fact]
        public void AnOverrideIsInForceFromItsEffectiveTimeUntilItExpires()
        {
            var expires = Morning.AddHours(8);

            Assert.False(HoldingFold.IsInForce(Morning, expires, Morning.AddTicks(-1)));
            Assert.True(HoldingFold.IsInForce(Morning, expires, Morning));
            Assert.True(HoldingFold.IsInForce(Morning, expires, expires.AddTicks(-1)));
            Assert.False(HoldingFold.IsInForce(Morning, expires, expires));
        }

        [Fact]
        public void PricesComeFromAnOverrideThenTheMarketThenTheLastTrade()
        {
            var trades = new[] { Buy(1, 10m, at: Morning), Buy(1, 11m, at: Morning.AddHours(1)) };
            var market = new PriceFact(new Price { Scheme = "Ticker", Code = "X", Last = 12m, AsOf = Morning.AddHours(2) });
            var badPrint = new PriceFact(new Price { Scheme = "Ticker", Code = "X", Last = 0m, AsOf = Morning.AddHours(2) });
            var priceOverride = new PriceOverrideFact(new PriceOverrideDto
            {
                PriceOverrideId = Guid.NewGuid(), SecurityId = Security, Price = 13m, ExpiresTime = Morning.AddDays(1),
                EffectiveTime = Morning, EnteredTime = Morning, Source = EventSource.Operator, SourceId = "fix-1", Reason = "Feed wrong", RowVersion = Next(),
            });

            Assert.Equal((13m, PriceSource.Override), Pair(HoldingFold.Price(Security, [priceOverride], market, trades)));
            Assert.Equal((12m, PriceSource.Market), Pair(HoldingFold.Price(Security, [], market, trades)));
            Assert.Equal((11m, PriceSource.LastTrade), Pair(HoldingFold.Price(Security, [], badPrint, trades)));
            Assert.Null(HoldingFold.Price(Security, [], null, []));
        }

        [Fact]
        public void CashIsTheEntriesPlusTheCashSideOfTheTrades()
        {
            var deposit = new CashEntryFact(new CashEntryDto
            {
                CashEntryId = Guid.NewGuid(), AccountId = Account, Kind = CashEntryKind.Deposit, Amount = 1_000m,
                EffectiveTime = Morning, EnteredTime = Morning, Source = EventSource.Simulator, SourceId = "deposit", RowVersion = Next(),
            });

            var cash = HoldingFold.Cash(Account, [deposit], [Buy(10, 20m, at: Morning.AddHours(1)), Sell(5, 30m, at: Morning.AddHours(2))]);

            Assert.Equal(950m, cash.Cash);   // 1,000 − 200 + 150
            Assert.Equal(3, cash.EventCount);
        }

        private static PositionState Fold(OpeningBalanceFact[]? openings = null, TradeFact[]? trades = null, SplitFact[]? splits = null, PositionOverrideFact[]? overrides = null) =>
            HoldingFold.Position(Account, Security, openings ?? [], trades ?? [], splits ?? [], overrides ?? []);

        private static (decimal, PriceSource)? Pair(EffectivePrice? price) => price is null ? null : (price.Price, price.Source);

        private static long Next() => System.Threading.Interlocked.Increment(ref ledgerSequence);

        private static TradeFact Buy(decimal quantity, decimal price, DateTimeOffset at, long? sourceSequence = null) => TradeOf(TradeSide.Buy, quantity, price, at, sourceSequence);

        private static TradeFact Sell(decimal quantity, decimal price, DateTimeOffset at) => TradeOf(TradeSide.Sell, quantity, price, at, null);

        private static TradeFact TradeOf(TradeSide side, decimal quantity, decimal price, DateTimeOffset at, long? sourceSequence) => new TradeFact(new TradeDto
        {
            TradeId = Guid.NewGuid(), AccountId = Account, SecurityId = Security, Side = side, Quantity = quantity, Price = price, Fees = 0m,
            NetAmount = side == TradeSide.Buy ? -(quantity * price) : quantity * price,
            EffectiveTime = at, EnteredTime = at, Source = EventSource.Simulator, SourceId = Guid.NewGuid().ToString(), SourceSequence = sourceSequence, RowVersion = Next(),
        });

        private static OpeningBalanceFact Opening(decimal quantity, decimal? costBasis) => new OpeningBalanceFact(new OpeningBalanceDto
        {
            OpeningBalanceId = Guid.NewGuid(), AccountId = Account, SecurityId = Security, Quantity = quantity, CostBasis = costBasis,
            EffectiveTime = Morning.AddDays(-30), EnteredTime = Morning, Source = EventSource.Simulator, SourceId = Guid.NewGuid().ToString(), RowVersion = Next(),
        });

        private static SplitFact Split(decimal numerator, decimal denominator, DateTimeOffset at) => new SplitFact(new SplitDto
        {
            SplitId = Guid.NewGuid(), SecurityId = Security, Numerator = numerator, Denominator = denominator,
            EffectiveTime = at, EnteredTime = at, Source = EventSource.Simulator, SourceId = Guid.NewGuid().ToString(), RowVersion = Next(),
        });

        private static PositionOverrideFact Override(decimal quantity, DateTimeOffset at, long? sourceSequence = null) => new PositionOverrideFact(new PositionOverrideDto
        {
            PositionOverrideId = Guid.NewGuid(), AccountId = Account, SecurityId = Security, Quantity = quantity, ExpiresTime = at.AddDays(1),
            EffectiveTime = at, EnteredTime = at, Source = EventSource.Operator, SourceId = Guid.NewGuid().ToString(), SourceSequence = sourceSequence,
            Reason = "The broker has it wrong until tomorrow.", RowVersion = Next(),
        });
    }
}
