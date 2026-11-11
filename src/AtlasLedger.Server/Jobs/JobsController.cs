// <copyright file="JobsController.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Jobs
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Mvc;

    /// <summary>
    /// A job the server has, and what it is doing for the tenant, from the tenant's JobRun log.
    /// </summary>
    /// <param name="Job">Its key.</param>
    /// <param name="Kind">Task or service.</param>
    /// <param name="MarketDaysOnly">Whether its schedules skip market holidays.</param>
    /// <param name="Running">The run in progress, if any.</param>
    /// <param name="Last">The latest run that has ended, if any.</param>
    public sealed record JobDto(string Job, JobKind Kind, bool MarketDaysOnly, JobRunDto? Running, JobRunDto? Last);

    /// <summary>
    /// A tenant's jobs.
    /// </summary>
    /// <param name="Jobs">Every job the server has, and what it is doing for the tenant.</param>
    /// <param name="Schedules">The tenant's schedule rows as the scheduler holds them.</param>
    /// <param name="Runs">The tenant's latest runs, newest first.</param>
    public sealed record JobsDto(IReadOnlyList<JobDto> Jobs, IReadOnlyList<ScheduleState> Schedules, IReadOnlyList<JobRunDto> Runs);

    /// <summary>
    /// Serves a tenant's jobs: what each is doing, where each schedule stands, and the latest runs.  Schedules and runs are
    /// rows (JobSchedule, JobRun), written through their tables: turning a job on or off is a change to its schedule; a run
    /// by hand is a JobRun row inserted as Requested.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("v1/tenants/{tenantId:guid}/jobs")]
    public sealed class JobsController : ControllerBase
    {
        private const int LatestRuns = 25;

        private readonly Ledger ledger;

        private readonly JobRegistry registry;

        private readonly JobScheduler scheduler;

        /// <summary>
        /// Initializes a new instance of the <see cref="JobsController"/> class.
        /// </summary>
        /// <param name="ledger">The ledger, for the runs.</param>
        /// <param name="registry">The jobs.</param>
        /// <param name="scheduler">Acts on the schedules.</param>
        public JobsController(Ledger ledger, JobRegistry registry, JobScheduler scheduler)
        {
            this.ledger = ledger;
            this.registry = registry;
            this.scheduler = scheduler;
        }

        /// <summary>
        /// Gets the tenant's jobs.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>Every job, the tenant's schedules, and its latest runs.</returns>
        [HttpGet]
        [Authorize(Policy = LedgerAccessPolicies.JobRunRead)]
        public async Task<ActionResult<JobsDto>> GetAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            JobRunDto[] runs = [];
            if (this.ledger.FindPartition(tenantId) is { } partition)
            {
                lock (partition.SyncRoot)
                {
                    runs = partition.JobRuns.Select(run => run.ToDto()).ToArray();
                }
            }

            static DateTimeOffset Latest(JobRunDto run) => run.EndTime ?? run.StartTime ?? run.RequestedTime;
            var jobs = this.registry.All.OrderBy(job => job.Key, StringComparer.Ordinal).Select(job =>
            {
                var mine = runs.Where(run => run.Job == job.Key).ToArray();
                return new JobDto(
                    job.Key,
                    job.Kind,
                    job.MarketDaysOnly,
                    mine.Where(run => run.Status == JobRunStatus.Running).MaxBy(run => run.StartTime),
                    mine.Where(run => run.Status is not (JobRunStatus.Running or JobRunStatus.Requested)).MaxBy(Latest));
            }).ToArray();
            var latest = runs.OrderByDescending(Latest).ThenByDescending(run => run.JobRunId).Take(LatestRuns).ToArray();
            return this.Ok(new JobsDto(jobs, await this.scheduler.SchedulesOfAsync(tenantId, cancellationToken).ConfigureAwait(false), latest));
        }
    }
}
