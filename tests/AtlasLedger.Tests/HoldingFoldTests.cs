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
    /// The fold that turns a holding's transaction lines into its state, prices, market value with both factors, and the
    /// balance of an exchange.
    /// </summary>
    public sealed class HoldingFoldTests
    {
        private static readonly Guid Account = Guid.Parse("00000000-0000-0000-0000-00000000000a");

        private static readonly AssetFact Stock = AssetOf(Guid.Parse("00000000-0000-0000-0000-00000000000b"), AssetType.Equity, ValueFactor.One, ValueFactor.One);

        private static readonly AssetFact Dollar = AssetOf(Guid.Parse("00000000-0000-0000-0000-00000000000c"), AssetType.Cash, ValueFactor.One, ValueFactor.One);

        private static readonly AssetFact Bond = AssetOf(Guid.Parse("00000000-0000-0000-0000-00000000000d"), AssetType.FixedIncome, ValueFactor.One, ValueFactor.PerHundred);

        private static readonly DateTimeOffset Morning = new DateTimeOffset(2026, 11, 6, 15, 0, 0, TimeSpan.Zero);

        private static long ledgerSequence;

        [Fact]
        public void APurchaseAddsItsCostAndASaleRemovesCostAtTheAverage()
        {
            var lines = Buy(100, 10m, Morning).Concat(Buy(100, 20m, Morning.AddHours(1))).Concat(Sell(50, 30m, Morning.AddHours(2))).ToArray();

            var stock = HoldingFold.Position(Account, Stock, lines.Where(line => line.Line.AssetId == Stock.AssetId));

            Assert.Equal(150m, stock.Quantity);
            Assert.Equal(2_250m, stock.CostBasis);   // 3,000 paid for 200; a quarter sold
            Assert.Equal(3, stock.LineCount);
        }

        [Fact]
        public void CashIsHeldLikeAnyOtherAsset()
        {
            var lines = Deposit(1_000m, Morning).Concat(Buy(10, 20m, Morning.AddHours(1))).Concat(Sell(5, 30m, Morning.AddHours(2))).ToArray();

            var dollars = HoldingFold.Position(Account, Dollar, lines.Where(line => line.Line.AssetId == Dollar.AssetId));

            Assert.Equal(950m, dollars.Quantity);   // 1,000 − 200 + 150
            Assert.Equal(AssetType.Cash, dollars.AssetType);
        }

        [Fact]
        public void LinesFoldInEffectiveTimeOrderNotTheOrderTheyArrived()
        {
            // The override (set to 100 at 10:00) was recorded first; a purchase effective at 9:00 arrived later.  The purchase
            // is folded first, so the override, later in effective time, has the last word.
            var setTo100 = Override(100, Morning.AddHours(1));
            var late = Buy(50, 10m, Morning);

            var stock = HoldingFold.Position(Account, Stock, late.Concat(setTo100).Where(line => line.Line.AssetId == Stock.AssetId));

            Assert.True(late[0].Transaction.RowVersion > setTo100[0].Transaction.RowVersion);
            Assert.Equal(100m, stock.Quantity);
            Assert.True(stock.IsOverridden);
        }

        [Fact]
        public void TiesBreakOnTheSourcesSequenceThenTheLedgers()
        {
            Assert.Equal(110m, Quantity(Override(100, Morning, sourceSequence: 1), Buy(10, 1m, Morning, sourceSequence: 2)));
            Assert.Equal(100m, Quantity(Buy(10, 1m, Morning, sourceSequence: 1), Override(100, Morning, sourceSequence: 2)));

            // No source sequence: the ledger's sequence decides, so the transaction recorded first is folded first.
            var recordedFirst = Buy(10, 1m, Morning);
            var recordedSecond = Override(100, Morning);
            Assert.Equal(100m, Quantity(recordedFirst, recordedSecond));
        }

        [Fact]
        public void ASplitAddsUnitsAndKeepsTheCost()
        {
            var lines = Buy(10, 100m, Morning)
                .Concat(Lines(TransactionKind.Split, Morning.AddDays(1), null, null, (Stock, LineEffect.Change, 10m, 0m)))
                .Concat(Buy(5, 50m, Morning.AddDays(2)));

            var stock = HoldingFold.Position(Account, Stock, lines.Where(line => line.Line.AssetId == Stock.AssetId));

            Assert.Equal(25m, stock.Quantity);   // 10, doubled, then 5 more
            Assert.Equal(1_250m, stock.CostBasis);
        }

        [Fact]
        public void AnOpeningBalanceWithoutAnAmountLeavesTheCostUnknown()
        {
            var known = Lines(TransactionKind.OpeningBalance, Morning.AddDays(-30), null, null, (Stock, LineEffect.Change, 10m, 500m));
            var unknown = Lines(TransactionKind.OpeningBalance, Morning.AddDays(-30), null, null, (Stock, LineEffect.Change, 10m, null));

            Assert.Equal(550m, HoldingFold.Position(Account, Stock, known.Concat(Buy(5, 10m, Morning)).Where(line => line.Line.AssetId == Stock.AssetId)).CostBasis);
            var position = HoldingFold.Position(Account, Stock, unknown.Concat(Buy(5, 10m, Morning)).Where(line => line.Line.AssetId == Stock.AssetId));
            Assert.Equal(15m, position.Quantity);
            Assert.Null(position.CostBasis);
        }

        [Fact]
        public void ATemporaryEntryIsInForceFromItsEffectiveTimeUntilItExpires()
        {
            var expires = Morning.AddHours(8);

            Assert.False(HoldingFold.IsInForce(Morning, expires, Morning.AddTicks(-1)));
            Assert.True(HoldingFold.IsInForce(Morning, expires, Morning));
            Assert.True(HoldingFold.IsInForce(Morning, expires, expires.AddTicks(-1)));
            Assert.False(HoldingFold.IsInForce(Morning, expires, expires));
            Assert.True(HoldingFold.IsInForce(Morning, null, Morning.AddYears(10)));
        }

        [Fact]
        public void PricesComeFromAnOverrideThenTheMarketThenTheLastExchange()
        {
            var exchanges = Buy(1, 10m, Morning).Concat(Buy(1, 11m, Morning.AddHours(1))).Where(line => line.Line.AssetId == Stock.AssetId).ToArray();
            var market = new PriceFact(new Price { Scheme = SymbolScheme.Ticker, Code = "X", Currency = CurrencyCode.Valuation, Last = 12m, AsOf = Morning.AddHours(2) });
            var badPrint = new PriceFact(new Price { Scheme = SymbolScheme.Ticker, Code = "X", Currency = CurrencyCode.Valuation, Last = 0m, AsOf = Morning.AddHours(2) });
            var priceOverride = new PriceOverrideFact(new PriceOverrideDto
            {
                PriceOverrideId = Guid.NewGuid(), AssetId = Stock.AssetId, Price = 13m, ExpiresTime = Morning.AddDays(1),
                EffectiveTime = Morning, EnteredTime = Morning, Source = EventSource.Operator, SourceId = "fix-1", Reason = "Feed wrong", RowVersion = Next(),
            });

            Assert.Equal((13m, PriceSource.Override), Pair(HoldingFold.Price(Stock, [priceOverride], market, exchanges)));
            Assert.Equal((12m, PriceSource.Market), Pair(HoldingFold.Price(Stock, [], market, exchanges)));
            Assert.Equal((11m, PriceSource.LastTrade), Pair(HoldingFold.Price(Stock, [], badPrint, exchanges)));
            Assert.Null(HoldingFold.Price(Stock, [], null, []));
        }

        [Fact]
        public void MarketValueUsesBothFactors()
        {
            // Two option contracts on 100 shares each at $3.50; $10,000 of a bond at 98.50 per 100 of face value.
            Assert.Equal(700m, HoldingFold.MarketValue(2m, ValueFactor.EquityOptionContract, 3.50m, ValueFactor.One, 1m));
            Assert.Equal(9_850m, HoldingFold.MarketValue(10_000m, ValueFactor.One, 98.50m, ValueFactor.PerHundred, 1m));

            // 100 shares quoted at £10 when a pound costs $1.25; and a dollar, priced at one of itself.
            Assert.Equal(1_250m, HoldingFold.MarketValue(100m, ValueFactor.One, 10m, ValueFactor.One, 1.25m));
            Assert.Equal(500m, HoldingFold.MarketValue(500m, ValueFactor.One, 1m, ValueFactor.One, 1m));

            // And the quoted price is recovered from what an exchange cost.
            var bought = Lines(TransactionKind.Buy, Morning, null, null, (Bond, LineEffect.Change, 10_000m, 9_850m), (Dollar, LineEffect.Change, -9_850m, -9_850m));
            Assert.Equal(98.50m, HoldingFold.QuotedPrice(Bond, bought[0].Line));
        }

        [Fact]
        public void AnExchangeBalancesWhenWhatIsGivenEqualsWhatIsReceived()
        {
            Assert.True(HoldingFold.IsBalanced(Buy(10, 20m, Morning).Select(line => line.Line).ToArray()));
            Assert.False(HoldingFold.IsBalanced(Lines(TransactionKind.Buy, Morning, null, null, (Stock, LineEffect.Change, 10m, 200m), (Dollar, LineEffect.Change, -190m, -190m)).Select(line => line.Line).ToArray()));
            Assert.False(HoldingFold.IsBalanced(Lines(TransactionKind.Buy, Morning, null, null, (Stock, LineEffect.Change, 10m, 200m)).Select(line => line.Line).ToArray()));
            Assert.False(HoldingFold.IsBalanced(Lines(TransactionKind.Buy, Morning, null, null, (Stock, LineEffect.Change, 10m, null), (Dollar, LineEffect.Change, -200m, -200m)).Select(line => line.Line).ToArray()));
        }

        private static decimal Quantity(params PostedLine[][] transactions) =>
            HoldingFold.Position(Account, Stock, transactions.SelectMany(lines => lines).Where(line => line.Line.AssetId == Stock.AssetId)).Quantity;

        private static (decimal, PriceSource)? Pair(EffectivePrice? price) => price is null ? null : (price.Price, price.Source);

        private static long Next() => System.Threading.Interlocked.Increment(ref ledgerSequence);

        private static AssetFact AssetOf(Guid assetId, AssetType assetType, decimal quantityFactor, decimal priceFactor) =>
            new AssetFact(new AssetDto { AssetId = assetId, AssetType = assetType, QuoteCurrency = CurrencyCode.Valuation, QuantityFactor = quantityFactor, PriceFactor = priceFactor });

        private static PostedLine[] Deposit(decimal dollars, DateTimeOffset at) =>
            Lines(TransactionKind.Deposit, at, null, null, (Dollar, LineEffect.Change, dollars, dollars));

        private static PostedLine[] Buy(decimal shares, decimal price, DateTimeOffset at, long? sourceSequence = null) =>
            Lines(TransactionKind.Buy, at, sourceSequence, null, (Stock, LineEffect.Change, shares, shares * price), (Dollar, LineEffect.Change, -(shares * price), -(shares * price)));

        private static PostedLine[] Sell(decimal shares, decimal price, DateTimeOffset at) =>
            Lines(TransactionKind.Sell, at, null, null, (Stock, LineEffect.Change, -shares, -(shares * price)), (Dollar, LineEffect.Change, shares * price, shares * price));

        private static PostedLine[] Override(decimal shares, DateTimeOffset at, long? sourceSequence = null) =>
            Lines(TransactionKind.Override, at, sourceSequence, at.AddDays(1), (Stock, LineEffect.SetTo, shares, null));

        private static PostedLine[] Lines(TransactionKind kind, DateTimeOffset at, long? sourceSequence, DateTimeOffset? expires, params (AssetFact Asset, LineEffect Effect, decimal Quantity, decimal? Amount)[] lines)
        {
            var transaction = new TransactionFact(new TransactionDto
            {
                TransactionId = Guid.NewGuid(), Kind = kind, ExpiresTime = expires, EffectiveTime = at, EnteredTime = at,
                Source = kind == TransactionKind.Override ? EventSource.Operator : EventSource.Simulator, SourceId = Guid.NewGuid().ToString(),
                SourceSequence = sourceSequence, RowVersion = Next(),
            });
            return lines
                .Select((line, index) => new PostedLine(
                    new TransactionLineFact(new TransactionLineDto
                    {
                        TransactionId = transaction.TransactionId, LineNumber = index + 1, AccountId = Account, AssetId = line.Asset.AssetId,
                        Effect = line.Effect, Quantity = line.Quantity, Amount = line.Amount, RowVersion = transaction.RowVersion,
                    }),
                    transaction))
                .ToArray();
        }
    }
}
