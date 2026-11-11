// <copyright file="JobsController.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Jobs
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using AtlasLedger.Model.Primary;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Mvc;

    /// <summary>
    /// A tenant's scheduled jobs.
    /// </summary>
    /// <param name="Jobs">The jobs the server has, by key: what a schedule's Job may name.</param>
    /// <param name="Schedules">Each of the tenant's schedules, as the scheduler holds it.</param>
    public sealed record JobsDto(IReadOnlyList<string> Jobs, IReadOnlyList<JobState> Schedules);

    /// <summary>
    /// Serves where a tenant's scheduled jobs stand: scheduled or not (and why not), and the last run's outcome since the
    /// server started.  The schedules themselves are the tenant's JobSchedule rows, read and written through their table.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("v1/tenants/{tenantId:guid}/jobs")]
    public sealed class JobsController : ControllerBase
    {
        private readonly JobRegistry registry;

        private readonly JobStatusStore status;

        /// <summary>
        /// Initializes a new instance of the <see cref="JobsController"/> class.
        /// </summary>
        /// <param name="registry">The jobs.</param>
        /// <param name="status">The schedules' states.</param>
        public JobsController(JobRegistry registry, JobStatusStore status)
        {
            this.registry = registry;
            this.status = status;
        }

        /// <summary>
        /// Gets where the tenant's scheduled jobs stand.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <returns>The jobs and the schedules' states.</returns>
        [HttpGet]
        [Authorize(Policy = LedgerAccessPolicies.JobScheduleRead)]
        public ActionResult<JobsDto> Get(Guid tenantId) =>
            this.Ok(new JobsDto(this.registry.All.Select(job => job.Key).Order(StringComparer.Ordinal).ToArray(), this.status.For(tenantId)));
    }
}
