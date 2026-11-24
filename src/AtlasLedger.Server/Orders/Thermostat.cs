// <copyright file="Thermostat.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Orders
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using NRules.Fluent.Dsl;

    /// <summary>
    /// A model's invested share under its volatility target: how much of the book's value its weights apply to, the rest
    /// kept in cash.
    /// </summary>
    /// <param name="ModelId">The model.</param>
    /// <param name="Share">The share invested (the target over the measured volatility, at most one); null while it can't
    /// be measured.</param>
    /// <param name="Volatility">The weighted mix's volatility over the window (annualized); null while it can't be
    /// measured.</param>
    /// <param name="Through">The last close in the window.</param>
    /// <param name="Reason">Why it can't be measured, when it can't.</param>
    public sealed record CashShare(Guid ModelId, decimal? Share, decimal? Volatility, DateOnly? Through, string? Reason);

    /// <summary>
    /// A model's share invested under its volatility target, from the tenant's facts: the shared thermostat
    /// (<see cref="AtlasLedger.Model.Thermostat"/>, the same function the website's preview runs) applied to the model's mix,
    /// measured from the closes alone, so it changes once a day, when a close arrives, never on a tick.  For now only fixed
    /// weights; the target is set by hand.
    /// </summary>
    public static class CashShares
    {
        /// <summary>
        /// Measures a model's share invested.
        /// </summary>
        /// <param name="model">The model: fixed weights with a volatility target.</param>
        /// <param name="weights">Its weights.</param>
        /// <param name="assets">The tenant's assets (cash weights are left out of the mix).</param>
        /// <param name="symbols">The tenant's symbols (each asset's ticker).</param>
        /// <param name="closes">Closing prices.</param>
        /// <param name="dividends">Cash dividends.</param>
        /// <param name="splits">Splits.</param>
        /// <returns>The share, or why it can't be measured.</returns>
        public static CashShare Measure(
            ModelFact model,
            IEnumerable<WeightFact> weights,
            IEnumerable<AssetFact> assets,
            IEnumerable<SymbolFact> symbols,
            IEnumerable<ClosingPriceFact> closes,
            IEnumerable<DividendFact> dividends,
            IEnumerable<SplitFact> splits)
        {
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(weights);
            ArgumentNullException.ThrowIfNull(assets);
            ArgumentNullException.ThrowIfNull(symbols);
            ArgumentNullException.ThrowIfNull(closes);
            ArgumentNullException.ThrowIfNull(dividends);
            ArgumentNullException.ThrowIfNull(splits);
            if (model.VolatilityTarget is not { } target || target <= 0m)
            {
                return new CashShare(model.ModelId, null, null, null, "It has no volatility target.");
            }

            var window = model.VolatilityWindow is { } returnsInWindow && returnsInWindow > 1 ? returnsInWindow : AtlasLedger.Model.Thermostat.DefaultWindow;
            var assetById = assets.ToDictionary(asset => asset.AssetId);
            var mix = weights
                .Where(weight => weight.ModelId == model.ModelId && weight.Fraction > 0m && assetById.TryGetValue(weight.AssetId, out var asset) && asset.AssetType != AssetType.Cash)
                .ToArray();
            if (mix.Length == 0)
            {
                return new CashShare(model.ModelId, 0m, null, null, null);   // nothing to invest in: all cash
            }

            var tickerOf = symbols.Where(symbol => symbol.Scheme == SymbolScheme.Ticker).GroupBy(symbol => symbol.NodeId).ToDictionary(group => group.Key, group => group.Min(symbol => symbol.Code)!);
            var series = new List<(IReadOnlyDictionary<DateOnly, double> Returns, decimal Fraction)>();
            foreach (var weight in mix)
            {
                if (!tickerOf.TryGetValue(weight.AssetId, out var ticker))
                {
                    return new CashShare(model.ModelId, null, null, null, "A holding in its weights has no ticker to measure it by.");
                }

                series.Add((HistoryOf(ticker, assetById[weight.AssetId].QuoteCurrency, closes, dividends, splits).TotalReturns(), weight.Fraction));
            }

            var mixed = AtlasLedger.Model.Thermostat.MixReturns(series);
            if (mixed.Count < window)
            {
                return new CashShare(model.ModelId, null, null, mixed.Count > 0 ? mixed[^1].Date : null, $"Its volatility needs {window} daily returns of every holding; there are {mixed.Count}.");
            }

            var last = mixed.Skip(mixed.Count - window).ToArray();
            var volatility = AtlasLedger.Model.Thermostat.VolatilityOf(last.Select(item => item.Return).ToArray());
            var share = AtlasLedger.Model.Thermostat.ShareFor(target, volatility);
            return new CashShare(model.ModelId, share, double.IsFinite(volatility) && volatility > 0d ? Math.Round((decimal)volatility, 6, MidpointRounding.ToEven) : null, last[^1].Date, null);
        }

        /// <summary>A ticker's history from the tenant's facts.</summary>
        private static TickerHistory HistoryOf(string ticker, string currency, IEnumerable<ClosingPriceFact> closes, IEnumerable<DividendFact> dividends, IEnumerable<SplitFact> splits) =>
            new(
                ticker,
                closes
                    .Where(close => close.Scheme == SymbolScheme.Ticker && string.Equals(close.Code, ticker, StringComparison.OrdinalIgnoreCase) && close.Currency == currency)
                    .GroupBy(close => close.Date)
                    .ToDictionary(group => group.Key, group => group.OrderByDescending(close => close.AsOf).First().Close),
                dividends
                    .Where(dividend => dividend.Scheme == SymbolScheme.Ticker && string.Equals(dividend.Code, ticker, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(dividend => dividend.ExDate)
                    .ToDictionary(group => group.Key, group => group.Sum(dividend => dividend.Amount)),
                splits
                    .Where(split => split.Scheme == SymbolScheme.Ticker && string.Equals(split.Code, ticker, StringComparison.OrdinalIgnoreCase) && split.Ratio > 0m)
                    .GroupBy(split => split.Date)
                    .ToDictionary(group => group.Key, group => group.Aggregate(1m, (product, split) => product * split.Ratio)));
    }

    /// <summary>
    /// A volatility-target model's share invested, measured again whenever its weights, its holdings' history or the model
    /// changes (once a day, when a close arrives; never on a tick).
    /// </summary>
    [Name("Orders.Thermostat")]
    public sealed class ThermostatRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            ModelFact model = default!;
            IEnumerable<WeightFact> weights = default!;
            IEnumerable<AssetFact> assets = default!;
            IEnumerable<SymbolFact> symbols = default!;
            IEnumerable<ClosingPriceFact> closes = default!;
            IEnumerable<DividendFact> dividends = default!;
            IEnumerable<SplitFact> splits = default!;
            this.When()
                .Match(() => model, candidate => candidate.ModelKind == ModelKind.FixedWeights, candidate => candidate.CashRule == CashRule.VolatilityTarget)
                .Query(() => weights, query => query.Match<WeightFact>(item => item.ModelId == model.ModelId).Collect())
                .Query(() => assets, query => query.Match<AssetFact>().Collect())
                .Query(() => symbols, query => query.Match<SymbolFact>().Collect())
                .Query(() => closes, query => query.Match<ClosingPriceFact>().Collect())
                .Query(() => dividends, query => query.Match<DividendFact>().Collect())
                .Query(() => splits, query => query.Match<SplitFact>().Collect());
            this.Then()
                .Yield(_ => CashShares.Measure(model, weights, assets, symbols, closes, dividends, splits));
        }
    }
}
