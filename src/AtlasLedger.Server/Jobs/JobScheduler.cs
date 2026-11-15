// <copyright file="JobScheduler.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Jobs
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Collections.Specialized;
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
    /// Settings for jobs.
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

        /// <summary>
        /// Gets or sets how long a service that ended or failed while it should be on waits before it is started again.
        /// </summary>
        public TimeSpan RestartDelay { get; set; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// A schedule row as the scheduler holds it: what it asks for, whether it can be acted on, and when it next starts and
    /// stops.  In memory only.
    /// </summary>
    /// <param name="JobScheduleId">The schedule.</param>
    /// <param name="Job">The job's key.</param>
    /// <param name="Kind">Task or service, if the job is known.</param>
    /// <param name="Status">What the row asks for.</param>
    /// <param name="CronSchedule">When a task runs, or a service starts.</param>
    /// <param name="StopSchedule">When a service stops.</param>
    /// <param name="TimeZone">The zone the cron expressions are read in.</param>
    /// <param name="Description">The row's description.</param>
    /// <param name="Problem">Why the row can't be acted on, or null.</param>
    /// <param name="NextStart">When a task next runs, or a service next starts.</param>
    /// <param name="NextStop">When a service next stops.</param>
    public sealed record ScheduleState(
        Guid JobScheduleId,
        string Job,
        JobKind? Kind,
        JobStatus Status,
        string? CronSchedule,
        string? StopSchedule,
        string? TimeZone,
        string? Description,
        string? Problem,
        DateTimeOffset? NextStart,
        DateTimeOffset? NextStop);

    /// <summary>
    /// Acts on every tenant's JobSchedule rows.  It starts after the tenants are recovered and watches each tenant's commits
    /// (the signal the rules engine waits on, outside the tenant's lock): after every commit it compares the tenant's rows
    /// with what it has acted on, so a change takes effect at once.
    /// <list type="bullet">
    /// <item>Off: nothing runs on its own; turning a row Off stops what it started, cancelling a task's run in progress.</item>
    /// <item>On: a service is started now and runs until the row changes.  A task can't be On.</item>
    /// <item>Scheduled: a task runs at each CronSchedule time (Quartz.NET, in memory); a service is on from each
    /// CronSchedule time to the next StopSchedule time, and is started at once if the row is set inside its window.</item>
    /// </list>
    /// A deleted row stops what it started.  A missed time (the server down) is skipped, not made up.  Jobs marked for
    /// market days skip the NYSE's holidays.
    /// </summary>
    public sealed partial class JobScheduler : BackgroundService
    {
        private const string MarketCalendarName = "nyse";

        private static readonly TimeZoneInfo Exchange = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        private readonly Ledger ledger;

        private readonly IServiceProvider services;

        private readonly JobRegistry registry;

        private readonly JobRunner runner;

        private readonly JobOptions options;

        private readonly TimeProvider timeProvider;

        private readonly ILogger<JobScheduler> logger;

        // Each tenant's rows as last acted on; a tenant's entry is written only by its own watcher, read by the API.
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, ScheduleState>> tenants = new ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, ScheduleState>>();

        private readonly TaskCompletionSource<IScheduler> started = new TaskCompletionSource<IScheduler>(TaskCreationOptions.RunContinuationsAsynchronously);

        private CancellationToken stopping;

        /// <summary>
        /// Initializes a new instance of the <see cref="JobScheduler"/> class.
        /// </summary>
        /// <param name="ledger">The ledger, for each tenant's schedules.</param>
        /// <param name="services">The services, to make Quartz's jobs.</param>
        /// <param name="registry">The jobs.</param>
        /// <param name="runner">Runs them.</param>
        /// <param name="options">The settings.</param>
        /// <param name="timeProvider">The clock.</param>
        /// <param name="logger">The log.</param>
        public JobScheduler(Ledger ledger, IServiceProvider services, JobRegistry registry, JobRunner runner, IOptions<JobOptions> options, TimeProvider timeProvider, ILogger<JobScheduler> logger)
        {
            ArgumentNullException.ThrowIfNull(options);
            this.ledger = ledger;
            this.services = services;
            this.registry = registry;
            this.runner = runner;
            this.options = options.Value;
            this.timeProvider = timeProvider;
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

        /// <summary>
        /// The first time a cron expression fires after a moment, skipping a calendar's excluded days.
        /// </summary>
        /// <param name="cron">The cron expression.</param>
        /// <param name="zone">The zone it is read in.</param>
        /// <param name="calendar">Days to skip, or null.</param>
        /// <param name="after">The moment.</param>
        /// <returns>The time, or null if it never fires again.</returns>
        public static DateTimeOffset? NextTime(string cron, TimeZoneInfo zone, ICalendar? calendar, DateTimeOffset after)
        {
            var trigger = (IOperableTrigger)TriggerBuilder.Create()
                .StartAt(after.AddMilliseconds(1))
                .WithSchedule(CronScheduleBuilder.CronSchedule(cron).InTimeZone(zone))
                .Build();
            return trigger.ComputeFirstFireTimeUtc(calendar);
        }

        /// <summary>
        /// Whether a moment falls inside a service's window: its next stop comes before its next start.
        /// </summary>
        /// <param name="start">When it starts.</param>
        /// <param name="stop">When it stops.</param>
        /// <param name="zone">The zone they are read in.</param>
        /// <param name="calendar">Days to skip, or null.</param>
        /// <param name="now">The moment.</param>
        /// <returns>True if the service should be on.</returns>
        public static bool IsInsideWindow(string start, string stop, TimeZoneInfo zone, ICalendar? calendar, DateTimeOffset now)
        {
            var nextStop = NextTime(stop, zone, calendar, now);
            var nextStart = NextTime(start, zone, calendar, now);
            return nextStop is { } stopping && (nextStart is not { } starting || stopping < starting);
        }

        /// <summary>
        /// Gets a tenant's schedule rows as the scheduler holds them, with their next start and stop.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The rows.</returns>
        public async Task<IReadOnlyList<ScheduleState>> SchedulesOfAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            if (!this.tenants.TryGetValue(tenantId, out var schedules) || !this.started.Task.IsCompletedSuccessfully)
            {
                return [];
            }

            var quartz = this.started.Task.Result;
            var states = new List<ScheduleState>();
            foreach (var state in schedules.Values.OrderBy(state => state.Job, StringComparer.Ordinal).ThenBy(state => state.JobScheduleId))
            {
                var start = await quartz.GetTrigger(TriggerOf(StartKey(tenantId, state.JobScheduleId)), cancellationToken).ConfigureAwait(false);
                var stop = await quartz.GetTrigger(TriggerOf(StopKey(tenantId, state.JobScheduleId)), cancellationToken).ConfigureAwait(false);
                states.Add(state with { NextStart = start?.GetNextFireTimeUtc(), NextStop = stop?.GetNextFireTimeUtc() });
            }

            return states;
        }

        /// <inheritdoc/>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
            if (this.started.Task.IsCompletedSuccessfully)
            {
                await this.started.Task.Result.Shutdown(waitForJobsToComplete: false, cancellationToken).ConfigureAwait(false);
            }

            await this.runner.DisposeAsync().ConfigureAwait(false);   // cancels every run, and waits for them to end
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
                var quartz = await factory.GetScheduler(stoppingToken).ConfigureAwait(false);
                quartz.JobFactory = new ServiceJobFactory(this.services);
                if (this.options.MarketHolidays)
                {
                    await quartz.AddCalendar(MarketCalendarName, MarketHolidays(), replace: true, updateTriggers: true, stoppingToken).ConfigureAwait(false);
                    if (this.timeProvider.GetUtcNow().Year >= MarketCalendar.LastYear)
                    {
                        this.LogCalendarEnding(MarketCalendar.LastYear);
                    }
                }

                await quartz.Start(stoppingToken).ConfigureAwait(false);
                this.stopping = stoppingToken;
                this.started.SetResult(quartz);
                this.LogStarted(this.registry.All.Count, this.registry.Keys);
                this.ledger.PartitionCreated += this.OnPartitionCreated;
                try
                {
                    foreach (var partition in this.ledger.Partitions.ToArray())
                    {
                        this.Watch(partition, isRecovered: true);
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

        private static JobKey StartKey(Guid tenantId, Guid scheduleId) => new JobKey(scheduleId.ToString("N"), tenantId.ToString("N"));

        private static JobKey StopKey(Guid tenantId, Guid scheduleId) => new JobKey($"{scheduleId:N}-stop", tenantId.ToString("N"));

        private static TriggerKey TriggerOf(JobKey key) => new TriggerKey(key.Name, key.Group);

        private static async Task UnscheduleAsync(IScheduler quartz, Guid tenantId, Guid id, CancellationToken cancellationToken)
        {
            await quartz.DeleteJob(StartKey(tenantId, id), cancellationToken).ConfigureAwait(false);
            await quartz.DeleteJob(StopKey(tenantId, id), cancellationToken).ConfigureAwait(false);
        }

        private void OnPartitionCreated(object? sender, LedgerPartition partition) => this.Watch(partition, isRecovered: false);

        /// <summary>
        /// Starts watching a tenant.  A recovered tenant (loaded at start-up) may hold runs from before the restart; a
        /// tenant created since can't.
        /// </summary>
        private void Watch(LedgerPartition partition, bool isRecovered)
        {
            if (this.started.Task.IsCompletedSuccessfully && this.tenants.TryAdd(partition.TenantId, new ConcurrentDictionary<Guid, ScheduleState>()))
            {
                _ = Task.Run(() => this.WatchAsync(this.started.Task.Result, partition, isRecovered, this.stopping), this.stopping);
            }
        }

        /// <summary>
        /// One tenant's watcher: acts on its rows, then waits for its next commit.  The sequence is read before acting, so a
        /// commit made while acting wakes it again.
        /// </summary>
        private async Task WatchAsync(IScheduler quartz, LedgerPartition partition, bool isRecovered, CancellationToken cancellationToken)
        {
            var tenantId = partition.TenantId;
            var acted = this.tenants[tenantId];
            var handled = new HashSet<Guid>();
            var isFirstPass = isRecovered;
            try
            {
                while (true)
                {
                    var seen = partition.CommittedSequence;
                    if (partition.IsFaulted)
                    {
                        // The tenant is stopped: so are its jobs.
                        foreach (var id in acted.Keys.ToArray())
                        {
                            await this.RemoveAsync(quartz, tenantId, acted, id, cancellationToken).ConfigureAwait(false);
                        }

                        this.runner.StopTenant(tenantId);
                        return;
                    }

                    var rows = new List<Row>();
                    lock (partition.SyncRoot)
                    {
                        foreach (var row in partition.JobSchedules)
                        {
                            rows.Add(new Row(row.JobScheduleId, row.Job, row.Status, row.CronSchedule, row.StopSchedule, row.TimeZone, row.Description));
                        }
                    }

                    await this.ReconcileAsync(quartz, tenantId, acted, rows, cancellationToken).ConfigureAwait(false);
                    this.TakeUpRuns(partition, acted, handled, isFirstPass);
                    isFirstPass = false;
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

        /// <summary>
        /// Takes up the runs asked for by hand: each Requested row is started or refused, once.  On the first pass after the
        /// server starts, a run still Requested or Running is from before: it is marked Interrupted, never run again.
        /// </summary>
        private void TakeUpRuns(LedgerPartition partition, ConcurrentDictionary<Guid, ScheduleState> acted, HashSet<Guid> handled, bool isFirstPass)
        {
            var tenantId = partition.TenantId;
            var open = new List<(Guid JobRunId, string Job, JobRunStatus Status)>();
            lock (partition.SyncRoot)
            {
                foreach (var run in partition.JobRuns)
                {
                    if (run.Status is JobRunStatus.Requested or JobRunStatus.Running)
                    {
                        open.Add((run.JobRunId, run.Job, run.Status));
                    }
                }
            }

            handled.IntersectWith(open.Select(run => run.JobRunId));
            foreach (var (id, key, status) in open)
            {
                if (isFirstPass)
                {
                    this.runner.Interrupt(tenantId, id);
                    continue;
                }

                if (status != JobRunStatus.Requested || !handled.Add(id))
                {
                    continue;
                }

                var schedules = acted.Values.Where(state => state.Job == key).ToArray();
                switch (this.registry.Find(key))
                {
                    case null:
                        this.runner.Refuse(tenantId, id, $"No job has the key \"{key}\". The jobs are: {this.registry.Keys}.");
                        break;
                    case { Kind: JobKind.Service }:
                        this.runner.Refuse(tenantId, id, $"{key} is a service: turn it on (its schedule's Status On) rather than run it.");
                        break;
                    case { } when schedules.Length > 0 && schedules.All(state => state.Status == JobStatus.Off):
                        this.runner.Refuse(tenantId, id, $"{key} is off: every schedule of it is Off.");
                        break;
                    case { } job:
                        this.runner.StartRequested(tenantId, job, id);
                        break;
                }
            }
        }

        private async Task ReconcileAsync(IScheduler quartz, Guid tenantId, ConcurrentDictionary<Guid, ScheduleState> acted, List<Row> rows, CancellationToken cancellationToken)
        {
            var ids = rows.Select(row => row.JobScheduleId).ToHashSet();
            foreach (var id in acted.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                await this.RemoveAsync(quartz, tenantId, acted, id, cancellationToken).ConfigureAwait(false);
                this.LogRemoved(tenantId, id);
            }

            foreach (var row in rows)
            {
                var previous = acted.GetValueOrDefault(row.JobScheduleId);
                if (previous is not null && previous.Job == row.Job && previous.Status == row.Status && previous.CronSchedule == row.CronSchedule
                    && previous.StopSchedule == row.StopSchedule && previous.TimeZone == row.TimeZone)
                {
                    if (previous.Description != row.Description)
                    {
                        acted[row.JobScheduleId] = previous with { Description = row.Description };
                    }

                    continue;
                }

                await this.ApplyAsync(quartz, tenantId, acted, previous, row, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task RemoveAsync(IScheduler quartz, Guid tenantId, ConcurrentDictionary<Guid, ScheduleState> acted, Guid id, CancellationToken cancellationToken)
        {
            if (acted.TryRemove(id, out var state))
            {
                await UnscheduleAsync(quartz, tenantId, id, cancellationToken).ConfigureAwait(false);
                this.runner.Stop(tenantId, state.Job, id);
            }
        }

        /// <summary>
        /// Acts on a new or changed row.  What the old row started is stopped, except a service that is to keep running
        /// under the new one (so moving a window that still covers now doesn't restart the service).
        /// </summary>
        private async Task ApplyAsync(IScheduler quartz, Guid tenantId, ConcurrentDictionary<Guid, ScheduleState> acted, ScheduleState? previous, Row row, CancellationToken cancellationToken)
        {
            var id = row.JobScheduleId;
            await UnscheduleAsync(quartz, tenantId, id, cancellationToken).ConfigureAwait(false);
            var job = this.registry.Find(row.Job);
            var (problem, zone) = this.Check(job, row);
            var shouldRun = problem is null && job is { Kind: JobKind.Service }
                && (row.Status == JobStatus.On || (row.Status == JobStatus.Scheduled && IsInsideWindow(row.CronSchedule!, row.StopSchedule!, zone!, this.CalendarFor(job), this.timeProvider.GetUtcNow())));
            if (previous is not null && !(shouldRun && previous.Job == row.Job))
            {
                this.runner.Stop(tenantId, previous.Job, id);
            }

            acted[id] = new ScheduleState(id, row.Job, job?.Kind, row.Status, row.CronSchedule, row.StopSchedule, row.TimeZone, row.Description, problem, null, null);
            if (problem is not null)
            {
                this.LogProblem(tenantId, id, row.Job, problem);
                return;
            }

            if (row.Status == JobStatus.Off)
            {
                this.LogOff(tenantId, row.Job, id);
                return;
            }

            if (job!.Kind == JobKind.Task)
            {
                var next = await this.ScheduleAsync(quartz, tenantId, id, job, ScheduleFire.Run, row.CronSchedule!, zone!, StartKey(tenantId, id), cancellationToken).ConfigureAwait(false);
                this.LogScheduled(tenantId, row.Job, row.CronSchedule!, row.TimeZone!, next);
                return;
            }

            if (row.Status == JobStatus.Scheduled)
            {
                var start = await this.ScheduleAsync(quartz, tenantId, id, job, ScheduleFire.Start, row.CronSchedule!, zone!, StartKey(tenantId, id), cancellationToken).ConfigureAwait(false);
                await this.ScheduleAsync(quartz, tenantId, id, job, ScheduleFire.Stop, row.StopSchedule!, zone!, StopKey(tenantId, id), cancellationToken).ConfigureAwait(false);
                this.LogScheduled(tenantId, row.Job, $"{row.CronSchedule} to {row.StopSchedule}", row.TimeZone!, start);
            }
            else
            {
                this.LogOn(tenantId, row.Job, id);
            }

            if (shouldRun)
            {
                this.runner.StartService(tenantId, job, id, row.Status == JobStatus.On ? JobRunReason.Schedule : JobRunReason.Window);   // on, or set inside its window: running now
            }
        }

        /// <summary>
        /// Why a row can't be acted on, or null; and its zone, when it needs one.
        /// </summary>
        private (string? Problem, TimeZoneInfo? Zone) Check(JobDescriptor? job, Row row)
        {
            if (job is null)
            {
                return ($"No job has the key \"{row.Job}\". The jobs are: {this.registry.Keys}.", null);
            }

            switch (job.Kind, row.Status)
            {
                case (_, JobStatus.Off):
                case (JobKind.Service, JobStatus.On):
                    return (null, null);
                case (JobKind.Task, JobStatus.On):
                    return ($"{job.Key} is a task: it runs and ends, so it can't be on. Set it to Scheduled, or run it by hand (ledger-operator jobs run).", null);
                case (_, JobStatus.Scheduled):
                    break;
                default:
                    return ($"{row.Status} is not a status.", null);
            }

            if (row.CronSchedule is not { } cron || !CronExpression.IsValidExpression(cron))
            {
                return ($"CronSchedule \"{row.CronSchedule}\" is not a Quartz cron expression (seconds minutes hours day-of-month month day-of-week, such as 0 45 15 ? * MON-FRI).", null);
            }

            if (job.Kind == JobKind.Service && (row.StopSchedule is not { } stop || !CronExpression.IsValidExpression(stop)))
            {
                return ($"{job.Key} is a service: its schedule needs a StopSchedule, a Quartz cron expression for when it stops (\"{row.StopSchedule}\" is not one).", null);
            }

            if (job.Kind == JobKind.Task && row.StopSchedule is not null)
            {
                return ($"{job.Key} is a task: it stops by itself, so it takes no StopSchedule.", null);
            }

            try
            {
                return (null, TimeZoneInfo.FindSystemTimeZoneById(row.TimeZone ?? string.Empty));
            }
            catch (TimeZoneNotFoundException)
            {
                return ($"TimeZone \"{row.TimeZone}\" is not a time zone (use an IANA name, such as America/New_York).", null);
            }
        }

        private ICalendar? CalendarFor(JobDescriptor job) => job.MarketDaysOnly && this.options.MarketHolidays ? MarketHolidays() : null;

        private async Task<DateTimeOffset> ScheduleAsync(IScheduler quartz, Guid tenantId, Guid id, JobDescriptor job, string action, string cron, TimeZoneInfo zone, JobKey key, CancellationToken cancellationToken)
        {
            var detail = JobBuilder.Create<ScheduleFire>()
                .WithIdentity(key)
                .UsingJobData(ScheduleFire.TenantIdKey, tenantId.ToString())
                .UsingJobData(ScheduleFire.JobScheduleIdKey, id.ToString())
                .UsingJobData(ScheduleFire.JobKey, job.Key)
                .UsingJobData(ScheduleFire.ActionKey, action)
                .Build();
            var trigger = TriggerBuilder.Create()
                .WithIdentity(TriggerOf(key))
                .ForJob(key)
                .WithSchedule(CronScheduleBuilder.CronSchedule(cron).InTimeZone(zone).WithMisfireHandlingInstructionDoNothing());
            if (job.MarketDaysOnly && this.options.MarketHolidays)
            {
                trigger = trigger.ModifiedByCalendar(MarketCalendarName);
            }

            return await quartz.ScheduleJob(detail, trigger.Build(), cancellationToken).ConfigureAwait(false);
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Jobs: scheduler started with {Count} job(s): {Jobs}.")]
        private partial void LogStarted(int count, string jobs);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Jobs: the market holidays run only through {LastYear}; add the next years to MarketCalendar.")]
        private partial void LogCalendarEnding(int lastYear);

        [LoggerMessage(Level = LogLevel.Information, Message = "Jobs, tenant {TenantId}: {Job} scheduled ({Schedule}, {TimeZone}); next {NextRun}.")]
        private partial void LogScheduled(Guid tenantId, string job, string schedule, string timeZone, DateTimeOffset nextRun);

        [LoggerMessage(Level = LogLevel.Information, Message = "Jobs, tenant {TenantId}: {Job} turned on (schedule {JobScheduleId}).")]
        private partial void LogOn(Guid tenantId, string job, Guid jobScheduleId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Jobs, tenant {TenantId}: {Job} turned off (schedule {JobScheduleId}).")]
        private partial void LogOff(Guid tenantId, string job, Guid jobScheduleId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Jobs, tenant {TenantId}: schedule {JobScheduleId} ({Job}) can't be acted on: {Problem}")]
        private partial void LogProblem(Guid tenantId, Guid jobScheduleId, string job, string problem);

        [LoggerMessage(Level = LogLevel.Information, Message = "Jobs, tenant {TenantId}: schedule {JobScheduleId} removed.")]
        private partial void LogRemoved(Guid tenantId, Guid jobScheduleId);

        [LoggerMessage(Level = LogLevel.Error, Message = "Jobs, tenant {TenantId}: the scheduler stopped watching this tenant's schedules.")]
        private partial void LogWatcherFailed(Exception exception, Guid tenantId);

        private sealed record Row(Guid JobScheduleId, string Job, JobStatus Status, string? CronSchedule, string? StopSchedule, string? TimeZone, string? Description);
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
    /// The one Quartz job: a schedule's time has come.  It hands the work to the <see cref="JobRunner"/> and returns at
    /// once, so a long run never holds one of Quartz's threads.
    /// </summary>
    public sealed class ScheduleFire : IJob
    {
        /// <summary>The job data key of the tenant.</summary>
        public const string TenantIdKey = "TenantId";

        /// <summary>The job data key of the schedule.</summary>
        public const string JobScheduleIdKey = "JobScheduleId";

        /// <summary>The job data key of the job's key.</summary>
        public const string JobKey = "Job";

        /// <summary>The job data key of what to do.</summary>
        public const string ActionKey = "Action";

        /// <summary>Run a task.</summary>
        public const string Run = "Run";

        /// <summary>Start a service.</summary>
        public const string Start = "Start";

        /// <summary>Stop a service.</summary>
        public const string Stop = "Stop";

        private readonly JobRegistry registry;

        private readonly JobRunner runner;

        /// <summary>
        /// Initializes a new instance of the <see cref="ScheduleFire"/> class.
        /// </summary>
        /// <param name="registry">The jobs.</param>
        /// <param name="runner">Runs them.</param>
        public ScheduleFire(JobRegistry registry, JobRunner runner)
        {
            this.registry = registry;
            this.runner = runner;
        }

        /// <inheritdoc/>
        public Task Execute(IJobExecutionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var data = context.MergedJobDataMap;
            var tenantId = Guid.Parse(data.GetString(TenantIdKey)!);
            var scheduleId = Guid.Parse(data.GetString(JobScheduleIdKey)!);
            var key = data.GetString(JobKey)!;
            if (this.registry.Find(key) is { } job)
            {
                switch (data.GetString(ActionKey))
                {
                    case Run:
                        this.runner.StartTask(tenantId, job, scheduleId);
                        break;
                    case Start:
                        this.runner.StartService(tenantId, job, scheduleId, JobRunReason.Window);
                        break;
                    case Stop:
                        this.runner.Stop(tenantId, key, scheduleId);
                        break;
                }
            }

            return Task.CompletedTask;
        }
    }
}
