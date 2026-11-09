// <copyright file="HoldingRules.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Holdings
{
    using System.Collections.Generic;
    using System.Linq;
    using AtlasLedger.Model.Primary;
    using NRules.Fluent.Dsl;

    /// <summary>
    /// The scheme of a security's ticker symbol, which the market feed prices.
    /// </summary>
    public static class SymbolSchemes
    {
        /// <summary>The exchange ticker.</summary>
        public const string Ticker = "Ticker";
    }

    /// <summary>
    /// A position override is in force from its effective time until it expires.
    /// </summary>
    [Name("Holdings.PositionOverrideInForce")]
    public sealed class PositionOverrideInForceRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            ClockFact clock = default!;
            PositionOverrideFact item = default!;

            this.When()
                .Match(() => clock)
                .Match(() => item, candidate => HoldingFold.IsInForce(candidate.EffectiveTime, candidate.ExpiresTime, clock.Now));
            this.Then()
                .Yield(_ => new PositionOverrideInForce(item));
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
    /// A holding: every event for an account and a security, folded in processing order.
    /// </summary>
    [Name("Holdings.Position")]
    public sealed class PositionRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            AccountFact account = default!;
            SecurityFact security = default!;
            IEnumerable<OpeningBalanceFact> openings = default!;
            IEnumerable<TradeFact> trades = default!;
            IEnumerable<SplitFact> splits = default!;
            IEnumerable<PositionOverrideInForce> overrides = default!;

            this.When()
                .Match(() => account)
                .Match(() => security)
                .Query(() => openings, query => query
                    .Match<OpeningBalanceFact>(item => item.AccountId == account.AccountId, item => item.SecurityId == security.SecurityId)
                    .Collect())
                .Query(() => trades, query => query
                    .Match<TradeFact>(item => item.AccountId == account.AccountId, item => item.SecurityId == security.SecurityId)
                    .Collect())
                .Query(() => overrides, query => query
                    .Match<PositionOverrideInForce>(item => item.Override.AccountId == account.AccountId, item => item.Override.SecurityId == security.SecurityId)
                    .Collect())
                .Having(() => openings.Any() || trades.Any() || overrides.Any())
                .Query(() => splits, query => query
                    .Match<SplitFact>(item => item.SecurityId == security.SecurityId)
                    .Collect());
            this.Then()
                .Yield(_ => HoldingFold.Position(account.AccountId, security.SecurityId, openings, trades, splits, overrides.Select(item => item.Override)));
        }
    }

    /// <summary>
    /// An account's cash: its cash entries and the cash side of its trades.
    /// </summary>
    [Name("Holdings.Cash")]
    public sealed class CashRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            AccountFact account = default!;
            IEnumerable<CashEntryFact> entries = default!;
            IEnumerable<TradeFact> trades = default!;

            this.When()
                .Match(() => account)
                .Query(() => entries, query => query
                    .Match<CashEntryFact>(item => item.AccountId == account.AccountId)
                    .Collect())
                .Query(() => trades, query => query
                    .Match<TradeFact>(item => item.AccountId == account.AccountId)
                    .Collect())
                .Having(() => entries.Any() || trades.Any());
            this.Then()
                .Yield(_ => HoldingFold.Cash(account.AccountId, entries, trades));
        }
    }

    /// <summary>
    /// The price of a security: an override in force, else the market's latest price for its ticker, else the price of
    /// its last trade in any of the tenant's accounts.
    /// </summary>
    [Name("Holdings.Price")]
    public sealed class PriceRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            SecurityFact security = default!;
            IEnumerable<SymbolFact> tickers = default!;
            IEnumerable<PriceFact> markets = default!;
            IEnumerable<PriceOverrideInForce> overrides = default!;
            IEnumerable<TradeFact> trades = default!;

            this.When()
                .Match(() => security)
                .Query(() => tickers, query => query
                    .Match<SymbolFact>(item => item.NodeId == security.SecurityId, item => item.Scheme == SymbolSchemes.Ticker)
                    .Collect())
                .Query(() => markets, query => query
                    .Match<PriceFact>(item => item.Scheme == SymbolSchemes.Ticker, item => tickers.Any(ticker => ticker.Code == item.Code))
                    .Collect())
                .Query(() => overrides, query => query
                    .Match<PriceOverrideInForce>(item => item.Override.SecurityId == security.SecurityId)
                    .Collect())
                .Query(() => trades, query => query
                    .Match<TradeFact>(item => item.SecurityId == security.SecurityId)
                    .Collect())
                .Having(() => markets.Any(market => market.Last > 0m) || overrides.Any() || trades.Any());
            this.Then()
                .Yield(_ => HoldingFold.Price(
                    security.SecurityId,
                    overrides.Select(item => item.Override),
                    markets.Where(market => market.Last > 0m).OrderByDescending(market => market.AsOf).FirstOrDefault(),
                    trades)!);
        }
    }
}
