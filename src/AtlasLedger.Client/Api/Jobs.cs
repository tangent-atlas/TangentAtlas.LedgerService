// <copyright file="Jobs.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Api
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A schedule's state, as the server's scheduler holds it.
    /// </summary>
    /// <param name="JobScheduleId">The schedule.</param>
    /// <param name="Job">The job's key.</param>
    /// <param name="CronSchedule">When it runs.</param>
    /// <param name="TimeZone">The zone the cron expression is read in.</param>
    /// <param name="Status">Scheduled, UnknownJob or InvalidSchedule.</param>
    /// <param name="Message">Why it isn't scheduled, if it isn't.</param>
    /// <param name="NextRun">When it runs next, if scheduled.</param>
    /// <param name="LastRun">When it last ran since the server started.</param>
    /// <param name="LastOutcome">What that run did.</param>
    public sealed record JobState(
        Guid JobScheduleId,
        string Job,
        string CronSchedule,
        string TimeZone,
        string Status,
        string? Message,
        DateTimeOffset? NextRun,
        DateTimeOffset? LastRun,
        string? LastOutcome);

    /// <summary>
    /// A tenant's scheduled jobs.
    /// </summary>
    /// <param name="Jobs">The jobs the server has, by key.</param>
    /// <param name="Schedules">Each of the tenant's schedules.</param>
    public sealed record TenantJobs(IReadOnlyList<string> Jobs, IReadOnlyList<JobState> Schedules);
}
