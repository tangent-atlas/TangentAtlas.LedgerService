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
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// What a job is doing for a tenant: whether it is running, and what its last run did.  In memory only.
    /// </summary>
    /// <param name="Job">The job's key.</param>
    /// <param name="IsRunning">Whether it is running now.</param>
    /// <param name="RunningSince">When the run in progress started.</param>
    /// <param name="StartedBy">The schedule that started it, or null if it was run by hand.</param>
    /// <param name="LastRun">When its last run (or a service's last stop) ended, since the server started.</param>
    /// <param name="LastOutcome">What that run did.</param>
    public sealed record JobActivity(string Job, bool IsRunning, DateTimeOffset? RunningSince, Guid? StartedBy, DateTimeOffset? LastRun, string? LastOutcome);

    /// <summary>
    /// The answer to a request to run a task by hand.
    /// </summary>
    public enum RunRequest
    {
        /// <summary>It ran: see the outcome.</summary>
        Ran,

        /// <summary>No job has the key.</summary>
        UnknownJob,

        /// <summary>The job is a service: services are turned on, not run.</summary>
        NotATask,

        /// <summary>It is already running for the tenant.</summary>
        AlreadyRunning,
    }

    /// <summary>
    /// Runs jobs: tasks to completion, services until stopped.  At most one run of a job per tenant at a time, however it
    /// was started (a schedule, a window, or by hand).  Each run has its own cancellation, so turning off the schedule that
    /// started it, or the server stopping, cancels it; a service that ends or fails while it should be on is started again
    /// after <see cref="JobOptions.RestartDelay"/>.
    /// </summary>
    public sealed partial class JobRunner : IAsyncDisposable
    {
        private readonly IServiceProvider services;

        private readonly JobRegistry registry;

        private readonly JobOptions options;

        private readonly TimeProvider timeProvider;

        private readonly ILogger<JobRunner> logger;

        private readonly CancellationTokenSource stopping = new CancellationTokenSource();

        private readonly Lock gate = new Lock();

        private int disposed;

        private readonly Dictionary<(Guid TenantId, string Job), Run> running = new Dictionary<(Guid TenantId, string Job), Run>();

        private readonly Dictionary<(Guid TenantId, string Job), (DateTimeOffset Time, string Outcome)> last = new Dictionary<(Guid TenantId, string Job), (DateTimeOffset Time, string Outcome)>();

        /// <summary>
        /// Initializes a new instance of the <see cref="JobRunner"/> class.
        /// </summary>
        /// <param name="services">The services, to make each run's job.</param>
        /// <param name="registry">The jobs.</param>
        /// <param name="options">The settings.</param>
        /// <param name="timeProvider">The clock.</param>
        /// <param name="logger">The log.</param>
        public JobRunner(IServiceProvider services, JobRegistry registry, IOptions<JobOptions> options, TimeProvider timeProvider, ILogger<JobRunner> logger)
        {
            ArgumentNullException.ThrowIfNull(options);
            this.services = services;
            this.registry = registry;
            this.options = options.Value;
            this.timeProvider = timeProvider;
            this.logger = logger;
        }

        /// <summary>
        /// Gets what each of a tenant's jobs is doing: every job the server has, run or not.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <returns>The jobs' activity, by key.</returns>
        public IReadOnlyList<JobActivity> ActivityOf(Guid tenantId)
        {
            lock (this.gate)
            {
                return this.registry.All.Select(job => job.Key).Order(StringComparer.Ordinal).Select(key =>
                {
                    var isRunning = this.running.TryGetValue((tenantId, key), out var run);
                    var hasLast = this.last.TryGetValue((tenantId, key), out var ended);
                    return new JobActivity(key, isRunning, run?.Since, run?.StartedBy, hasLast ? ended.Time : null, hasLast ? ended.Outcome : null);
                }).ToArray();
            }
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
        /// Runs a task by hand and waits for it.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="job">The task's key.</param>
        /// <returns>Whether it ran, and its outcome if it did.</returns>
        public async Task<(RunRequest Result, string? Outcome)> RunNowAsync(Guid tenantId, string job)
        {
            switch (this.registry.Find(job))
            {
                case null:
                    return (RunRequest.UnknownJob, null);
                case { Kind: JobKind.Service }:
                    return (RunRequest.NotATask, null);
                case { } task:
                    return this.TryStart(tenantId, task, startedBy: null) is { } run
                        ? (RunRequest.Ran, await run.Completion.ConfigureAwait(false))
                        : (RunRequest.AlreadyRunning, null);
            }
        }

        /// <summary>
        /// Starts a scheduled run of a task, without waiting for it.  If the task is still running, this run is skipped.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="job">The task.</param>
        /// <param name="scheduleId">The schedule that fired.</param>
        public void StartTask(Guid tenantId, JobDescriptor job, Guid scheduleId)
        {
            ArgumentNullException.ThrowIfNull(job);
            if (this.TryStart(tenantId, job, scheduleId) is null)
            {
                this.LogSkipped(tenantId, job.Key);
            }
        }

        /// <summary>
        /// Starts a service, if it isn't running already.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="job">The service.</param>
        /// <param name="scheduleId">The schedule turning it on.</param>
        public void StartService(Guid tenantId, JobDescriptor job, Guid scheduleId)
        {
            ArgumentNullException.ThrowIfNull(job);
            this.TryStart(tenantId, job, scheduleId);
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
                runs = this.running.Values.Select(run => (Task)run.Completion).ToArray();
            }

            await Task.WhenAll(runs).ConfigureAwait(false);
            this.stopping.Dispose();
        }

        private Run? TryStart(Guid tenantId, JobDescriptor job, Guid? startedBy)
        {
            lock (this.gate)
            {
                if (this.stopping.IsCancellationRequested || this.running.ContainsKey((tenantId, job.Key)))
                {
                    return null;
                }

                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(this.stopping.Token);
                var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var run = new Run(startedBy, this.timeProvider.GetUtcNow(), cancellation, completion.Task);
                this.running[(tenantId, job.Key)] = run;
                _ = Task.Run(async () =>
                {
                    var outcome = job.Kind == JobKind.Task
                        ? await this.RunTaskAsync(tenantId, job, cancellation.Token).ConfigureAwait(false)
                        : await this.RunServiceAsync(tenantId, job, cancellation.Token).ConfigureAwait(false);
                    lock (this.gate)
                    {
                        this.running.Remove((tenantId, job.Key));
                        this.last[(tenantId, job.Key)] = (this.timeProvider.GetUtcNow(), outcome);
                    }

                    cancellation.Dispose();
                    completion.SetResult(outcome);
                });
                return run;
            }
        }

        private async Task<string> RunTaskAsync(Guid tenantId, JobDescriptor job, CancellationToken cancellationToken)
        {
            try
            {
                var task = (ITenantJob)ActivatorUtilities.CreateInstance(this.services, job.Type);
                var outcome = await task.RunAsync(tenantId, cancellationToken).ConfigureAwait(false);
                this.LogRan(tenantId, job.Key, outcome);
                return outcome;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                this.LogCancelled(tenantId, job.Key);
                return "Cancelled.";
            }
            catch (Exception exception)
            {
                this.LogFailed(exception, tenantId, job.Key);
                return $"Failed: {exception.Message}";
            }
        }

        private async Task<string> RunServiceAsync(Guid tenantId, JobDescriptor job, CancellationToken cancellationToken)
        {
            this.LogServiceStarted(tenantId, job.Key);
            while (true)
            {
                string? failure;
                try
                {
                    var service = (ITenantService)ActivatorUtilities.CreateInstance(this.services, job.Type);
                    await service.RunAsync(tenantId, cancellationToken).ConfigureAwait(false);
                    failure = cancellationToken.IsCancellationRequested ? null : "it ended on its own";
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
                    this.LogServiceStopped(tenantId, job.Key);
                    return "Stopped.";
                }

                // Still wanted: start it again after a pause, unless it is stopped meanwhile.
                lock (this.gate)
                {
                    this.last[(tenantId, job.Key)] = (this.timeProvider.GetUtcNow(), $"Restarting: {failure}");
                }

                try
                {
                    await Task.Delay(this.options.RestartDelay, this.timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    this.LogServiceStopped(tenantId, job.Key);
                    return $"Stopped (it had failed: {failure}).";
                }
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

        private sealed record Run(Guid? StartedBy, DateTimeOffset Since, CancellationTokenSource Cancellation, Task<string> Completion);
    }
}
