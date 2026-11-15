// <copyright file="StatisticsController.cs" company="Tangent Atlas">
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
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;

    /// <summary>
    /// One account's statistics, and its days if asked for.
    /// </summary>
    /// <param name="Statistics">The statistics.</param>
    /// <param name="Days">Each market day's value, flow and return; null unless asked for.</param>
    public sealed record AccountStatisticsDto(AccountStatistics Statistics, IReadOnlyList<AccountDay>? Days);

    /// <summary>
    /// A tenant's account statistics, as the rules derived them.
    /// </summary>
    /// <param name="AsOf">When the rules derived them.</param>
    /// <param name="Accounts">Each account held somewhere.</param>
    public sealed record StatisticsDto(DateTimeOffset AsOf, IReadOnlyList<AccountStatisticsDto> Accounts);

    /// <summary>
    /// Serves each account's performance: its value at each close, its daily returns, and the statistics measured from them
    /// (total and annualized return, volatility, worst drawdown, Sharpe ratio against the 13-week Treasury bill).
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("v1/tenants/{tenantId:guid}/statistics")]
    public sealed class StatisticsController : ControllerBase
    {
        private readonly HoldingsStore store;

        /// <summary>
        /// Initializes a new instance of the <see cref="StatisticsController"/> class.
        /// </summary>
        /// <param name="store">The rules' published snapshots.</param>
        public StatisticsController(HoldingsStore store)
        {
            this.store = store;
        }

        /// <summary>
        /// Gets the statistics.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="days">Whether to include each day.</param>
        /// <returns>The statistics; 409 while the rules haven't run.</returns>
        [HttpGet]
        [Authorize(Policy = LedgerAccessPolicies.EventRead)]
        public ActionResult<StatisticsDto> Get(Guid tenantId, [FromQuery] bool days = false)
        {
            if (this.store.Find(tenantId) is not { } snapshot)
            {
                return this.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "No statistics yet.",
                    detail: "The rules engine hasn't run for this tenant since it was switched on. Switch it on (services on rules) and try again.");
            }

            return new StatisticsDto(
                snapshot.AsOf,
                snapshot.Histories
                    .OrderBy(history => history.AccountId)
                    .Select(history => new AccountStatisticsDto(history.Statistics, days ? history.Days : null))
                    .ToArray());
        }
    }
}
