// <copyright file="HoldingRules.cs" company="Tangent Atlas">
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
    /// <remarks>
    /// Starts from the lines, grouped by account and asset, so every join has a key and a holding exists only where a line
    /// does.  Starting from "any account and any asset" made the network hold every pair of them and test each new line
    /// against all of them: measured November 10, 200 seconds to load 200 accounts × 1,000 assets that way, 0.2 seconds
    /// this way (and under a second for 500 × 2,000).
    /// </remarks>
    [Name("Holdings.Position")]
    public sealed class PositionRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            IGrouping<(Guid AccountId, Guid AssetId), PostedLine> lines = default!;
            AccountFact account = default!;
            AssetFact asset = default!;

            this.When()
                .Query(() => lines, query => query
                    .Match<PostedLine>()
                    .GroupBy(item => ValueTuple.Create(item.Line.AccountId, item.Line.AssetId)))
                .Match(() => account, candidate => candidate.AccountId == lines.Key.AccountId)
                .Match(() => asset, candidate => candidate.AssetId == lines.Key.AssetId);
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

    /// <summary>
    /// An account's net contributions: its deposits less its withdrawals, from the posted lines grouped by account.
    /// </summary>
    [Name("Holdings.AccountFlow")]
    public sealed class AccountFlowRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            IGrouping<Guid, PostedLine> lines = default!;

            this.When()
                .Query(() => lines, query => query
                    .Match<PostedLine>(item => item.Transaction.Kind == TransactionKind.Deposit || item.Transaction.Kind == TransactionKind.Withdrawal)
                    .GroupBy(item => item.Line.AccountId));
            this.Then()
                .Yield(_ => new AccountFlow(lines.Key, lines.Sum(item => item.Line.Amount ?? 0m)));
        }
    }
}
