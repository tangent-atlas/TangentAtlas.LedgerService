// <copyright file="JobTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias client;
extern alias server;

namespace AtlasLedger.Tests
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using client::AtlasLedger.Client.Api;
    using client::AtlasLedger.Client.Scripts;
    using client::AtlasLedger.Client.TestData;
    using client::AtlasLedger.Model;
    using Microsoft.Extensions.DependencyInjection;
    using Quartz;
    using server::AtlasLedger.Server;
    using server::AtlasLedger.Server.Jobs;
    using server::AtlasLedger.Server.Persistence;
    using Xunit;
    using Commands = client::AtlasLedger.Client.Commands;

    /// <summary>
    /// Jobs: tasks and services, found by their keys; each tenant's schedules turn them off, on, or on a schedule, and act
    /// on each commit; tasks can be run by hand.
    /// </summary>
    public sealed class JobTests
    {
        private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        private static readonly string[] Quiet = ["--Jobs:MarketHolidays=false", "--Jobs:RestartDelay=00:00:00.100"];

        [Fact]
        public void JobsAreFoundByTheirKeysAndKnowTheirKind()
        {
            var registry = JobRegistry.Discover(typeof(AtlasLedgerServer).Assembly);
            var generate = registry.Find("GenerateOrders");
            Assert.NotNull(generate);
            Assert.Equal((typeof(GenerateOrdersJob), JobKind.Task, true), (generate.Type, generate.Kind, generate.MarketDaysOnly));
            Assert.Null(registry.Find("generateorders"));   // keys are exact
            Assert.Equal(JobKind.Service, new JobDescriptor("Echo", typeof(EchoService), marketDaysOnly: false).Kind);

            // Two jobs with one key, or a class that is neither a task nor a service (or both): the server doesn't start.
            Assert.Throws<InvalidOperationException>(() => new JobRegistry([generate, new JobDescriptor("GenerateOrders", typeof(SlowTask), marketDaysOnly: false)]));
            Assert.Throws<InvalidOperationException>(() => new JobDescriptor("Nothing", typeof(object), marketDaysOnly: false));
            Assert.Throws<InvalidOperationException>(() => new JobDescriptor("Both", typeof(BothJob), marketDaysOnly: false));
        }

        [Fact]
        public void AMarketDaysJobSkipsWeekendsAndHolidays()
        {
            // 3:45 pm in New York on weekdays, from Wednesday, November 25, 2026, at 4 pm: Thanksgiving (Thursday) is a
            // holiday, so the next run is Friday; after Friday's, Monday.
            var calendar = JobScheduler.MarketHolidays();
            Assert.Equal(new DateTimeOffset(2026, 11, 27, 15, 45, 0, TimeSpan.FromHours(-5)), Next("0 45 15 ? * MON-FRI", new DateTimeOffset(2026, 11, 25, 21, 0, 0, TimeSpan.Zero), calendar));
            Assert.Equal(new DateTimeOffset(2026, 11, 30, 15, 45, 0, TimeSpan.FromHours(-5)), Next("0 45 15 ? * MON-FRI", new DateTimeOffset(2026, 11, 27, 21, 0, 0, TimeSpan.Zero), calendar));

            // Without the calendar, Thanksgiving is just a Thursday.
            Assert.Equal(new DateTimeOffset(2026, 11, 26, 15, 45, 0, TimeSpan.FromHours(-5)), Next("0 45 15 ? * MON-FRI", new DateTimeOffset(2026, 11, 25, 21, 0, 0, TimeSpan.Zero), null));

            // Across the change to daylight time, still 3:45 pm in New York: Monday, March 9, 2026 is 19:45 UTC.
            Assert.Equal(new DateTimeOffset(2026, 3, 9, 19, 45, 0, TimeSpan.Zero), Next("0 45 15 ? * MON-FRI", new DateTimeOffset(2026, 3, 6, 21, 0, 0, TimeSpan.Zero), calendar).ToUniversalTime());
        }

        [Fact]
        public void AServiceIsOnInsideItsWindow()
        {
            // On from 9:25 am to 4:05 pm in New York on market days.
            var calendar = JobScheduler.MarketHolidays();
            bool At(int month, int day, int hour, int minute) =>
                JobScheduler.IsInsideWindow("0 25 9 ? * MON-FRI", "0 5 16 ? * MON-FRI", NewYork, calendar, new DateTimeOffset(2026, month, day, hour, minute, 0, NewYork.GetUtcOffset(new DateTime(2026, month, day))));

            Assert.True(At(11, 25, 10, 0));    // Wednesday morning
            Assert.False(At(11, 25, 9, 0));    // before the start
            Assert.False(At(11, 25, 17, 0));   // after the stop
            Assert.False(At(11, 26, 10, 0));   // Thanksgiving
            Assert.True(At(11, 27, 15, 0));    // the Friday after
            Assert.False(At(11, 28, 10, 0));   // Saturday
        }

        [Fact]
        public async Task AScheduleGeneratesOrdersWithoutBeingAsked()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var server = ServerFixture.With(
            [
                .. Quiet,
                "--Services:StartOn=rules,market-feed,broker,trading",
                "--MarketFeed:TickInterval=00:00:05",   // prices still between ticks, so a move can't call for a top-up mid-test
                "--MarketFeed:BadPrintInterval=1.00:00:00",
                "--Broker:PollInterval=00:00:00.100",
            ]);
            await server.InitializeAsync();
            var tool = new LedgerApiClient(server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            var state = OrderRoutingTests.ReadState();
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
            await tool.CommitTransactionsAsync(tenantId, Joe10Account.Opening(state, 1_000_000m), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Weights(state), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Jobs(), cancellationToken);

            // Every second, and three schedules that can't be acted on: a job no one has, a zone that doesn't exist, and a
            // task turned on.
            var everySecond = Guid.NewGuid();
            await tool.ApplyAsync(
                tenantId,
                new LedgerScript()
                    .Insert("JobSchedule", Schedule(everySecond, "GenerateOrders", JobStatus.Scheduled, "* * * * * ?"))
                    .Insert("JobSchedule", Schedule(Guid.NewGuid(), "SendNewsletter", JobStatus.Scheduled, "0 0 9 * * ?"))
                    .Insert("JobSchedule", Schedule(Guid.NewGuid(), "GenerateOrders", JobStatus.Scheduled, "0 0 9 * * ?", zone: "Atlantis/Capital"))
                    .Insert("JobSchedule", Schedule(Guid.NewGuid(), "GenerateOrders", JobStatus.On)),
                cancellationToken);

            // The job orders what the model proposes once prices arrive; the broker fills it; later runs find nothing to do.
            var orders = await OrderRoutingTests.WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Orders.Count == 2 && found.Orders.All(order => order.Status == "Filled"));
            Assert.All(orders.Orders, order => Assert.Equal(OrderOrigin.Rebalance, order.Origin));
            var jobs = await OrderRoutingTests.WaitForAsync(
                () => tool.GetJobsAsync(tenantId, cancellationToken),
                found => found.Jobs.Single(job => job.Job == "GenerateOrders").Last?.Outcome?.StartsWith("0 order(s)", StringComparison.Ordinal) == true);
            Assert.Contains(jobs.Runs, run => run is { Job: "GenerateOrders", Reason: JobRunReason.Schedule, Status: JobRunStatus.Succeeded } && run.Outcome!.StartsWith("2 order(s)", StringComparison.Ordinal));
            Assert.Equal(2, await OrderRoutingTests.OrderCountAsync(tool, tenantId, cancellationToken));
            var joe10 = jobs.Schedules.Single(schedule => schedule.JobScheduleId == Joe10Account.GenerateOrdersScheduleId);
            Assert.Null(joe10.Problem);
            Assert.NotNull(joe10.NextStart);
            Assert.StartsWith("No job has the key", jobs.Schedules.Single(schedule => schedule.Job == "SendNewsletter").Problem, StringComparison.Ordinal);
            Assert.Contains("not a time zone", jobs.Schedules.Single(schedule => schedule.TimeZone == "Atlantis/Capital").Problem, StringComparison.Ordinal);
            Assert.Contains("is a task", jobs.Schedules.Single(schedule => schedule.Status == JobStatus.On).Problem, StringComparison.Ordinal);

            // Deleting the row stops the job.
            var row = (await tool.ListAsync<JobScheduleDto>(tenantId, "JobSchedule", cancellationToken)).Single(schedule => schedule.JobScheduleId == everySecond);
            await tool.ApplyAsync(tenantId, new LedgerScript().Delete("JobSchedule", new { row.JobScheduleId }, row.RowVersion), cancellationToken);
            await OrderRoutingTests.WaitForAsync(() => tool.GetJobsAsync(tenantId, cancellationToken), found => found.Schedules.All(schedule => schedule.JobScheduleId != everySecond));
        }

        [Fact]
        public async Task WithTradingOffAScheduledRunDoesNothingAndATaskCanBeRunByHand()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var server = ServerFixture.With([.. Quiet, "--Services:StartOn=rules,market-feed", "--MarketFeed:TickInterval=00:00:00.100", "--MarketFeed:BadPrintInterval=1.00:00:00"]);
            await server.InitializeAsync();
            var tool = new LedgerApiClient(server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            var state = OrderRoutingTests.ReadState();
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
            await tool.CommitTransactionsAsync(tenantId, Joe10Account.Opening(state, 1_000_000m), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Weights(state), cancellationToken);
            await OrderRoutingTests.WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Proposals.SingleOrDefault() is { IsWaiting: false, Trades.Count: 2 });

            await tool.ApplyAsync(tenantId, new LedgerScript().Insert("JobSchedule", Schedule(Guid.NewGuid(), "GenerateOrders", JobStatus.Scheduled, "* * * * * ?")), cancellationToken);
            await OrderRoutingTests.WaitForAsync(
                () => tool.GetJobsAsync(tenantId, cancellationToken),
                found => found.Jobs.Single(job => job.Job == "GenerateOrders").Last?.Outcome == "Skipped: trading is switched off.");
            Assert.Equal(0, await OrderRoutingTests.OrderCountAsync(tool, tenantId, cancellationToken));

            // By hand: the same task, the same switch, and the run in the log.  A key no job has is refused.
            var byHand = await tool.RunJobAsync(tenantId, "GenerateOrders", TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Equal((JobRunStatus.Succeeded, JobRunReason.Hand, "Skipped: trading is switched off."), (byHand.Status, byHand.Reason, byHand.Outcome));
            Assert.NotNull(byHand.StartTime);
            var unknown = await tool.RunJobAsync(tenantId, "SendNewsletter", TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Equal(JobRunStatus.Refused, unknown.Status);
            Assert.StartsWith("No job has the key", unknown.Outcome, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TurningAScheduleOffOrDeletingItCancelsARunInProgress()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            SlowTask.Reset();
            await using var server = WithTestJobs();
            await server.InitializeAsync();
            var tool = new LedgerApiClient(server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            var id = Guid.NewGuid();
            await tool.ApplyAsync(tenantId, new LedgerScript().Insert("JobSchedule", Schedule(id, "Slow", JobStatus.Scheduled, "* * * * * ?")), cancellationToken);

            // It starts within a second of the commit, and runs until it is stopped; meanwhile it can't be run by hand.
            await SlowTask.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var busy = await tool.RunJobAsync(tenantId, "Slow", TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Equal((JobRunStatus.Refused, "Slow is already running."), (busy.Status, busy.Outcome));

            // Turned off: the run in progress is cancelled at once, and it is not scheduled again.
            await SetAsync(tool, tenantId, "Slow", JobStatus.Off, cancellationToken);
            await SlowTask.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var jobs = await OrderRoutingTests.WaitForAsync(() => tool.GetJobsAsync(tenantId, cancellationToken), found => found.Jobs.Single(job => job.Job == "Slow") is { Running: null, Last.Status: JobRunStatus.Cancelled });
            Assert.Equal((JobStatus.Off, (DateTimeOffset?)null), (jobs.Schedules.Single().Status, jobs.Schedules.Single().NextStart));

            // Off means off: not by hand either.
            var off = await tool.RunJobAsync(tenantId, "Slow", TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Equal((JobRunStatus.Refused, "Slow is off: every schedule of it is Off."), (off.Status, off.Outcome));

            // Scheduled again: it runs again; deleted: that run is cancelled too, and the schedule is gone.
            SlowTask.Reset();
            await SetAsync(tool, tenantId, "Slow", JobStatus.Scheduled, cancellationToken);
            await SlowTask.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var current = (await tool.ListAsync<JobScheduleDto>(tenantId, "JobSchedule", cancellationToken)).Single();
            await tool.ApplyAsync(tenantId, new LedgerScript().Delete("JobSchedule", new { JobScheduleId = id }, current.RowVersion), cancellationToken);
            await SlowTask.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Empty((await tool.GetJobsAsync(tenantId, cancellationToken)).Schedules);
        }

        [Fact]
        public async Task AServiceRunsWhileOnOrInsideItsWindowAndIsRestartedIfItFails()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            EchoService.Reset(failFirst: true);
            await using var server = WithTestJobs();
            await server.InitializeAsync();
            var tool = new LedgerApiClient(server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            await tool.ApplyAsync(tenantId, new LedgerScript().Insert("Node", new NodeDto { NodeId = Guid.NewGuid(), DisplayName = "A", FullName = "A", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account }), cancellationToken);

            // On by hand, with no schedule yet: a row is added, and it runs.  Its first start fails; it is started again.
            await SetAsync(tool, tenantId, "Echo", JobStatus.On, cancellationToken);
            await OrderRoutingTests.WaitForAsync(() => Task.FromResult(EchoService.Starts), starts => starts >= 2);
            var jobs = await tool.GetJobsAsync(tenantId, cancellationToken);
            var echo = jobs.Jobs.Single(job => job.Job == "Echo");
            Assert.Equal(("Service", jobs.Schedules.Single().JobScheduleId), (echo.Kind, echo.Running!.JobScheduleId!.Value));
            Assert.Contains(jobs.Runs, run => run is { Job: "Echo", Status: JobRunStatus.Failed, Reason: JobRunReason.Schedule });   // the first start, in the log
            Assert.Equal(JobRunReason.Restart, echo.Running.Reason);

            // A service isn't run by hand.
            var notATask = await tool.RunJobAsync(tenantId, "Echo", TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Equal(JobRunStatus.Refused, notATask.Status);
            Assert.Contains("is a service", notATask.Outcome, StringComparison.Ordinal);

            // Off: it stops.
            await SetAsync(tool, tenantId, "Echo", JobStatus.Off, cancellationToken);
            await OrderRoutingTests.WaitForAsync(() => tool.GetJobsAsync(tenantId, cancellationToken), found => found.Jobs.Single(job => job.Job == "Echo") is { Running: null, Last.Status: JobRunStatus.Stopped });

            // Scheduled without a stop: refused.  With one, on at even seconds and off at odd ones: it starts and stops.
            var row = (await tool.ListAsync<JobScheduleDto>(tenantId, "JobSchedule", cancellationToken)).Single();
            await tool.ApplyAsync(tenantId, new LedgerScript().Update("JobSchedule", new { row.JobScheduleId }, Schedule(row.JobScheduleId, "Echo", JobStatus.Scheduled, "0/2 * * * * ?", rowVersion: row.RowVersion)), cancellationToken);
            await OrderRoutingTests.WaitForAsync(() => tool.GetJobsAsync(tenantId, cancellationToken), found => found.Schedules.Single().Problem?.Contains("StopSchedule", StringComparison.Ordinal) == true);
            row = (await tool.ListAsync<JobScheduleDto>(tenantId, "JobSchedule", cancellationToken)).Single();
            var (starts, stops) = (EchoService.Starts, EchoService.Stops);
            await tool.ApplyAsync(tenantId, new LedgerScript().Update("JobSchedule", new { row.JobScheduleId }, Schedule(row.JobScheduleId, "Echo", JobStatus.Scheduled, "0/2 * * * * ?", stop: "1/2 * * * * ?", rowVersion: row.RowVersion)), cancellationToken);
            await OrderRoutingTests.WaitForAsync(() => Task.FromResult((EchoService.Starts, EchoService.Stops)), counts => counts.Starts >= starts + 2 && counts.Stops >= stops + 2);
            jobs = await tool.GetJobsAsync(tenantId, cancellationToken);
            Assert.Null(jobs.Schedules.Single().Problem);
            Assert.NotNull(jobs.Schedules.Single().NextStop);
        }

        [Fact]
        public async Task ARunInFlightWhenTheServerStoppedIsMarkedInterrupted()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var database = new InMemoryLedgerDatabase();
            var tenantId = Guid.NewGuid();
            var running = Guid.NewGuid();
            await using (var first = ServerFixture.With(Quiet, services => services.AddSingleton<ILedgerDatabase>(database)))
            {
                await first.InitializeAsync();
                var tool = new LedgerApiClient(first.HttpClient, DevelopmentCallers.Tool);

                // As a run would stand if the server died mid-run.
                await tool.InsertAsync(tenantId, "JobRun", new JobRunDto { JobRunId = running, Job = "GenerateOrders", Reason = JobRunReason.Schedule, Status = JobRunStatus.Running, RequestedTime = DateTimeOffset.UtcNow, StartTime = DateTimeOffset.UtcNow }, cancellationToken);
            }

            await using var second = ServerFixture.With(Quiet, services => services.AddSingleton<ILedgerDatabase>(database));
            await second.InitializeAsync();
            var again = new LedgerApiClient(second.HttpClient, DevelopmentCallers.Tool);
            var run = await OrderRoutingTests.WaitForAsync(() => again.GetAsync<JobRunDto>(tenantId, "JobRun", running.ToString(), cancellationToken), found => found.Status != JobRunStatus.Running);
            Assert.Equal(JobRunStatus.Interrupted, run.Status);
            Assert.Contains("not confirmed", run.Outcome, StringComparison.Ordinal);
        }

        [Fact]
        public void SettingAStatusChangesEveryScheduleOfTheJob()
        {
            var rows = new[]
            {
                Schedule(Guid.NewGuid(), "GenerateOrders", JobStatus.Scheduled, "0 45 15 ? * MON-FRI", rowVersion: 7),
                Schedule(Guid.NewGuid(), "GenerateOrders", JobStatus.Off, "0 0 10 ? * MON-FRI", rowVersion: 8),
                Schedule(Guid.NewGuid(), "Echo", JobStatus.On, rowVersion: 9),
            };

            var (script, _) = Commands.SetJobStatus(rows, "GenerateOrders", JobStatus.Off, Guid.NewGuid);
            Assert.Single(script!.Operations);   // the other is off already
            Assert.Null(Commands.SetJobStatus(rows, "Echo", JobStatus.On, Guid.NewGuid).Script);
            var (added, _) = Commands.SetJobStatus(rows, "Feed", JobStatus.On, Guid.NewGuid);
            Assert.Equal(ScriptAction.Insert, added!.Operations.Single().Action);   // a service turned on with no schedule gets one
            Assert.Null(Commands.SetJobStatus(rows, "Feed", JobStatus.Scheduled, Guid.NewGuid).Script);   // scheduled needs times
        }

        private static ServerFixture WithTestJobs()
        {
            var registry = JobRegistry.Discover(typeof(AtlasLedgerServer).Assembly);
            return ServerFixture.With(
                Quiet,
                services => services.AddSingleton(new JobRegistry([.. registry.All, new JobDescriptor("Slow", typeof(SlowTask), marketDaysOnly: false), new JobDescriptor("Echo", typeof(EchoService), marketDaysOnly: false)])));
        }

        private static async Task SetAsync(LedgerApiClient tool, Guid tenantId, string job, JobStatus status, CancellationToken cancellationToken) =>
            await Commands.SetJobStatusAsync(tool, tenantId, job, status, TextWriter.Null, cancellationToken);

        private static JobScheduleDto Schedule(Guid id, string job, JobStatus status, string? cron = null, string? stop = null, string zone = "Etc/UTC", long rowVersion = 0) =>
            new JobScheduleDto { JobScheduleId = id, Job = job, Status = status, CronSchedule = cron, StopSchedule = stop, TimeZone = zone, RowVersion = rowVersion };

        private static DateTimeOffset Next(string cron, DateTimeOffset after, ICalendar? calendar)
        {
            var next = JobScheduler.NextTime(cron, NewYork, calendar, after);
            Assert.NotNull(next);
            return TimeZoneInfo.ConvertTime(next.Value, NewYork);
        }

        /// <summary>
        /// A task that runs until it is cancelled.
        /// </summary>
        private sealed class SlowTask : ITenantJob
        {
            public static TaskCompletionSource Started { get; private set; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            public static TaskCompletionSource Cancelled { get; private set; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            public static void Reset()
            {
                Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public async Task<string> RunAsync(Guid tenantId, CancellationToken cancellationToken)
            {
                Started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    return "Finished.";
                }
                catch (OperationCanceledException)
                {
                    Cancelled.TrySetResult();
                    throw;
                }
            }
        }

        /// <summary>
        /// A service that counts its starts and stops, and can fail its first start.
        /// </summary>
        private sealed class EchoService : ITenantService
        {
            private static int starts;

            private static int stops;

            private static int failFirst;

            public static int Starts => Volatile.Read(ref starts);

            public static int Stops => Volatile.Read(ref stops);

            public static void Reset(bool failFirst)
            {
                starts = 0;
                stops = 0;
                EchoService.failFirst = failFirst ? 1 : 0;
            }

            public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref starts);
                if (Interlocked.Exchange(ref failFirst, 0) == 1)
                {
                    throw new InvalidOperationException("The first start fails.");
                }

                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                finally
                {
                    Interlocked.Increment(ref stops);
                }
            }
        }

        /// <summary>
        /// Not a valid job: both a task and a service.
        /// </summary>
        private sealed class BothJob : ITenantJob, ITenantService
        {
            Task<string> ITenantJob.RunAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(string.Empty);

            Task ITenantService.RunAsync(Guid tenantId, CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
