// <copyright file="JobRunner.cs" company="Tangent Atlas">
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
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Runs jobs, and records every run in the tenant's JobRun log: tasks to completion, services until stopped.  At most
    /// one run of a job per tenant at a time, however it was started (a schedule, a window, or by hand).  Each run has its
    /// own cancellation, so turning off the schedule that started it, or the server stopping, cancels it; a service that
    /// ends or fails while it should be on is started again after <see cref="JobOptions.RestartDelay"/>, as a new run.
    /// </summary>
    /// <remarks>
    /// A run's own work (a task's transactions) and the row recording how it ended are separate commits.  If the server
    /// stops between them, the work stands and the run is marked Interrupted at the next start: what it did is not
    /// confirmed.  A known gap, left for later.
    /// </remarks>
    public sealed partial class JobRunner : IAsyncDisposable
    {
        private const int OutcomeLength = 512;

        private readonly IServiceProvider services;

        private readonly Ledger ledger;

        private readonly JobOptions options;

        private readonly TimeProvider timeProvider;

        private readonly ILogger<JobRunner> logger;

        private readonly CancellationTokenSource stopping = new CancellationTokenSource();

        private readonly Lock gate = new Lock();

        private readonly Dictionary<(Guid TenantId, string Job), Run> running = new Dictionary<(Guid TenantId, string Job), Run>();

        private int disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="JobRunner"/> class.
        /// </summary>
        /// <param name="services">The services, to make each run's job.</param>
        /// <param name="ledger">The ledger, where runs are recorded.</param>
        /// <param name="options">The settings.</param>
        /// <param name="timeProvider">The clock.</param>
        /// <param name="logger">The log.</param>
        public JobRunner(IServiceProvider services, Ledger ledger, IOptions<JobOptions> options, TimeProvider timeProvider, ILogger<JobRunner> logger)
        {
            ArgumentNullException.ThrowIfNull(options);
            this.services = services;
            this.ledger = ledger;
            this.options = options.Value;
            this.timeProvider = timeProvider;
            this.logger = logger;
        }

        /// <summary>
        /// Gets a value indicating whether a job is running for a tenant.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="job">The job's key.</param>
        /// <returns>True if it is running.</returns>
        public bool IsRunning(Guid tenantId, string job)
        {
            lock (this.gate)
            {
                return this.running.ContainsKey((tenantId, job));
            }
        }

        /// <summary>
        /// Takes up a run asked for by hand: starts it, or records why not.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="job">The task.</param>
        /// <param name="jobRunId">The Requested row.</param>
        public void StartRequested(Guid tenantId, JobDescriptor job, Guid jobRunId)
        {
            ArgumentNullException.ThrowIfNull(job);
            if (!this.TryStart(tenantId, job, null, JobRunReason.Hand, jobRunId))
            {
                this.Refuse(tenantId, jobRunId, $"{job.Key} is already running.");
            }
        }

        /// <summary>
        /// Starts a scheduled run of a task, without waiting for it.  If the task is still running, the run is recorded as
        /// refused.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="job">The task.</param>
        /// <param name="scheduleId">The schedule that fired.</param>
        public void StartTask(Guid tenantId, JobDescriptor job, Guid scheduleId)
        {
            ArgumentNullException.ThrowIfNull(job);
            if (!this.TryStart(tenantId, job, scheduleId, JobRunReason.Schedule, null))
            {
                var now = this.timeProvider.GetUtcNow();
                this.Insert(tenantId, new JobRun
                {
                    JobRunId = Guid.CreateVersion7(now),
                    Job = job.Key,
                    JobScheduleId = scheduleId,
                    Reason = JobRunReason.Schedule,
                    Status = JobRunStatus.Refused,
                    RequestedTime = now,
                    EndTime = now,
                    Outcome = "Still running from before.",
                });
                this.LogSkipped(tenantId, job.Key);
            }
        }

        /// <summary>
        /// Starts a service, if it isn't running already.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="job">The service.</param>
        /// <param name="scheduleId">The schedule turning it on.</param>
        /// <param name="reason">Its row turned it on (Schedule), or its window opened (Window).</param>
        public void StartService(Guid tenantId, JobDescriptor job, Guid scheduleId, JobRunReason reason)
        {
            ArgumentNullException.ThrowIfNull(job);
            this.TryStart(tenantId, job, scheduleId, reason, null);
        }

        /// <summary>
        /// Refuses a run asked for by hand, recording why.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="jobRunId">The Requested row.</param>
        /// <param name="why">Why it is refused.</param>
        public void Refuse(Guid tenantId, Guid jobRunId, string why)
        {
            var now = this.timeProvider.GetUtcNow();
            this.Change(tenantId, jobRunId, row =>
            {
                row.Status = JobRunStatus.Refused;
                row.EndTime = now;
                row.Outcome = Shorten(why);
            });
        }

        /// <summary>
        /// Marks a run that was Requested or Running when the server started as Interrupted.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="jobRunId">The run.</param>
        public void Interrupt(Guid tenantId, Guid jobRunId)
        {
            var now = this.timeProvider.GetUtcNow();
            this.Change(tenantId, jobRunId, row =>
            {
                row.Outcome = row.Status == JobRunStatus.Requested ? "The server stopped before it ran." : "The server stopped while it ran: what it did is not confirmed.";
                row.Status = JobRunStatus.Interrupted;
                row.EndTime = now;
            });
        }

        /// <summary>
        /// Stops what a schedule started, if it is running: cancels a task's run or stops a service.  What was started by
        /// hand or by another schedule is left alone.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="job">The job's key.</param>
        /// <param name="scheduleId">The schedule.</param>
        public void Stop(Guid tenantId, string job, Guid scheduleId)
        {
            lock (this.gate)
            {
                if (this.running.TryGetValue((tenantId, job), out var run) && run.StartedBy == scheduleId)
                {
                    run.Cancellation.Cancel();
                }
            }
        }

        /// <summary>
        /// Stops everything running for a tenant (its partition faulted).
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        public void StopTenant(Guid tenantId)
        {
            lock (this.gate)
            {
                foreach (var run in this.running.Where(item => item.Key.TenantId == tenantId).Select(item => item.Value))
                {
                    run.Cancellation.Cancel();
                }
            }
        }

        /// <summary>
        /// Stops everything and waits for the runs to end (the server is stopping).
        /// </summary>
        /// <returns>A task that completes when every run has ended.</returns>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 1)
            {
                return;
            }

            Task[] runs;
            lock (this.gate)
            {
                this.stopping.Cancel();
                runs = this.running.Values.Select(run => run.Completion).ToArray();
            }

            await Task.WhenAll(runs).ConfigureAwait(false);
            this.stopping.Dispose();
        }

        private static string Shorten(string text) => text.Length <= OutcomeLength ? text : string.Concat(text.AsSpan(0, OutcomeLength - 1), "…");

        private bool TryStart(Guid tenantId, JobDescriptor job, Guid? startedBy, JobRunReason reason, Guid? requestId)
        {
            lock (this.gate)
            {
                if (this.stopping.IsCancellationRequested || this.running.ContainsKey((tenantId, job.Key)))
                {
                    return false;
                }

                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(this.stopping.Token);
                var run = new Run(startedBy, cancellation, Task.Run(() => this.RunAsync(tenantId, job, startedBy, reason, requestId, cancellation)));
                this.running[(tenantId, job.Key)] = run;
                return true;
            }
        }

        private async Task RunAsync(Guid tenantId, JobDescriptor job, Guid? startedBy, JobRunReason reason, Guid? requestId, CancellationTokenSource cancellation)
        {
            try
            {
                if (job.Kind == JobKind.Task)
                {
                    var id = this.Started(tenantId, job, startedBy, reason, requestId);
                    var (status, outcome) = await this.RunTaskAsync(tenantId, job, cancellation.Token).ConfigureAwait(false);
                    this.Ended(tenantId, id, status, outcome);
                }
                else
                {
                    await this.RunServiceAsync(tenantId, job, startedBy, reason, cancellation.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                lock (this.gate)
                {
                    this.running.Remove((tenantId, job.Key));
                }

                cancellation.Dispose();
            }
        }

        private async Task<(JobRunStatus Status, string Outcome)> RunTaskAsync(Guid tenantId, JobDescriptor job, CancellationToken cancellationToken)
        {
            try
            {
                var task = (ITenantJob)ActivatorUtilities.CreateInstance(this.services, job.Type);
                var outcome = await task.RunAsync(tenantId, cancellationToken).ConfigureAwait(false);
                this.LogRan(tenantId, job.Key, outcome);
                return (JobRunStatus.Succeeded, outcome);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                this.LogCancelled(tenantId, job.Key);
                return (JobRunStatus.Cancelled, "Cancelled.");
            }
            catch (Exception exception)
            {
                this.LogFailed(exception, tenantId, job.Key);
                return (JobRunStatus.Failed, $"Failed: {exception.Message}");
            }
        }

        /// <summary>
        /// Runs a service until it is stopped; each start is its own run, so a failure and the restart after it are both in
        /// the log.
        /// </summary>
        private async Task RunServiceAsync(Guid tenantId, JobDescriptor job, Guid? startedBy, JobRunReason reason, CancellationToken cancellationToken)
        {
            while (true)
            {
                var id = this.Started(tenantId, job, startedBy, reason, null);
                this.LogServiceStarted(tenantId, job.Key);
                string? failure;
                try
                {
                    var service = (ITenantService)ActivatorUtilities.CreateInstance(this.services, job.Type);
                    await service.RunAsync(tenantId, cancellationToken).ConfigureAwait(false);
                    failure = cancellationToken.IsCancellationRequested ? null : "It ended on its own.";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    failure = null;
                }
                catch (Exception exception)
                {
                    failure = exception.Message;
                    this.LogServiceFailed(exception, tenantId, job.Key);
                }

                if (failure is null)
                {
                    this.Ended(tenantId, id, JobRunStatus.Stopped, "Stopped.");
                    this.LogServiceStopped(tenantId, job.Key);
                    return;
                }

                // Still wanted: start it again after a pause, unless it is stopped meanwhile.
                this.Ended(tenantId, id, JobRunStatus.Failed, $"Failed: {failure}");
                try
                {
                    await Task.Delay(this.options.RestartDelay, this.timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    this.LogServiceStopped(tenantId, job.Key);
                    return;
                }

                reason = JobRunReason.Restart;
            }
        }

        /// <summary>
        /// Records a run's start: a Requested row becomes Running, or a new row is written.
        /// </summary>
        private Guid Started(Guid tenantId, JobDescriptor job, Guid? startedBy, JobRunReason reason, Guid? requestId)
        {
            var now = this.timeProvider.GetUtcNow();
            if (requestId is { } id)
            {
                this.Change(tenantId, id, row =>
                {
                    row.Status = JobRunStatus.Running;
                    row.StartTime = now;
                });
                return id;
            }

            var runId = Guid.CreateVersion7(now);
            this.Insert(tenantId, new JobRun
            {
                JobRunId = runId,
                Job = job.Key,
                JobScheduleId = startedBy,
                Reason = reason,
                Status = JobRunStatus.Running,
                RequestedTime = now,
                StartTime = now,
            });
            return runId;
        }

        private void Ended(Guid tenantId, Guid jobRunId, JobRunStatus status, string outcome)
        {
            var now = this.timeProvider.GetUtcNow();
            this.Change(tenantId, jobRunId, row =>
            {
                row.Status = status;
                row.EndTime = now;
                row.Outcome = Shorten(outcome);
            });
        }

        // A record that can't be written (the tenant faulted) is logged and dropped: it must not stop the job.
        private void Insert(Guid tenantId, JobRun row)
        {
            try
            {
                if (this.ledger.FindPartition(tenantId) is { IsFaulted: false } partition)
                {
                    using var transaction = partition.BeginTransaction();
                    transaction.InsertJobRun(row);
                    transaction.Commit();
                }
            }
            catch (Exception exception)
            {
                this.LogNotRecorded(exception, tenantId);
            }
        }

        private void Change(Guid tenantId, Guid jobRunId, Action<JobRun> change)
        {
            try
            {
                if (this.ledger.FindPartition(tenantId) is { IsFaulted: false } partition)
                {
                    using var transaction = partition.BeginTransaction();
                    if (partition.FindJobRun(jobRunId) is { } current)
                    {
                        var changes = JobRun.FromDto(current.ToDto());   // a new row at the stored version
                        change(changes);
                        transaction.UpdateJobRun(changes);
                        transaction.Commit();
                    }
                }
            }
            catch (Exception exception)
            {
                this.LogNotRecorded(exception, tenantId);
            }
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Jobs, tenant {TenantId}: {Job} ran: {Outcome}")]
        private partial void LogRan(Guid tenantId, string job, string outcome);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Jobs, tenant {TenantId}: {Job} cancelled.")]
        private partial void LogCancelled(Guid tenantId, string job);

        [LoggerMessage(Level = LogLevel.Error, Message = "Jobs, tenant {TenantId}: {Job} failed.")]
        private partial void LogFailed(Exception exception, Guid tenantId, string job);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Jobs, tenant {TenantId}: {Job} skipped: it is still running.")]
        private partial void LogSkipped(Guid tenantId, string job);

        [LoggerMessage(Level = LogLevel.Information, Message = "Jobs, tenant {TenantId}: service {Job} started.")]
        private partial void LogServiceStarted(Guid tenantId, string job);

        [LoggerMessage(Level = LogLevel.Information, Message = "Jobs, tenant {TenantId}: service {Job} stopped.")]
        private partial void LogServiceStopped(Guid tenantId, string job);

        [LoggerMessage(Level = LogLevel.Error, Message = "Jobs, tenant {TenantId}: service {Job} failed; it will be started again.")]
        private partial void LogServiceFailed(Exception exception, Guid tenantId, string job);

        [LoggerMessage(Level = LogLevel.Error, Message = "Jobs, tenant {TenantId}: a run could not be recorded.")]
        private partial void LogNotRecorded(Exception exception, Guid tenantId);

        private sealed record Run(Guid? StartedBy, CancellationTokenSource Cancellation, Task Completion);
    }
}
