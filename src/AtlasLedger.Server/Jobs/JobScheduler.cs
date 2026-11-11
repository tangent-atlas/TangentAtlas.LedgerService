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

        /// <summary>Not scheduled: the row's IsEnabled is off.</summary>
        Disabled,

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
        /// Sets a schedule's state, keeping its last run.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="state">The state.</param>
        public void Set(Guid tenantId, JobState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            // A schedule changed or turned off keeps what its last run did: that run may have been the one just cancelled.
            this.states.AddOrUpdate((tenantId, state.JobScheduleId), state, (_, previous) => state with { LastRun = previous.LastRun, LastOutcome = previous.LastOutcome });
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
    /// Runs every tenant's scheduled jobs, with Quartz.NET (in memory): each enabled JobSchedule row is a cron trigger for
    /// the job its key names, run for that tenant.  It starts after the tenants are recovered and watches each tenant's
    /// commits (the same signal the rules engine waits on, outside the tenant's lock): after every commit it compares the
    /// tenant's rows with what it has scheduled.  A new or changed row is (re)scheduled at once; a row turned off or deleted
    /// is unscheduled, and a run of it in progress is cancelled.  A missed run (the server down at its time) is skipped, not
    /// made up.  Jobs marked for market days skip the NYSE's holidays.
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

        // Each tenant's schedules as last acted on; a tenant's entry is touched only by its own watcher.
        private readonly ConcurrentDictionary<Guid, Dictionary<Guid, Schedule>> tenants = new ConcurrentDictionary<Guid, Dictionary<Guid, Schedule>>();

        private CancellationToken stopping;

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
                this.stopping = stoppingToken;
                this.ledger.PartitionCreated += this.OnPartitionCreated;
                try
                {
                    foreach (var partition in this.ledger.Partitions.ToArray())
                    {
                        this.Watch(partition);
                    }

                    await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    this.ledger.PartitionCreated -= this.OnPartitionCreated;
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void OnPartitionCreated(object? sender, LedgerPartition partition) => this.Watch(partition);

        private void Watch(LedgerPartition partition)
        {
            if (this.scheduler is { } quartz && this.tenants.TryAdd(partition.TenantId, new Dictionary<Guid, Schedule>()))
            {
                _ = Task.Run(() => this.WatchAsync(quartz, partition, this.stopping), this.stopping);
            }
        }

        /// <summary>
        /// One tenant's watcher: acts on its rows, then waits for its next commit.  The sequence is read before acting, so a
        /// commit made while acting wakes it again.
        /// </summary>
        private async Task WatchAsync(IScheduler quartz, LedgerPartition partition, CancellationToken cancellationToken)
        {
            var tenantId = partition.TenantId;
            var scheduled = this.tenants[tenantId];
            try
            {
                while (true)
                {
                    var seen = partition.CommittedSequence;
                    if (partition.IsFaulted)
                    {
                        // The tenant is stopped: so are its jobs.
                        await this.ReconcileAsync(quartz, tenantId, scheduled, new Dictionary<Guid, Schedule>(), cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    var wanted = new Dictionary<Guid, Schedule>();
                    lock (partition.SyncRoot)
                    {
                        foreach (var row in partition.JobSchedules)
                        {
                            wanted[row.JobScheduleId] = new Schedule(row.Job, row.CronSchedule, row.TimeZone, row.IsEnabled);
                        }
                    }

                    await this.ReconcileAsync(quartz, tenantId, scheduled, wanted, cancellationToken).ConfigureAwait(false);
                    await partition.WaitForCommitAsync(seen, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                this.LogWatcherFailed(exception, tenantId);
            }
        }

        private async Task ReconcileAsync(IScheduler quartz, Guid tenantId, Dictionary<Guid, Schedule> scheduled, Dictionary<Guid, Schedule> wanted, CancellationToken cancellationToken)
        {
            foreach (var id in scheduled.Keys.Where(id => !wanted.ContainsKey(id)).ToArray())
            {
                await StopAsync(quartz, (tenantId, id), cancellationToken).ConfigureAwait(false);
                scheduled.Remove(id);
                this.status.Remove(tenantId, id);
                this.LogUnscheduled(tenantId, id);
            }

            foreach (var (id, schedule) in wanted.Where(item => !scheduled.TryGetValue(item.Key, out var current) || current != item.Value))
            {
                var isNew = !scheduled.ContainsKey(id);
                scheduled[id] = schedule;
                if (!isNew)
                {
                    await StopAsync(quartz, (tenantId, id), cancellationToken).ConfigureAwait(false);   // changed: what's running ran on the old row
                }

                await this.ScheduleAsync(quartz, (tenantId, id), schedule, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Unschedules a schedule and cancels a run of it in progress: the run's cancellation token fires, and the job stops
        /// at its next safe point (a ledger transaction is never cut in half).
        /// </summary>
        private static async Task StopAsync(IScheduler quartz, (Guid TenantId, Guid JobScheduleId) key, CancellationToken cancellationToken)
        {
            var jobKey = JobKeyOf(key);
            await quartz.Interrupt(jobKey, cancellationToken).ConfigureAwait(false);
            await quartz.DeleteJob(jobKey, cancellationToken).ConfigureAwait(false);
        }

        private async Task ScheduleAsync(IScheduler quartz, (Guid TenantId, Guid JobScheduleId) key, Schedule schedule, CancellationToken cancellationToken)
        {
            var jobKey = JobKeyOf(key);
            JobState State(JobScheduleStatus status, string? message, DateTimeOffset? next) =>
                new JobState(key.JobScheduleId, schedule.Job, schedule.CronSchedule, schedule.TimeZone, status, message, next, null, null);

            if (!schedule.IsEnabled)
            {
                this.status.Set(key.TenantId, State(JobScheduleStatus.Disabled, null, null));
                this.LogDisabled(key.TenantId, key.JobScheduleId, schedule.Job);
                return;
            }

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

        private sealed record Schedule(string Job, string CronSchedule, string TimeZone, bool IsEnabled);

        private static JobKey JobKeyOf((Guid TenantId, Guid JobScheduleId) key) => new JobKey(key.JobScheduleId.ToString("N"), key.TenantId.ToString("N"));

        [LoggerMessage(Level = LogLevel.Information, Message = "Jobs: scheduler started with {Count} job(s): {Jobs}.")]
        private partial void LogStarted(int count, string jobs);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Jobs: the market holidays run only through {LastYear}; add the next years to MarketCalendar.")]
        private partial void LogCalendarEnding(int lastYear);

        [LoggerMessage(Level = LogLevel.Information, Message = "Jobs, tenant {TenantId}: {Job} scheduled ({CronSchedule}, {TimeZone}); next run {NextRun}.")]
        private partial void LogScheduled(Guid tenantId, string job, string cronSchedule, string timeZone, DateTimeOffset nextRun);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Jobs, tenant {TenantId}: schedule {JobScheduleId} not scheduled: {Message}")]
        private partial void LogNotScheduled(Guid tenantId, Guid jobScheduleId, string message);

        [LoggerMessage(Level = LogLevel.Information, Message = "Jobs, tenant {TenantId}: {Job} (schedule {JobScheduleId}) turned off.")]
        private partial void LogDisabled(Guid tenantId, Guid jobScheduleId, string job);

        [LoggerMessage(Level = LogLevel.Error, Message = "Jobs, tenant {TenantId}: the scheduler stopped watching this tenant's schedules.")]
        private partial void LogWatcherFailed(Exception exception, Guid tenantId);

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
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                // Its schedule was turned off, changed or deleted, or the server is stopping.
                outcome = "Cancelled.";
                this.LogCancelled(tenantId, key);
            }
            catch (Exception exception)
            {
                outcome = $"Failed: {exception.Message}";
                this.LogFailed(exception, tenantId, key);
            }

            this.status.Ran(tenantId, jobScheduleId, context.FireTimeUtc, outcome, context.NextFireTimeUtc);
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Jobs, tenant {TenantId}: {Job} ran: {Outcome}")]
        private partial void LogRan(Guid tenantId, string job, string outcome);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Jobs, tenant {TenantId}: {Job} cancelled.")]
        private partial void LogCancelled(Guid tenantId, string job);

        [LoggerMessage(Level = LogLevel.Error, Message = "Jobs, tenant {TenantId}: {Job} failed.")]
        private partial void LogFailed(Exception exception, Guid tenantId, string job);
    }
}
