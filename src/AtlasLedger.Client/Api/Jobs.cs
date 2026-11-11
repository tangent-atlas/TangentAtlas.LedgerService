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
    /// <param name="IsRunning">Whether it is running now.</param>
    /// <param name="RunningSince">When the run in progress started.</param>
    /// <param name="StartedBy">The schedule that started it, or null if it was run by hand.</param>
    /// <param name="LastRun">When its last run ended, since the server started.</param>
    /// <param name="LastOutcome">What that run did.</param>
    public sealed record JobActivity(string Job, string Kind, bool MarketDaysOnly, bool IsRunning, DateTimeOffset? RunningSince, Guid? StartedBy, DateTimeOffset? LastRun, string? LastOutcome);

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
    public sealed record TenantJobs(IReadOnlyList<JobActivity> Jobs, IReadOnlyList<JobScheduleState> Schedules);

    /// <summary>
    /// What a task run by hand did.
    /// </summary>
    /// <param name="Job">The task.</param>
    /// <param name="Outcome">What it did.</param>
    public sealed record JobRun(string Job, string Outcome);
}
