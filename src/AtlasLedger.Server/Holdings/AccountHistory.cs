// <copyright file="AccountHistory.cs" company="Tangent Atlas">
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
    using AtlasLedger.Server.Jobs;

    /// <summary>
    /// An account at one market day's close.
    /// </summary>
    /// <param name="Date">The market day.</param>
    /// <param name="Value">What the account was worth at the close, in its currency; null if something it held had no price
    /// (or was in another currency).</param>
    /// <param name="Flow">The value that came in (positive) or went out (negative) from outside during the day: deposits,
    /// withdrawals, journals and transfers of securities (at the day's close).</param>
    /// <param name="Return">The day's return: (value − flow) ÷ the previous day's value − 1, the flows taken as arriving at
    /// the close; null on the first day, or when a value is missing.</param>
    /// <param name="RiskFreeReturn">The day's risk-free return: the 13-week Treasury bill's yield (coupon equivalent)
    /// published for the market day before, ÷ 252; null if none is loaded.</param>
    public sealed record AccountDay(DateOnly Date, decimal? Value, decimal Flow, decimal? Return, decimal? RiskFreeReturn);

    /// <summary>
    /// An account's performance over its history, measured from its daily returns as purely as the data allows: no
    /// adjustment for luck or for how short the history is.
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="From">The first day valued.</param>
    /// <param name="Through">The last day valued.</param>
    /// <param name="Value">Its value at the last close.</param>
    /// <param name="NetFlows">What came in less what went out, over the history.</param>
    /// <param name="Returns">How many daily returns were measured.</param>
    /// <param name="TotalReturn">The returns compounded: (1 + r₁)(1 + r₂)… − 1 (time-weighted, so deposits don't count as
    /// gains).</param>
    /// <param name="AnnualizedReturn">The total return a year: (1 + total)^(252 ÷ returns) − 1.</param>
    /// <param name="Volatility">The daily returns' standard deviation (sample), × √252.</param>
    /// <param name="SharpeRatio">The daily excess returns' (return − risk-free return) mean ÷ their standard deviation
    /// (sample), × √252; null with fewer than two returns, or none varying.</param>
    /// <param name="MaximumDrawdown">The largest fall from a peak of the compounded returns, as a fraction (0.12 for 12%).</param>
    /// <param name="IsRanked">Whether there are enough returns to rank it (<see cref="AccountHistoryFold.ReturnsToRank"/>).</param>
    /// <param name="Gaps">Why some days have no value or no return.</param>
    public sealed record AccountStatistics(
        Guid AccountId,
        DateOnly? From,
        DateOnly? Through,
        decimal? Value,
        decimal NetFlows,
        int Returns,
        decimal? TotalReturn,
        decimal? AnnualizedReturn,
        decimal? Volatility,
        decimal? SharpeRatio,
        decimal? MaximumDrawdown,
        bool IsRanked,
        IReadOnlyList<string> Gaps);

    /// <summary>
    /// A corporate action a paper account is owed and hasn't been credited: a dividend on the shares it held at the close
    /// before the ex-date, or new shares from a split.
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="AssetId">The stock.</param>
    /// <param name="Ticker">Its ticker.</param>
    /// <param name="Date">The ex-date or split date.</param>
    /// <param name="IsSplit">A split (new shares), or a dividend (cash).</param>
    /// <param name="SharesHeld">The shares held at the close before.</param>
    /// <param name="Value">For a dividend, the amount per share; for a split, the ratio.</param>
    /// <param name="Currency">A dividend's currency.</param>
    /// <param name="PaymentDate">A dividend's payment date.</param>
    /// <param name="CashAssetId">The asset of the account's cash, which receives a dividend.</param>
    /// <param name="SourceId">Its identity at the paper broker, from which its event's key is made.</param>
    public sealed record CorporateActionDue(
        Guid AccountId,
        Guid AssetId,
        string Ticker,
        DateOnly Date,
        bool IsSplit,
        decimal SharesHeld,
        decimal Value,
        string? Currency,
        DateOnly? PaymentDate,
        Guid? CashAssetId,
        string SourceId);

    /// <summary>
    /// An account's history, day by day at each close, its statistics, and the corporate actions it is owed: derived by the
    /// rules from its posted lines and the closes, never stored.
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="Days">Each market day from its first activity through the latest close loaded.</param>
    /// <param name="Statistics">The statistics.</param>
    /// <param name="Due">The corporate actions owed (paper accounts only).</param>
    public sealed record AccountHistory(Guid AccountId, IReadOnlyList<AccountDay> Days, AccountStatistics Statistics, IReadOnlyList<CorporateActionDue> Due);

    /// <summary>
    /// Builds an account's history from its lines and the market's closes.  Ordinary code, called by the rules.
    /// </summary>
    public static class AccountHistoryFold
    {
        /// <summary>The market days in a year, for daily rates and annualizing.</summary>
        public const int MarketDaysAYear = 252;

        /// <summary>The code of the risk-free rate: the 13-week Treasury bill's coupon equivalent, from the Treasury's daily bill rates.</summary>
        public const string RiskFreeRate = "TBILL-13W";

        /// <summary>The returns an account needs before it is ranked: about a month of market days.</summary>
        public const int ReturnsToRank = 20;

        /// <summary>
        /// Builds the history.
        /// </summary>
        /// <param name="account">The account.</param>
        /// <param name="isPaper">Whether it is held at the paper broker (the simulator), which credits corporate actions.</param>
        /// <param name="lines">Its posted lines (others are ignored).</param>
        /// <param name="assets">The tenant's assets.</param>
        /// <param name="symbols">The tenant's symbols.</param>
        /// <param name="closes">The closes loaded.</param>
        /// <param name="rates">The interest rates loaded.</param>
        /// <param name="dividends">The dividends loaded.</param>
        /// <param name="splits">The splits loaded.</param>
        /// <returns>The history.</returns>
        public static AccountHistory Build(
            AccountFact account,
            bool isPaper,
            IEnumerable<PostedLine> lines,
            IEnumerable<AssetFact> assets,
            IEnumerable<SymbolFact> symbols,
            IEnumerable<ClosingPriceFact> closes,
            IEnumerable<InterestRateFact> rates,
            IEnumerable<DividendFact> dividends,
            IEnumerable<SplitFact> splits)
        {
            ArgumentNullException.ThrowIfNull(account);
            var mine = lines.Where(line => line.AccountId == account.AccountId && line.Effect == LineEffect.Change).OrderBy(HoldingFold.ProcessingOrder).ToArray();
            var assetById = assets.ToDictionary(asset => asset.AssetId);
            var tickerOf = symbols.Where(symbol => symbol.Scheme == SymbolScheme.Ticker).GroupBy(symbol => symbol.NodeId).ToDictionary(group => group.Key, group => group.Min(symbol => symbol.Code)!);
            var assetOfTicker = tickerOf.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);
            var closesByTicker = closes
                .GroupBy(close => (close.Code, close.Currency))
                .ToDictionary(group => group.Key, group => new SortedList<DateOnly, decimal>(group.ToDictionary(close => close.Date, close => close.Close)));
            var rateByDate = new SortedList<DateOnly, decimal>(rates.Where(rate => rate.Code == RiskFreeRate).ToDictionary(rate => rate.Date, rate => rate.Rate));
            var cash = Cash(account, assetById.Values, symbols);
            var gaps = new SortedSet<string>(StringComparer.Ordinal);

            if (mine.Length == 0)
            {
                return new AccountHistory(account.AccountId, [], Statistics(account.AccountId, [], gaps), []);
            }

            // The days: every market day with a close loaded, from the day of the first line through the last close.
            var first = Day(mine[0].EffectiveTime);
            var days = closesByTicker.Values.SelectMany(list => list.Keys).Where(day => day >= first).Distinct().Order().ToArray();
            var result = new List<AccountDay>(days.Length);
            decimal? previousValue = null;
            var index = 0;
            var quantities = new Dictionary<Guid, decimal>();
            foreach (var day in days)
            {
                // Apply the lines up to this day's close; the external ones are the day's flow.
                var close = MarketCalendar.CloseTime(day);
                var flow = 0m;
                var isFlowKnown = true;
                for (; index < mine.Length && mine[index].EffectiveTime <= close; index++)
                {
                    var line = mine[index];
                    quantities[line.AssetId] = quantities.GetValueOrDefault(line.AssetId) + line.Quantity;
                    if (line.IsExternal)
                    {
                        if (Worth(line.AssetId, line.Quantity, day) is { } worth)
                        {
                            flow += worth;
                        }
                        else
                        {
                            isFlowKnown = false;
                        }
                    }
                }

                decimal? value = 0m;
                foreach (var (assetId, quantity) in quantities)
                {
                    if (quantity == 0m)
                    {
                        continue;
                    }

                    value = Worth(assetId, quantity, day) is { } worth && value is { } sum ? sum + worth : null;
                }

                if (!isFlowKnown)
                {
                    value = null;
                }

                decimal? dayReturn = previousValue is > 0m && value is { } today ? ((today - flow) / previousValue.Value) - 1m : null;
                decimal? riskFree = rateByDate.Keys.LastOrDefault(date => date < day) is var rateDate && rateByDate.TryGetValue(rateDate, out var rate) ? rate / MarketDaysAYear : null;
                if (dayReturn is not null && riskFree is null)
                {
                    gaps.Add("No risk-free rate loaded for some days: their excess return counts it as 0.");
                }

                result.Add(new AccountDay(day, Round(value, 4), Round(flow, 4)!.Value, Round(dayReturn, 10), Round(riskFree, 10)));
                previousValue = value;
            }

            var due = isPaper ? Due(account, mine, assetById, assetOfTicker, cash, dividends, splits) : [];
            return new AccountHistory(account.AccountId, result, Statistics(account.AccountId, result, gaps), due);

            decimal? Worth(Guid assetId, decimal quantity, DateOnly day)
            {
                if (!assetById.TryGetValue(assetId, out var asset))
                {
                    gaps.Add($"Asset {assetId} isn't known.");
                    return null;
                }

                if (asset.AssetType == AssetType.Cash)
                {
                    if (asset.AssetId == cash)
                    {
                        return quantity;
                    }

                    gaps.Add($"Cash {asset.AssetId} isn't in the account's currency, {account.Currency}.");
                    return null;
                }

                if (!string.Equals(asset.QuoteCurrency, account.Currency, StringComparison.Ordinal))
                {
                    gaps.Add($"Asset {asset.AssetId} is quoted in {asset.QuoteCurrency}, not the account's {account.Currency}.");
                    return null;
                }

                // The day's close, or the last before it (a halted stock keeps its last close).
                if (tickerOf.TryGetValue(assetId, out var ticker)
                    && closesByTicker.TryGetValue((ticker, asset.QuoteCurrency), out var list)
                    && LastOnOrBefore(list, day) is { } price)
                {
                    return quantity * asset.QuantityFactor * price * asset.PriceFactor;
                }

                gaps.Add($"No close loaded for {ticker ?? assetId.ToString()} on a day it was held.");
                return null;
            }
        }

        /// <summary>
        /// The statistics of a history.
        /// </summary>
        /// <param name="accountId">The account.</param>
        /// <param name="days">Its days.</param>
        /// <param name="gaps">Why some are missing.</param>
        /// <returns>The statistics.</returns>
        public static AccountStatistics Statistics(Guid accountId, IReadOnlyList<AccountDay> days, IEnumerable<string> gaps)
        {
            ArgumentNullException.ThrowIfNull(days);
            var returns = days.Where(day => day.Return is not null).Select(day => (double)day.Return!.Value).ToArray();
            var excess = days.Where(day => day.Return is not null).Select(day => (double)(day.Return!.Value - (day.RiskFreeReturn ?? 0m))).ToArray();
            var annual = Math.Sqrt(MarketDaysAYear);
            double? total = returns.Length > 0 ? returns.Aggregate(1d, (product, item) => product * (1d + item)) - 1d : null;
            double? annualized = total is { } compounded && compounded > -1d ? Math.Pow(1d + compounded, (double)MarketDaysAYear / returns.Length) - 1d : null;
            double? volatility = returns.Length > 1 ? StandardDeviation(returns) * annual : null;
            var excessDeviation = excess.Length > 1 ? StandardDeviation(excess) : 0d;
            double? sharpe = excessDeviation > 0d ? excess.Average() / excessDeviation * annual : null;
            double? drawdown = null;
            if (returns.Length > 0)
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

            return new AccountStatistics(
                accountId,
                days.Count > 0 ? days[0].Date : null,
                days.Count > 0 ? days[^1].Date : null,
                days.Count > 0 ? days[^1].Value : null,
                days.Sum(day => day.Flow),
                returns.Length,
                Round(total),
                Round(annualized),
                Round(volatility),
                Round(sharpe),
                Round(drawdown),
                returns.Length >= ReturnsToRank && sharpe is not null,
                gaps.ToArray());
        }

        /// <summary>
        /// The key of the event that credits a corporate action: the same every time, so it is booked once.
        /// </summary>
        /// <param name="sourceId">The action's identity at the paper broker.</param>
        /// <returns>The event's key.</returns>
        public static Guid EventId(string sourceId) => Brokers.OrderRouter.ReportId(EventSource.Simulator, sourceId);

        /// <summary>
        /// The corporate actions a paper account is owed: for each dividend or split of a stock it held at the close before
        /// the date, unless its event is already posted.
        /// </summary>
        private static IReadOnlyList<CorporateActionDue> Due(
            AccountFact account,
            IReadOnlyList<PostedLine> lines,
            IReadOnlyDictionary<Guid, AssetFact> assets,
            IReadOnlyDictionary<string, Guid> assetOfTicker,
            Guid? cash,
            IEnumerable<DividendFact> dividends,
            IEnumerable<SplitFact> splits)
        {
            var posted = lines.Select(line => line.EventId).ToHashSet();
            var due = new List<CorporateActionDue>();
            decimal HeldBefore(Guid assetId, DateOnly date)
            {
                var cutoff = MarketCalendar.CloseTime(MarketCalendar.PreviousMarketDay(date));
                return lines.Where(line => line.AssetId == assetId && line.EffectiveTime <= cutoff).Sum(line => line.Quantity);
            }

            foreach (var dividend in dividends.Where(item => item.Scheme == SymbolScheme.Ticker))
            {
                if (assetOfTicker.TryGetValue(dividend.Code, out var assetId) && assets.ContainsKey(assetId) && HeldBefore(assetId, dividend.ExDate) is var shares and > 0m)
                {
                    var sourceId = $"dividend:{account.AccountId:N}:{dividend.Code}:{dividend.ExDate:yyyy-MM-dd}";
                    if (!posted.Contains(EventId(sourceId)))
                    {
                        due.Add(new CorporateActionDue(account.AccountId, assetId, dividend.Code, dividend.ExDate, false, shares, dividend.Amount, dividend.Currency, dividend.PaymentDate, cash, sourceId));
                    }
                }
            }

            foreach (var split in splits.Where(item => item.Scheme == SymbolScheme.Ticker && item.Ratio > 0m && item.Ratio != 1m))
            {
                if (assetOfTicker.TryGetValue(split.Code, out var assetId) && assets.ContainsKey(assetId) && HeldBefore(assetId, split.Date) is var shares and > 0m)
                {
                    var sourceId = $"split:{account.AccountId:N}:{split.Code}:{split.Date:yyyy-MM-dd}";
                    if (!posted.Contains(EventId(sourceId)))
                    {
                        due.Add(new CorporateActionDue(account.AccountId, assetId, split.Code, split.Date, true, shares, split.Ratio, null, null, null, sourceId));
                    }
                }
            }

            return due.OrderBy(item => item.Date).ThenBy(item => item.Ticker, StringComparer.Ordinal).ToArray();
        }

        /// <summary>
        /// The event that credits a corporate action: a dividend as income (cash, in the account's currency, to the cent),
        /// effective at the open of the ex-date; a split as shares received at no cost, effective at the open of its date.
        /// </summary>
        /// <param name="due">The action.</param>
        /// <returns>The event, or null if a dividend has no cash asset to go to.</returns>
        public static AccountActivityEvent? Event(CorporateActionDue due)
        {
            ArgumentNullException.ThrowIfNull(due);
            if (due.IsSplit)
            {
                return new ReceiveAndDeliverEvent
                {
                    AccountId = due.AccountId,
                    TradeDate = due.Date,
                    Reason = DeliveryReason.Split,
                    Items = [new ActivityItem(due.AssetId, due.SharesHeld * (due.Value - 1m), 0m)],
                };
            }

            if (due.CashAssetId is not { } cash)
            {
                return null;
            }

            var amount = Math.Round(due.SharesHeld * due.Value, 2, MidpointRounding.ToEven);
            return new IncomeEvent
            {
                AccountId = due.AccountId,
                TradeDate = due.Date,
                SettlementDate = due.PaymentDate,
                IncomeType = IncomeType.Dividend,
                PaidByAssetId = due.AssetId,
                Items = [new ActivityItem(cash, amount, amount)],
            };
        }

        /// <summary>
        /// When a corporate action takes effect: the open of its date (9:30 am in New York).
        /// </summary>
        /// <param name="date">Its date.</param>
        /// <returns>The time.</returns>
        public static DateTimeOffset OpenTime(DateOnly date) => MarketCalendar.CloseTime(date).AddHours(-6.5);

        private static DateOnly Day(DateTimeOffset time) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, MarketCalendar.Zone).DateTime);

        private static Guid? Cash(AccountFact account, IEnumerable<AssetFact> assets, IEnumerable<SymbolFact> symbols)
        {
            var currencies = symbols.Where(symbol => symbol.Scheme == SymbolScheme.Currency && symbol.Code == account.Currency).Select(symbol => symbol.NodeId).ToHashSet();
            return assets.Where(asset => asset.AssetType == AssetType.Cash && currencies.Contains(asset.AssetId)).Select(asset => (Guid?)asset.AssetId).FirstOrDefault();
        }

        private static decimal? LastOnOrBefore(SortedList<DateOnly, decimal> list, DateOnly day)
        {
            var keys = list.Keys;
            int low = 0, high = keys.Count - 1, found = -1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                if (keys[middle] <= day)
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return found >= 0 ? list.Values[found] : null;
        }

        private static double StandardDeviation(IReadOnlyList<double> values)
        {
            var mean = values.Average();
            return Math.Sqrt(values.Sum(value => (value - mean) * (value - mean)) / (values.Count - 1));
        }

        private static decimal? Round(decimal? value, int places) => value is { } number ? Math.Round(number, places, MidpointRounding.ToEven) : null;

        private static decimal? Round(double? value) => value is { } number && double.IsFinite(number) ? Math.Round((decimal)number, 6, MidpointRounding.ToEven) : null;
    }
}
