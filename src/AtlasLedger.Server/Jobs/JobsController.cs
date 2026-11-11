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
    using AtlasLedger.Model.Primary;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;

    /// <summary>
    /// A job the server has, and what it is doing for the tenant.
    /// </summary>
    /// <param name="Job">Its key.</param>
    /// <param name="Kind">Task or service.</param>
    /// <param name="MarketDaysOnly">Whether its schedules skip market holidays.</param>
    /// <param name="IsRunning">Whether it is running now.</param>
    /// <param name="RunningSince">When the run in progress started.</param>
    /// <param name="StartedBy">The schedule that started it, or null if it was run by hand.</param>
    /// <param name="LastRun">When its last run ended, since the server started.</param>
    /// <param name="LastOutcome">What that run did.</param>
    public sealed record JobDto(string Job, JobKind Kind, bool MarketDaysOnly, bool IsRunning, DateTimeOffset? RunningSince, Guid? StartedBy, DateTimeOffset? LastRun, string? LastOutcome);

    /// <summary>
    /// A tenant's jobs.
    /// </summary>
    /// <param name="Jobs">Every job the server has, and what it is doing for the tenant.</param>
    /// <param name="Schedules">The tenant's schedule rows as the scheduler holds them.</param>
    public sealed record JobsDto(IReadOnlyList<JobDto> Jobs, IReadOnlyList<ScheduleState> Schedules);

    /// <summary>
    /// What a task run by hand did.
    /// </summary>
    /// <param name="Job">The task.</param>
    /// <param name="Outcome">What it did.</param>
    public sealed record JobRunDto(string Job, string Outcome);

    /// <summary>
    /// Serves a tenant's jobs: what each is doing and where each schedule stands, and runs a task by hand.  The schedules
    /// themselves are the tenant's JobSchedule rows, read and written through their table: turning a job on or off is a
    /// change to its row.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("v1/tenants/{tenantId:guid}/jobs")]
    public sealed class JobsController : ControllerBase
    {
        private readonly JobRegistry registry;

        private readonly JobRunner runner;

        private readonly JobScheduler scheduler;

        /// <summary>
        /// Initializes a new instance of the <see cref="JobsController"/> class.
        /// </summary>
        /// <param name="registry">The jobs.</param>
        /// <param name="runner">Runs them.</param>
        /// <param name="scheduler">Acts on the schedules.</param>
        public JobsController(JobRegistry registry, JobRunner runner, JobScheduler scheduler)
        {
            this.registry = registry;
            this.runner = runner;
            this.scheduler = scheduler;
        }

        /// <summary>
        /// Gets the tenant's jobs.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>Every job and the tenant's schedules.</returns>
        [HttpGet]
        [Authorize(Policy = LedgerAccessPolicies.JobScheduleRead)]
        public async Task<ActionResult<JobsDto>> GetAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            var jobs = this.runner.ActivityOf(tenantId).Select(activity =>
            {
                var job = this.registry.Find(activity.Job)!;
                return new JobDto(job.Key, job.Kind, job.MarketDaysOnly, activity.IsRunning, activity.RunningSince, activity.StartedBy, activity.LastRun, activity.LastOutcome);
            }).ToArray();
            return this.Ok(new JobsDto(jobs, await this.scheduler.SchedulesOfAsync(tenantId, cancellationToken).ConfigureAwait(false)));
        }

        /// <summary>
        /// Runs a task now, whatever its schedules say, and waits for it.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="job">The task's key.</param>
        /// <returns>What it did; 404 if no job has the key; 409 if it is a service or already running.</returns>
        [HttpPost("{job}/run")]
        [Authorize(Policy = LedgerAccessPolicies.JobScheduleWrite)]
        public async Task<ActionResult<JobRunDto>> RunAsync(Guid tenantId, string job)
        {
            var (result, outcome) = await this.runner.RunNowAsync(tenantId, job).ConfigureAwait(false);
            return result switch
            {
                RunRequest.Ran => this.Ok(new JobRunDto(job, outcome!)),
                RunRequest.UnknownJob => this.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such job.", detail: $"No job has the key \"{job}\". The jobs are: {this.registry.Keys}."),
                RunRequest.NotATask => this.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not a task.", detail: $"{job} is a service: turn it on (its schedule's Status On) rather than run it."),
                _ => this.Problem(statusCode: StatusCodes.Status409Conflict, title: "Already running.", detail: $"{job} is already running for this tenant."),
            };
        }
    }
}
