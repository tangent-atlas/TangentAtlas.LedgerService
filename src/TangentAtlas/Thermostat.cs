// <copyright file="Thermostat.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Model
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Performance measured from daily returns, as purely as the data allows: one formula for the ledger's accounts and
    /// portfolios and the website's five-year preview, so they always agree.
    /// </summary>
    /// <param name="Returns">How many daily returns were measured.</param>
    /// <param name="TotalReturn">The returns compounded: (1 + r₁)(1 + r₂)… − 1.</param>
    /// <param name="AnnualizedReturn">(1 + total)^(252 ÷ returns) − 1.</param>
    /// <param name="Volatility">The daily returns' sample standard deviation × √252.</param>
    /// <param name="SharpeRatio">The daily excess returns' mean ÷ their standard deviation × √252.</param>
    /// <param name="MaximumDrawdown">The worst fall from a peak, as a fraction of the peak.</param>
    public sealed record PerformanceFigures(int Returns, decimal? TotalReturn, decimal? AnnualizedReturn, decimal? Volatility, decimal? SharpeRatio, decimal? MaximumDrawdown);

    /// <summary>The performance formulas.</summary>
    public static class Performance
    {
        /// <summary>The market days in a year, for daily rates and annualizing.</summary>
        public const int MarketDaysAYear = 252;

        /// <summary>
        /// Measures daily returns.
        /// </summary>
        /// <param name="returns">The daily returns.</param>
        /// <param name="riskFree">Each day's risk-free return, the same length (0 where there is none).</param>
        /// <returns>The figures; each null where it can't be measured.</returns>
        public static PerformanceFigures Measure(IReadOnlyList<double> returns, IReadOnlyList<double> riskFree)
        {
            ArgumentNullException.ThrowIfNull(returns);
            ArgumentNullException.ThrowIfNull(riskFree);
            var excess = returns.Select((value, index) => value - riskFree[index]).ToArray();
            var annual = Math.Sqrt(MarketDaysAYear);
            double? total = returns.Count > 0 ? returns.Aggregate(1d, (product, item) => product * (1d + item)) - 1d : null;
            double? annualized = total is { } compounded && compounded > -1d ? Math.Pow(1d + compounded, (double)MarketDaysAYear / returns.Count) - 1d : null;
            double? volatility = returns.Count > 1 ? StandardDeviation(returns) * annual : null;
            var excessDeviation = excess.Length > 1 ? StandardDeviation(excess) : 0d;
            double? sharpe = excessDeviation > 0d ? excess.Average() / excessDeviation * annual : null;
            double? drawdown = null;
            if (returns.Count > 0)
            {
                var level = 1d;
                var peak = 1d;
                var worst = 0d;
                foreach (var item in returns)
                {
                    level *= 1d + item;
                    peak = Math.Max(peak, level);
                    worst = Math.Max(worst, 1d - (level / peak));
                }

                drawdown = worst;
            }

            return new PerformanceFigures(returns.Count, Round(total), Round(annualized), Round(volatility), Round(sharpe), Round(drawdown));
        }

        /// <summary>The sample standard deviation.</summary>
        /// <param name="values">At least two values.</param>
        /// <returns>The standard deviation.</returns>
        public static double StandardDeviation(IReadOnlyList<double> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            var mean = values.Average();
            return Math.Sqrt(values.Sum(value => (value - mean) * (value - mean)) / (values.Count - 1));
        }

        private static decimal? Round(double? value) => value is { } number && double.IsFinite(number) ? Math.Round((decimal)number, 6, MidpointRounding.ToEven) : null;
    }

    /// <summary>One ticker's market history: closes as traded, cash dividends by ex-date, and splits by date.</summary>
    /// <param name="Ticker">The ticker.</param>
    /// <param name="Closes">Its closes, by market day.</param>
    /// <param name="Dividends">Its cash dividends per share, by ex-date.</param>
    /// <param name="Splits">Its split ratios, by the date they take effect.</param>
    public sealed record TickerHistory(string Ticker, IReadOnlyDictionary<DateOnly, decimal> Closes, IReadOnlyDictionary<DateOnly, decimal> Dividends, IReadOnlyDictionary<DateOnly, decimal> Splits)
    {
        /// <summary>
        /// The daily total returns: a holder of one share at a close has the split's ratio of shares at the next, and the
        /// dividend for each share he held, so neither looks like a drop.
        /// </summary>
        /// <returns>Each market day's return, by day (the first close has none).</returns>
        public IReadOnlyDictionary<DateOnly, double> TotalReturns()
        {
            var list = this.Closes.Where(pair => pair.Value > 0m).OrderBy(pair => pair.Key).ToArray();
            var returns = new Dictionary<DateOnly, double>();
            for (var index = 1; index < list.Length; index++)
            {
                var (before, beforeClose) = (list[index - 1].Key, list[index - 1].Value);
                var (after, afterClose) = (list[index].Key, list[index].Value);
                var ratio = this.Splits.Where(pair => pair.Key > before && pair.Key <= after && pair.Value > 0m).Aggregate(1m, (product, pair) => product * pair.Value);
                var dividend = this.Dividends.Where(pair => pair.Key > before && pair.Key <= after).Sum(pair => pair.Value);
                returns[after] = (double)(((afterClose * ratio) + dividend) / beforeClose) - 1d;
            }

            return returns;
        }
    }

    /// <summary>
    /// The thermostat (pre-registration §20): the share of a portfolio invested so its mix runs at the volatility target,
    /// the target over the mix's measured volatility (annualized sample standard deviation of its last 21 daily total
    /// returns), at most all of it.  The rest is cash.  One function for the ledger, which trades it, and the preview.
    /// </summary>
    public static class Thermostat
    {
        /// <summary>The daily returns in the measure: the Joe 10's.</summary>
        public const int DefaultWindow = 21;

        /// <summary>The annualized sample standard deviation of daily returns.</summary>
        /// <param name="returns">The daily returns.</param>
        /// <returns>The volatility; NaN with fewer than two.</returns>
        public static double VolatilityOf(IReadOnlyList<double> returns)
        {
            ArgumentNullException.ThrowIfNull(returns);
            return returns.Count < 2 ? double.NaN : Performance.StandardDeviation(returns) * Math.Sqrt(Performance.MarketDaysAYear);
        }

        /// <summary>The share invested for a target and a measured volatility: target ÷ volatility, at most one.</summary>
        /// <param name="target">The volatility target (0.15 for 15%).</param>
        /// <param name="volatility">The measured volatility.</param>
        /// <returns>The share, rounded down to six places; one when no movement was measured.</returns>
        public static decimal ShareFor(decimal target, double volatility) =>
            !double.IsFinite(volatility) || volatility <= 0d ? 1m : Math.Min(1m, Math.Round(target / (decimal)volatility, 6, MidpointRounding.ToZero));

        /// <summary>The mix's daily returns: its holdings' returns weighted by their fractions (normalized), on the days all have one.</summary>
        /// <param name="mix">Each holding's returns and fraction.</param>
        /// <returns>The mix's returns, oldest first.</returns>
        public static IReadOnlyList<(DateOnly Date, double Return)> MixReturns(IReadOnlyList<(IReadOnlyDictionary<DateOnly, double> Returns, decimal Fraction)> mix)
        {
            ArgumentNullException.ThrowIfNull(mix);
            if (mix.Count == 0)
            {
                return [];
            }

            var total = mix.Sum(item => item.Fraction);
            var days = mix.Skip(1).Aggregate((IEnumerable<DateOnly>)mix[0].Returns.Keys, (common, item) => common.Intersect(item.Returns.Keys)).Order().ToArray();
            return days.Select(day => (day, mix.Sum(item => (double)(item.Fraction / total) * item.Returns[day]))).ToArray();
        }
    }

    /// <summary>What a portfolio's settings would have done over a period, day by day, and its figures.</summary>
    /// <param name="From">The first market day with a return.</param>
    /// <param name="Through">The last.</param>
    /// <param name="StartingValue">Its value at the close before From.</param>
    /// <param name="EndingValue">Its value at the close of Through.</param>
    /// <param name="Figures">Its performance.</param>
    /// <param name="AverageInvested">The average share invested (one when fully invested).</param>
    /// <param name="Values">Its value at each close, from the close before From.</param>
    /// <param name="Problem">Why it couldn't be worked out, when it couldn't.</param>
    public sealed record PreviewResult(
        DateOnly? From,
        DateOnly? Through,
        decimal StartingValue,
        decimal? EndingValue,
        PerformanceFigures Figures,
        decimal? AverageInvested,
        IReadOnlyList<(DateOnly Date, decimal Value)> Values,
        string? Problem);

    /// <summary>
    /// What a portfolio's settings would have done: its mix of holdings (rebalanced to their percents every day), fully
    /// invested or under the thermostat, from a day to the last close held.  As the ledger trades it: at each day's close
    /// the share invested is set from the returns through the close before (what is known at 3:45 pm), and earns the next
    /// day's return; the cash earns nothing, as Play Money's does; no trading costs and no rounding to whole shares.  Scored
    /// by the same formulas as the ledger's accounts, the risk-free rate the 13-week Treasury bill's.
    /// </summary>
    public static class Preview
    {
        /// <summary>
        /// Works out a portfolio's history.
        /// </summary>
        /// <param name="mix">Each holding's history and its percent of what is invested.</param>
        /// <param name="volatilityTarget">The thermostat's target; null for fully invested.</param>
        /// <param name="window">The thermostat's window of daily returns.</param>
        /// <param name="riskFreeRates">The risk-free rate a year, by day.</param>
        /// <param name="from">The first day to include.</param>
        /// <param name="startingValue">The value it starts with.</param>
        /// <returns>The history and its figures.</returns>
        public static PreviewResult Run(
            IReadOnlyList<(TickerHistory History, decimal Percent)> mix,
            decimal? volatilityTarget,
            int window,
            IReadOnlyDictionary<DateOnly, decimal> riskFreeRates,
            DateOnly from,
            decimal startingValue)
        {
            ArgumentNullException.ThrowIfNull(mix);
            ArgumentNullException.ThrowIfNull(riskFreeRates);
            var returns = Thermostat.MixReturns(mix.Select(item => (item.History.TotalReturns(), item.Percent)).ToArray());
            return Run(returns, volatilityTarget, window, riskFreeRates, from, startingValue);
        }

        /// <summary>Works out a portfolio's history from its mix's daily returns (worked out once for many settings).</summary>
        /// <param name="mixReturns">The mix's daily returns, oldest first.</param>
        /// <param name="volatilityTarget">The thermostat's target; null for fully invested.</param>
        /// <param name="window">The thermostat's window of daily returns.</param>
        /// <param name="riskFreeRates">The risk-free rate a year, by day.</param>
        /// <param name="from">The first day to include.</param>
        /// <param name="startingValue">The value it starts with.</param>
        /// <returns>The history and its figures.</returns>
        public static PreviewResult Run(
            IReadOnlyList<(DateOnly Date, double Return)> mixReturns,
            decimal? volatilityTarget,
            int window,
            IReadOnlyDictionary<DateOnly, decimal> riskFreeRates,
            DateOnly from,
            decimal startingValue)
        {
            ArgumentNullException.ThrowIfNull(mixReturns);
            ArgumentNullException.ThrowIfNull(riskFreeRates);
            var empty = new PerformanceFigures(0, null, null, null, null, null);
            if (window < 2)
            {
                window = Thermostat.DefaultWindow;
            }

            // The first day: on or after From, and, under a target, with a full window known the close before.
            var first = Array.FindIndex(mixReturns.ToArray(), item => item.Date >= from);
            if (volatilityTarget is not null)
            {
                first = Math.Max(first, window + 1);
            }

            if (first < 0 || first >= mixReturns.Count)
            {
                return new PreviewResult(null, null, startingValue, null, empty, null, [], volatilityTarget is null
                    ? "There is no history for these holdings in the period."
                    : $"The thermostat needs {window} daily returns before the period starts; there aren't enough.");
            }

            var rates = riskFreeRates.OrderBy(pair => pair.Key).ToArray();
            var portfolio = new List<double>();
            var riskFree = new List<double>();
            var values = new List<(DateOnly, decimal)> { (mixReturns[first - 1 < 0 ? 0 : first - 1].Date, startingValue) };
            var value = (double)startingValue;
            var invested = 0m;
            var window0 = new double[window];
            for (var index = first; index < mixReturns.Count; index++)
            {
                var share = 1m;
                if (volatilityTarget is { } target)
                {
                    // Known at 3:45 pm the day before: the returns through the close before that.
                    for (var k = 0; k < window; k++)
                    {
                        window0[k] = mixReturns[index - 1 - window + k].Return;
                    }

                    share = Thermostat.ShareFor(target, Thermostat.VolatilityOf(window0));
                }

                var day = mixReturns[index].Date;
                var result = (double)share * mixReturns[index].Return;
                portfolio.Add(result);
                riskFree.Add((double)(RateBefore(rates, day) / Performance.MarketDaysAYear));
                value *= 1d + result;
                values.Add((day, Math.Round((decimal)value, 2)));
                invested += share;
            }

            return new PreviewResult(
                mixReturns[first].Date,
                mixReturns[^1].Date,
                startingValue,
                values[^1].Item2,
                Performance.Measure(portfolio, riskFree),
                Math.Round(invested / portfolio.Count, 4),
                values,
                null);
        }

        /// <summary>The rate on the last day before a day (as the ledger's accounts use it); zero without one.</summary>
        private static decimal RateBefore(KeyValuePair<DateOnly, decimal>[] rates, DateOnly day)
        {
            int low = 0, high = rates.Length - 1, found = -1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                if (rates[middle].Key < day)
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return found >= 0 ? rates[found].Value : 0m;
        }
    }
}
