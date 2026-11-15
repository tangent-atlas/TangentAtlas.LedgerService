// <copyright file="Statistics.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Api
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// An account at one market day's close.
    /// </summary>
    /// <param name="Date">The market day.</param>
    /// <param name="Value">What the account was worth at the close; null if something it held had no price.</param>
    /// <param name="Flow">What came in (positive) or went out (negative) from outside during the day.</param>
    /// <param name="Return">The day's return: (value − flow) ÷ the previous value − 1.</param>
    /// <param name="RiskFreeReturn">The day's risk-free return (the 13-week Treasury bill rate ÷ 252).</param>
    public sealed record AccountDay(DateOnly Date, decimal? Value, decimal Flow, decimal? Return, decimal? RiskFreeReturn);

    /// <summary>
    /// An account's performance, measured from its daily returns.
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="From">The first day valued.</param>
    /// <param name="Through">The last day valued.</param>
    /// <param name="Value">Its value at the last close.</param>
    /// <param name="NetFlows">What came in less what went out.</param>
    /// <param name="Returns">How many daily returns were measured.</param>
    /// <param name="TotalReturn">The returns compounded (time-weighted).</param>
    /// <param name="AnnualizedReturn">The total return a year.</param>
    /// <param name="Volatility">The daily returns' standard deviation, annualized.</param>
    /// <param name="SharpeRatio">The daily excess returns' mean ÷ their standard deviation, annualized.</param>
    /// <param name="MaximumDrawdown">The largest fall from a peak, as a fraction.</param>
    /// <param name="IsRanked">Whether there are enough returns to rank it.</param>
    /// <param name="Gaps">Why some days have no value or no return.</param>
    public sealed record AccountPerformance(
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
    /// One account's statistics, and its days if asked for.
    /// </summary>
    /// <param name="Statistics">The statistics.</param>
    /// <param name="Days">Its days, or null.</param>
    public sealed record AccountStatistics(AccountPerformance Statistics, IReadOnlyList<AccountDay>? Days);

    /// <summary>
    /// A tenant's account statistics.
    /// </summary>
    /// <param name="AsOf">When the rules derived them.</param>
    /// <param name="Accounts">Each account.</param>
    public sealed record TenantStatistics(DateTimeOffset AsOf, IReadOnlyList<AccountStatistics> Accounts);
}
