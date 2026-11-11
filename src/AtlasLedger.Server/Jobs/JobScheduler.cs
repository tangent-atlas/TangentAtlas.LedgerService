// <copyright file="JobScheduler.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Jobs
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Specialized;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Quartz;
    using Quartz.Impl;
    using Quartz.Impl.Calendar;
    using Quartz.Spi;

    /// <summary>
    /// Settings for the job scheduler.
    /// </summary>
    public sealed class JobOptions
    {
        /// <summary>
        /// The settings section.
        /// </summary>
        public const string SectionName = "Jobs";

        /// <summary>
        /// Gets or sets how often the scheduler compares the tenants' schedules with what it has scheduled.
        /// </summary>
        public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets a value indicating whether jobs marked for market days skip the NYSE's holidays (tests turn it off,
        /// so they run on any day).
        /// </summary>
        public bool MarketHolidays { get; set; } = true;
    }

    /// <summary>
    /// Where a schedule stands.
    /// </summary>
    public enum JobScheduleStatus
    {
        /// <summary>Scheduled: it will run at its next time.</summary>
        Scheduled,

        /// <summary>Not scheduled: no job has its key.</summary>
        UnknownJob,

        /// <summary>Not scheduled: the cron expression or the time zone is not valid.</summary>
        InvalidSchedule,
    }

    /// <summary>
    /// A schedule's state, as the scheduler holds it.  In memory only: what a run did is in its results and the log.
    /// </summary>
    /// <param name="JobScheduleId">The schedule.</param>
    /// <param name="Job">The job's key.</param>
    /// <param name="CronSchedule">When it runs.</param>
    /// <param name="TimeZone">The zone the cron expression is read in.</param>
    /// <param name="Status">Where it stands.</param>
    /// <param name="Message">Why it isn't scheduled, if it isn't.</param>
    /// <param name="NextRun">When it runs next, if scheduled.</param>
    /// <param name="LastRun">When it last ran since the server started.</param>
    /// <param name="LastOutcome">What that run did.</param>
    public sealed record JobState(
        Guid JobScheduleId,
        string Job,
        string CronSchedule,
        string TimeZone,
        JobScheduleStatus Status,
        string? Message,
        DateTimeOffset? NextRun,
        DateTimeOffset? LastRun,
        string? LastOutcome);

    /// <summary>
    /// The schedules' states, by tenant.
    /// </summary>
    public sealed class JobStatusStore
    {
        private readonly ConcurrentDictionary<(Guid TenantId, Guid JobScheduleId), JobState> states = new ConcurrentDictionary<(Guid TenantId, Guid JobScheduleId), JobState>();

        /// <summary>
        /// Gets a tenant's schedules' states.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <returns>The states.</returns>
        public IReadOnlyList<JobState> For(Guid tenantId) =>
            this.states.Where(item => item.Key.TenantId == tenantId).Select(item => item.Value).OrderBy(state => state.Job, StringComparer.Ordinal).ThenBy(state => state.JobScheduleId).ToArray();

        /// <summary>
        /// Sets a schedule's state.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="state">The state.</param>
        public void Set(Guid tenantId, JobState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            this.states[(tenantId, state.JobScheduleId)] = state;
        }

        /// <summary>
        /// Records a run.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="jobScheduleId">The schedule.</param>
        /// <param name="time">When it ran.</param>
        /// <param name="outcome">What it did.</param>
        /// <param name="nextRun">When it runs next.</param>
        public void Ran(Guid tenantId, Guid jobScheduleId, DateTimeOffset time, string outcome, DateTimeOffset? nextRun)
        {
            // A schedule deleted while it ran is gone: nothing to record against.
            var key = (tenantId, jobScheduleId);
            while (this.states.TryGetValue(key, out var state) && !this.states.TryUpdate(key, state with { LastRun = time, LastOutcome = outcome, NextRun = nextRun }, state))
            {
            }
        }

        /// <summary>
        /// Forgets a schedule.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="jobScheduleId">The schedule.</param>
        public void Remove(Guid tenantId, Guid jobScheduleId) => this.states.TryRemove((tenantId, jobScheduleId), out _);
    }

    /// <summary>
    /// Runs every tenant's scheduled jobs, with Quartz.NET (in memory): each JobSchedule row is a cron trigger for the job
    /// its key names, run for that tenant.  It starts after the tenants are recovered, and every few seconds compares each
    /// tenant's rows with what it has scheduled: a new row is scheduled, a changed one rescheduled, a deleted one
    /// unscheduled.  A missed run (the server down at its time) is skipped, not made up.  Jobs marked for market days skip
    /// the NYSE's holidays.
    /// </summary>
    public sealed partial class JobScheduler : BackgroundService
    {
        private const string MarketCalendarName = "nyse";

        private static readonly TimeZoneInfo Exchange = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        private readonly Ledger ledger;

        private readonly IServiceProvider services;

        private readonly JobRegistry registry;

        private readonly JobStatusStore status;

        private readonly JobOptions options;

        private readonly ILogger<JobScheduler> logger;

        private readonly Dictionary<(Guid TenantId, Guid JobScheduleId), (string Job, string CronSchedule, string TimeZone)> scheduled = new Dictionary<(Guid TenantId, Guid JobScheduleId), (string Job, string CronSchedule, string TimeZone)>();

        private IScheduler? scheduler;

        /// <summary>
        /// Initializes a new instance of the <see cref="JobScheduler"/> class.
        /// </summary>
        /// <param name="ledger">The ledger, for each tenant's schedules.</param>
        /// <param name="services">The services, to make each run's job.</param>
        /// <param name="registry">The jobs.</param>
        /// <param name="status">Where the schedules' states are kept.</param>
        /// <param name="options">The settings.</param>
        /// <param name="logger">The log.</param>
        public JobScheduler(Ledger ledger, IServiceProvider services, JobRegistry registry, JobStatusStore status, IOptions<JobOptions> options, ILogger<JobScheduler> logger)
        {
            ArgumentNullException.ThrowIfNull(options);
            this.ledger = ledger;
            this.services = services;
            this.registry = registry;
            this.status = status;
            this.options = options.Value;
            this.logger = logger;
        }

        /// <summary>
        /// The NYSE's holidays as a Quartz calendar, in New York time.
        /// </summary>
        /// <returns>The calendar.</returns>
        public static HolidayCalendar MarketHolidays()
        {
            var calendar = new HolidayCalendar { TimeZone = Exchange };
            foreach (var day in MarketCalendar.Holidays)
            {
                calendar.AddExcludedDate(day.ToDateTime(TimeOnly.MinValue));
            }

            return calendar;
        }

        /// <inheritdoc/>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
            if (this.scheduler is { } running)
            {
                await running.Shutdown(waitForJobsToComplete: true, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // Quartz keeps schedulers by name, process-wide: each server gets its own.
                var factory = new StdSchedulerFactory(new NameValueCollection
                {
                    ["quartz.scheduler.instanceName"] = $"AtlasLedger-{Guid.NewGuid():N}",
                    ["quartz.threadPool.maxConcurrency"] = "4",
                });
                this.scheduler = await factory.GetScheduler(stoppingToken).ConfigureAwait(false);
                this.scheduler.JobFactory = new ServiceJobFactory(this.services);
                if (this.options.MarketHolidays)
                {
                    await this.scheduler.AddCalendar(MarketCalendarName, MarketHolidays(), replace: true, updateTriggers: true, stoppingToken).ConfigureAwait(false);
                    if (DateTimeOffset.UtcNow.Year >= MarketCalendar.LastYear)
                    {
                        this.LogCalendarEnding(MarketCalendar.LastYear);
                    }
                }

                await this.scheduler.Start(stoppingToken).ConfigureAwait(false);
                this.LogStarted(this.registry.All.Count, string.Join(", ", this.registry.All.Select(job => job.Key).Order(StringComparer.Ordinal)));
                using var timer = new PeriodicTimer(this.options.ReconcileInterval);
                do
                {
                    await this.ReconcileAsync(this.scheduler, stoppingToken).ConfigureAwait(false);
                }
                while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task ReconcileAsync(IScheduler quartz, CancellationToken cancellationToken)
        {
            var wanted = new Dictionary<(Guid TenantId, Guid JobScheduleId), (string Job, string CronSchedule, string TimeZone)>();
            foreach (var partition in this.ledger.Partitions.ToArray())
            {
                if (partition.IsFaulted)
                {
                    continue;
                }

                lock (partition.SyncRoot)
                {
                    foreach (var row in partition.JobSchedules)
                    {
                        wanted[(partition.TenantId, row.JobScheduleId)] = (row.Job, row.CronSchedule, row.TimeZone);
                    }
                }
            }

            foreach (var key in this.scheduled.Keys.Where(key => !wanted.ContainsKey(key)).ToArray())
            {
                await quartz.DeleteJob(JobKeyOf(key), cancellationToken).ConfigureAwait(false);
                this.scheduled.Remove(key);
                this.status.Remove(key.TenantId, key.JobScheduleId);
                this.LogUnscheduled(key.TenantId, key.JobScheduleId);
            }

            foreach (var (key, schedule) in wanted.Where(item => !this.scheduled.TryGetValue(item.Key, out var current) || current != item.Value))
            {
                this.scheduled[key] = schedule;
                await this.ScheduleAsync(quartz, key, schedule, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ScheduleAsync(IScheduler quartz, (Guid TenantId, Guid JobScheduleId) key, (string Job, string CronSchedule, string TimeZone) schedule, CancellationToken cancellationToken)
        {
            var jobKey = JobKeyOf(key);
            await quartz.DeleteJob(jobKey, cancellationToken).ConfigureAwait(false);
            JobState State(JobScheduleStatus status, string? message, DateTimeOffset? next) =>
                new JobState(key.JobScheduleId, schedule.Job, schedule.CronSchedule, schedule.TimeZone, status, message, next, null, null);

            if (this.registry.Find(schedule.Job) is not { } job)
            {
                var message = $"No job has the key \"{schedule.Job}\". The jobs are: {string.Join(", ", this.registry.All.Select(item => item.Key).Order(StringComparer.Ordinal))}.";
                this.status.Set(key.TenantId, State(JobScheduleStatus.UnknownJob, message, null));
                this.LogNotScheduled(key.TenantId, key.JobScheduleId, message);
                return;
            }

            TimeZoneInfo zone;
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone);
            }
            catch (TimeZoneNotFoundException)
            {
                var message = $"\"{schedule.TimeZone}\" is not a time zone (use an IANA name, such as America/New_York).";
                this.status.Set(key.TenantId, State(JobScheduleStatus.InvalidSchedule, message, null));
                this.LogNotScheduled(key.TenantId, key.JobScheduleId, message);
                return;
            }

            if (!CronExpression.IsValidExpression(schedule.CronSchedule))
            {
                var message = $"\"{schedule.CronSchedule}\" is not a Quartz cron expression (seconds minutes hours day-of-month month day-of-week, such as 0 45 15 ? * MON-FRI).";
                this.status.Set(key.TenantId, State(JobScheduleStatus.InvalidSchedule, message, null));
                this.LogNotScheduled(key.TenantId, key.JobScheduleId, message);
                return;
            }

            var detail = JobBuilder.Create<TenantJobRunner>()
                .WithIdentity(jobKey)
                .UsingJobData(TenantJobRunner.TenantIdKey, key.TenantId.ToString())
                .UsingJobData(TenantJobRunner.JobScheduleIdKey, key.JobScheduleId.ToString())
                .UsingJobData(TenantJobRunner.JobKey, job.Key)
                .Build();
            var trigger = TriggerBuilder.Create()
                .WithIdentity(new TriggerKey(jobKey.Name, jobKey.Group))
                .ForJob(jobKey)
                .WithSchedule(CronScheduleBuilder.CronSchedule(schedule.CronSchedule).InTimeZone(zone).WithMisfireHandlingInstructionDoNothing());
            if (job.MarketDaysOnly && this.options.MarketHolidays)
            {
                trigger = trigger.ModifiedByCalendar(MarketCalendarName);
            }

            var built = trigger.Build();
            var first = await quartz.ScheduleJob(detail, built, cancellationToken).ConfigureAwait(false);
            this.status.Set(key.TenantId, State(JobScheduleStatus.Scheduled, null, first));
            this.LogScheduled(key.TenantId, job.Key, schedule.CronSchedule, schedule.TimeZone, first);
        }

        private static JobKey JobKeyOf((Guid TenantId, Guid JobScheduleId) key) => new JobKey(key.JobScheduleId.ToString("N"), key.TenantId.ToString("N"));

        [LoggerMessage(Level = LogLevel.Information, Message = "Jobs: scheduler started with {Count} job(s): {Jobs}.")]
        private partial void LogStarted(int count, string jobs);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Jobs: the market holidays run only through {LastYear}; add the next years to MarketCalendar.")]
        private partial void LogCalendarEnding(int lastYear);

        [LoggerMessage(Level = LogLevel.Information, Message = "Jobs, tenant {TenantId}: {Job} scheduled ({CronSchedule}, {TimeZone}); next run {NextRun}.")]
        private partial void LogScheduled(Guid tenantId, string job, string cronSchedule, string timeZone, DateTimeOffset nextRun);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Jobs, tenant {TenantId}: schedule {JobScheduleId} not scheduled: {Message}")]
        private partial void LogNotScheduled(Guid tenantId, Guid jobScheduleId, string message);

        [LoggerMessage(Level = LogLevel.Information, Message = "Jobs, tenant {TenantId}: schedule {JobScheduleId} removed.")]
        private partial void LogUnscheduled(Guid tenantId, Guid jobScheduleId);
    }

    /// <summary>
    /// Makes Quartz's jobs from the server's services.
    /// </summary>
    internal sealed class ServiceJobFactory(IServiceProvider services) : IJobFactory
    {
        /// <inheritdoc/>
        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
        {
            ArgumentNullException.ThrowIfNull(bundle);
            return (IJob)ActivatorUtilities.CreateInstance(services, bundle.JobDetail.JobType);
        }

        /// <inheritdoc/>
        public void ReturnJob(IJob job) => (job as IDisposable)?.Dispose();
    }

    /// <summary>
    /// The one Quartz job: runs a tenant's job, found by its key among the registered jobs, and records the outcome.  One run
    /// of a schedule at a time.
    /// </summary>
    [DisallowConcurrentExecution]
    public sealed partial class TenantJobRunner : IJob
    {
        /// <summary>
        /// The job data key of the tenant.
        /// </summary>
        public const string TenantIdKey = "TenantId";

        /// <summary>
        /// The job data key of the schedule.
        /// </summary>
        public const string JobScheduleIdKey = "JobScheduleId";

        /// <summary>
        /// The job data key of the job's key.
        /// </summary>
        public const string JobKey = "Job";

        private readonly IServiceProvider services;

        private readonly JobRegistry registry;

        private readonly JobStatusStore status;

        private readonly ILogger<TenantJobRunner> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="TenantJobRunner"/> class.
        /// </summary>
        /// <param name="services">The services, to make the job.</param>
        /// <param name="registry">The jobs.</param>
        /// <param name="status">Where the outcome is recorded.</param>
        /// <param name="logger">The log.</param>
        public TenantJobRunner(IServiceProvider services, JobRegistry registry, JobStatusStore status, ILogger<TenantJobRunner> logger)
        {
            this.services = services;
            this.registry = registry;
            this.status = status;
            this.logger = logger;
        }

        /// <inheritdoc/>
        public async Task Execute(IJobExecutionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var data = context.MergedJobDataMap;
            var tenantId = Guid.Parse(data.GetString(TenantIdKey)!);
            var jobScheduleId = Guid.Parse(data.GetString(JobScheduleIdKey)!);
            var key = data.GetString(JobKey)!;
            string outcome;
            try
            {
                var job = this.registry.Find(key) ?? throw new InvalidOperationException($"No job has the key \"{key}\".");
                var instance = (ITenantJob)ActivatorUtilities.CreateInstance(this.services, job.Type);
                outcome = await instance.RunAsync(tenantId, context.CancellationToken).ConfigureAwait(false);
                this.LogRan(tenantId, key, outcome);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                outcome = $"Failed: {exception.Message}";
                this.LogFailed(exception, tenantId, key);
            }

            this.status.Ran(tenantId, jobScheduleId, context.FireTimeUtc, outcome, context.NextFireTimeUtc);
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Jobs, tenant {TenantId}: {Job} ran: {Outcome}")]
        private partial void LogRan(Guid tenantId, string job, string outcome);

        [LoggerMessage(Level = LogLevel.Error, Message = "Jobs, tenant {TenantId}: {Job} failed.")]
        private partial void LogFailed(Exception exception, Guid tenantId, string job);
    }
}
