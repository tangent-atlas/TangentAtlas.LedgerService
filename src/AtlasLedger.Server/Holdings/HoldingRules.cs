// <copyright file="HoldingRules.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Holdings
{
    using System.Collections.Generic;
    using System.Linq;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using NRules.Fluent.Dsl;

    /// <summary>
    /// A line of an ordinary transaction is posted: it counts from the moment it is recorded.
    /// </summary>
    [Name("Holdings.PostedLine")]
    public sealed class PostedLineRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            TransactionLineFact line = default!;
            TransactionFact transaction = default!;

            this.When()
                .Match(() => line)
                .Match(() => transaction, candidate => candidate.TransactionId == line.TransactionId, candidate => candidate.Kind != TransactionKind.Override);
            this.Then()
                .Yield(_ => new PostedLine(line, transaction));
        }
    }

    /// <summary>
    /// A line of an override is posted while the override is in force, from its effective time until it expires.  Kept apart
    /// from the holdings so that the clock moving on re-checks only the overrides.
    /// </summary>
    [Name("Holdings.PostedOverrideLine")]
    public sealed class PostedOverrideLineRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            ClockFact clock = default!;
            TransactionLineFact line = default!;
            TransactionFact transaction = default!;

            this.When()
                .Match(() => clock)
                .Match(() => line)
                .Match(
                    () => transaction,
                    candidate => candidate.TransactionId == line.TransactionId,
                    candidate => candidate.Kind == TransactionKind.Override,
                    candidate => HoldingFold.IsInForce(candidate.EffectiveTime, candidate.ExpiresTime, clock.Now));
            this.Then()
                .Yield(_ => new PostedLine(line, transaction));
        }
    }

    /// <summary>
    /// A holding: every posted line for an account and an asset, folded in processing order.  Cash, stocks, funds, bonds,
    /// options, futures and crypto alike.
    /// </summary>
    [Name("Holdings.Position")]
    public sealed class PositionRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            AccountFact account = default!;
            AssetFact asset = default!;
            IEnumerable<PostedLine> lines = default!;

            this.When()
                .Match(() => account)
                .Match(() => asset)
                .Query(() => lines, query => query
                    .Match<PostedLine>(item => item.Line.AccountId == account.AccountId, item => item.Line.AssetId == asset.AssetId)
                    .Collect())
                .Having(() => lines.Any());
            this.Then()
                .Yield(_ => HoldingFold.Position(account, asset, lines));
        }
    }

    /// <summary>
    /// A price override is in force from its effective time until it expires.
    /// </summary>
    [Name("Holdings.PriceOverrideInForce")]
    public sealed class PriceOverrideInForceRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            ClockFact clock = default!;
            PriceOverrideFact item = default!;

            this.When()
                .Match(() => clock)
                .Match(() => item, candidate => HoldingFold.IsInForce(candidate.EffectiveTime, candidate.ExpiresTime, clock.Now));
            this.Then()
                .Yield(_ => new PriceOverrideInForce(item));
        }
    }

    /// <summary>
    /// A rate between two currencies: the latest price of one in the other, from the price table (a currency in itself is
    /// 1, from the initial prices; between currencies, from the feed).  Nothing is inverted or chained: a rate is used only
    /// as published.
    /// </summary>
    [Name("Holdings.CurrencyRate")]
    public sealed class CurrencyRateRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            PriceFact price = default!;

            this.When()
                .Match(() => price, candidate => candidate.Scheme == SymbolScheme.Currency, candidate => candidate.Last > 0m);
            this.Then()
                .Yield(_ => new CurrencyRate(price.Code, price.Currency, price.Last));
        }
    }

    /// <summary>
    /// The price of an asset, cash included, in its quote currency: an override in force, else the latest price published
    /// for any of its symbols in that currency (a ticker's from the market feed; a currency's from the initial prices), else
    /// the price of its last purchase or sale in any of the tenant's accounts.
    /// </summary>
    [Name("Holdings.Price")]
    public sealed class PriceRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            AssetFact asset = default!;
            IEnumerable<SymbolFact> symbols = default!;
            IEnumerable<PriceFact> markets = default!;
            IEnumerable<PriceOverrideInForce> overrides = default!;
            IEnumerable<PostedLine> exchanges = default!;

            this.When()
                .Match(() => asset)
                .Query(() => symbols, query => query
                    .Match<SymbolFact>(item => item.NodeId == asset.AssetId)
                    .Collect())
                .Query(() => markets, query => query
                    .Match<PriceFact>(item => item.Currency == asset.QuoteCurrency, item => symbols.Any(symbol => symbol.Scheme == item.Scheme && symbol.Code == item.Code))
                    .Collect())
                .Query(() => overrides, query => query
                    .Match<PriceOverrideInForce>(item => item.Override.AssetId == asset.AssetId)
                    .Collect())
                .Query(() => exchanges, query => query
                    .Match<PostedLine>(item => item.Line.AssetId == asset.AssetId, item => HoldingFold.Exchanges.Contains(item.Transaction.Kind))
                    .Collect())
                .Having(() => markets.Any(market => market.Last > 0m) || overrides.Any() || exchanges.Any());
            this.Then()
                .Yield(_ => HoldingFold.Price(
                    asset,
                    overrides.Select(item => item.Override),
                    markets.Where(market => market.Last > 0m).OrderByDescending(market => market.AsOf).FirstOrDefault(),
                    exchanges)!);
        }
    }
}
