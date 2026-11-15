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
    /// Posts each account-activity event's items, unless the event is an override (posted while in force, below), has a
    /// problem, or has been corrected (its correction stands instead).
    /// </summary>
    [Name("Holdings.PostedLines")]
    public sealed class PostedLinesRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            AccountActivityEvent activity = default!;
            this.When()
                .Match(() => activity, candidate => candidate.Envelope.Type != EventType.PositionOverride)
                .Not<EventProblem>(problem => problem.EventId == activity.EventId)
                .Not<CorrectionEvent>(correction => correction.Corrects == activity.EventId);
            this.Then()
                .Yield(_ => new PostedLines(activity.EventId, HoldingFold.Lines(activity)));
        }
    }

    /// <summary>
    /// Posts an override's items while it is in force: from its effective time until it expires, by the rules' clock.
    /// </summary>
    [Name("Holdings.PostedOverride")]
    public sealed class PostedOverrideRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            ClockFact clock = default!;
            PositionOverrideEvent overriding = default!;
            this.When()
                .Match(() => clock)
                .Match(() => overriding, candidate => HoldingFold.IsInForce(candidate.Envelope.EffectiveTime, candidate.ExpiresTime, clock.Now))
                .Not<EventProblem>(problem => problem.EventId == overriding.EventId)
                .Not<CorrectionEvent>(correction => correction.Corrects == overriding.EventId);
            this.Then()
                .Yield(_ => new PostedLines(overriding.EventId, HoldingFold.Lines(overriding)));
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
                    .Match<PostedLines>()
                    .SelectMany(posted => posted.Lines)
                    .GroupBy(item => ValueTuple.Create(item.AccountId, item.AssetId)))
                .Match(() => account, candidate => candidate.AccountId == lines.Key.AccountId)
                .Match(() => asset, candidate => candidate.AssetId == lines.Key.AssetId);
            this.Then()
                .Yield(_ => HoldingFold.Position(account, asset, lines));
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
    /// The price of an asset, cash included, in its quote currency: the latest price published for any of its symbols in
    /// that currency (a ticker's from the market feed; a currency's from the initial prices), else the price of its last
    /// purchase or sale in any of the tenant's accounts.
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
            IEnumerable<PostedLines> exchanges = default!;

            this.When()
                .Match(() => asset)
                .Query(() => symbols, query => query
                    .Match<SymbolFact>(item => item.NodeId == asset.AssetId)
                    .Collect())
                .Query(() => markets, query => query
                    .Match<PriceFact>(item => item.Currency == asset.QuoteCurrency, item => symbols.Any(symbol => symbol.Scheme == item.Scheme && symbol.Code == item.Code))
                    .Collect())
                .Query(() => exchanges, query => query
                    .Match<PostedLines>(item => item.Lines.Any(line => line.AssetId == asset.AssetId && HoldingFold.Exchanges.Contains(line.Kind)))
                    .Collect())
                .Having(() => markets.Any(market => market.Last > 0m) || exchanges.Any());
            this.Then()
                .Yield(_ => HoldingFold.Price(
                    asset,
                    markets.Where(market => market.Last > 0m).OrderByDescending(market => market.AsOf).FirstOrDefault(),
                    exchanges.SelectMany(posted => posted.Lines).Where(line => line.AssetId == asset.AssetId && HoldingFold.Exchanges.Contains(line.Kind)))!);
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
                    .Match<PostedLines>()
                    .SelectMany(posted => posted.Lines)
                    .Where(item => item.Kind == EventType.CashMovement)
                    .GroupBy(item => item.AccountId));
            this.Then()
                .Yield(_ => new AccountFlow(lines.Key, lines.Sum(item => item.Amount ?? 0m)));
        }
    }
}
