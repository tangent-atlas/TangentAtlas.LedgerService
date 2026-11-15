// <copyright file="Jobs.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Api
{
    using System;
    using System.Collections.Generic;
    using AtlasLedger.Model;

    /// <summary>
    /// A job the server has, and what it is doing for the tenant.
    /// </summary>
    /// <param name="Job">Its key.</param>
    /// <param name="Kind">Task or Service.</param>
    /// <param name="MarketDaysOnly">Whether its schedules skip market holidays.</param>
    /// <param name="Running">The run in progress, if any.</param>
    /// <param name="Last">The latest run that has ended, if any.</param>
    public sealed record JobActivity(string Job, string Kind, bool MarketDaysOnly, JobRunDto? Running, JobRunDto? Last);

    /// <summary>
    /// A schedule row as the server's scheduler holds it.
    /// </summary>
    /// <param name="JobScheduleId">The schedule.</param>
    /// <param name="Job">The job's key.</param>
    /// <param name="Kind">Task or Service, if the job is known.</param>
    /// <param name="Status">What the row asks for.</param>
    /// <param name="CronSchedule">When a task runs, or a service starts.</param>
    /// <param name="StopSchedule">When a service stops.</param>
    /// <param name="TimeZone">The zone the cron expressions are read in.</param>
    /// <param name="Description">The row's description.</param>
    /// <param name="Problem">Why the row can't be acted on, or null.</param>
    /// <param name="NextStart">When a task next runs, or a service next starts.</param>
    /// <param name="NextStop">When a service next stops.</param>
    public sealed record JobScheduleState(
        Guid JobScheduleId,
        string Job,
        string? Kind,
        JobStatus Status,
        string? CronSchedule,
        string? StopSchedule,
        string? TimeZone,
        string? Description,
        string? Problem,
        DateTimeOffset? NextStart,
        DateTimeOffset? NextStop);

    /// <summary>
    /// A tenant's jobs.
    /// </summary>
    /// <param name="Jobs">Every job the server has, and what it is doing for the tenant.</param>
    /// <param name="Schedules">The tenant's schedule rows.</param>
    /// <param name="Runs">The tenant's latest runs, newest first.</param>
    public sealed record TenantJobs(IReadOnlyList<JobActivity> Jobs, IReadOnlyList<JobScheduleState> Schedules, IReadOnlyList<JobRunDto> Runs);
}
