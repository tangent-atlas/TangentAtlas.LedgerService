// <copyright file="JobTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias client;
extern alias server;

namespace AtlasLedger.Tests
{
    using System;
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
    using Quartz.Spi;
    using server::AtlasLedger.Server;
    using server::AtlasLedger.Server.Jobs;
    using Xunit;

    /// <summary>
    /// Scheduled jobs: found by their keys, run on their tenants' schedules, skipping the market's holidays.
    /// </summary>
    public sealed class JobTests
    {
        private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        [Fact]
        public void JobsAreFoundByTheirKeys()
        {
            var registry = JobRegistry.Discover(typeof(AtlasLedgerServer).Assembly);
            var generate = registry.Find("GenerateOrders");
            Assert.NotNull(generate);
            Assert.Equal(typeof(GenerateOrdersJob), generate.Type);
            Assert.True(generate.MarketDaysOnly);
            Assert.Null(registry.Find("generateorders"));   // keys are exact

            // Two jobs with one key: the server doesn't start.
            Assert.Throws<InvalidOperationException>(() => new JobRegistry([generate, generate with { Type = typeof(object) }]));
        }

        [Fact]
        public void AMarketDaysJobSkipsWeekendsAndHolidays()
        {
            // 3:45 pm in New York on weekdays, from Wednesday, November 25, 2026, at 4 pm: Thanksgiving (Thursday) is a
            // holiday, so the next run is Friday; after Friday's, Monday.
            var calendar = JobScheduler.MarketHolidays();
            Assert.Equal(new DateTimeOffset(2026, 11, 27, 15, 45, 0, NewYork.GetUtcOffset(new DateTime(2026, 11, 27))), FirstRun(new DateTimeOffset(2026, 11, 25, 21, 0, 0, TimeSpan.Zero), calendar));
            Assert.Equal(new DateTimeOffset(2026, 11, 30, 15, 45, 0, TimeSpan.FromHours(-5)), FirstRun(new DateTimeOffset(2026, 11, 27, 21, 0, 0, TimeSpan.Zero), calendar));

            // Without the calendar, Thanksgiving is just a Thursday.
            Assert.Equal(new DateTimeOffset(2026, 11, 26, 15, 45, 0, TimeSpan.FromHours(-5)), FirstRun(new DateTimeOffset(2026, 11, 25, 21, 0, 0, TimeSpan.Zero), null));

            // Across the change to daylight time, still 3:45 pm in New York: Monday, March 9, 2026 is 19:45 UTC.
            Assert.Equal(new DateTimeOffset(2026, 3, 9, 19, 45, 0, TimeSpan.Zero), FirstRun(new DateTimeOffset(2026, 3, 6, 21, 0, 0, TimeSpan.Zero), calendar).ToUniversalTime());
        }

        [Fact]
        public async Task AScheduleGeneratesOrdersWithoutBeingAsked()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var server = ServerFixture.With(
            [
                "--Services:StartOn=rules,market-feed,broker,trading",
                "--MarketFeed:TickInterval=00:00:00.100",
                "--MarketFeed:BadPrintInterval=1.00:00:00",
                "--Broker:PollInterval=00:00:00.100",
                "--Jobs:MarketHolidays=false",
            ]);
            await server.InitializeAsync();
            var tool = new LedgerApiClient(server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            var state = OrderRoutingTests.ReadState();
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Opening(state, 1_000_000m), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Weights(state), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Jobs(), cancellationToken);

            // Every second, and two schedules that can't be: a job no one has, and a zone that doesn't exist.
            var everySecond = Guid.NewGuid();
            await tool.ApplyAsync(
                tenantId,
                new LedgerScript()
                    .Insert("JobSchedule", new JobScheduleDto { JobScheduleId = everySecond, Job = "GenerateOrders", CronSchedule = "* * * * * ?", TimeZone = "Etc/UTC", IsEnabled = true })
                    .Insert("JobSchedule", new JobScheduleDto { JobScheduleId = Guid.NewGuid(), Job = "SendNewsletter", CronSchedule = "0 0 9 * * ?", TimeZone = "Etc/UTC", IsEnabled = true })
                    .Insert("JobSchedule", new JobScheduleDto { JobScheduleId = Guid.NewGuid(), Job = "GenerateOrders", CronSchedule = "0 0 9 * * ?", TimeZone = "Atlantis/Capital", IsEnabled = true }),
                cancellationToken);

            // The job orders what the model proposes once prices arrive; the broker fills it; later runs find nothing to do.
            var orders = await OrderRoutingTests.WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Orders.Count == 2 && found.Orders.All(order => order.Status == "Filled"));
            Assert.All(orders.Orders, order => Assert.Equal(OrderOrigin.Rebalance, order.Origin));
            var jobs = await OrderRoutingTests.WaitForAsync(
                () => tool.GetJobsAsync(tenantId, cancellationToken),
                found => found.Schedules.SingleOrDefault(schedule => schedule.JobScheduleId == everySecond)?.LastOutcome?.StartsWith("0 order(s)", StringComparison.Ordinal) == true);
            Assert.Contains("GenerateOrders", jobs.Jobs);
            Assert.Equal(2, (await tool.ListAsync<OrderDto>(tenantId, "Order", cancellationToken)).Count);
            Assert.Equal("Scheduled", jobs.Schedules.Single(schedule => schedule.JobScheduleId == Joe10Account.GenerateOrdersScheduleId).Status);
            Assert.Equal("UnknownJob", jobs.Schedules.Single(schedule => schedule.Job == "SendNewsletter").Status);
            Assert.Equal("InvalidSchedule", jobs.Schedules.Single(schedule => schedule.TimeZone == "Atlantis/Capital").Status);

            // Deleting the row stops the job.
            var row = (await tool.ListAsync<JobScheduleDto>(tenantId, "JobSchedule", cancellationToken)).Single(schedule => schedule.JobScheduleId == everySecond);
            await tool.ApplyAsync(tenantId, new LedgerScript().Delete("JobSchedule", new { row.JobScheduleId }, row.RowVersion), cancellationToken);
            await OrderRoutingTests.WaitForAsync(() => tool.GetJobsAsync(tenantId, cancellationToken), found => found.Schedules.All(schedule => schedule.JobScheduleId != everySecond));
        }

        [Fact]
        public async Task WithTradingOffAScheduledRunDoesNothing()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var server = ServerFixture.With(
            [
                "--Services:StartOn=rules,market-feed",
                "--MarketFeed:TickInterval=00:00:00.100",
                "--MarketFeed:BadPrintInterval=1.00:00:00",
                "--Jobs:MarketHolidays=false",
            ]);
            await server.InitializeAsync();
            var tool = new LedgerApiClient(server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            var state = OrderRoutingTests.ReadState();
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Opening(state, 1_000_000m), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Weights(state), cancellationToken);
            await OrderRoutingTests.WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Proposals.SingleOrDefault() is { IsWaiting: false, Trades.Count: 2 });

            var everySecond = Guid.NewGuid();
            await tool.ApplyAsync(tenantId, new LedgerScript().Insert("JobSchedule", new JobScheduleDto { JobScheduleId = everySecond, Job = "GenerateOrders", CronSchedule = "* * * * * ?", TimeZone = "Etc/UTC", IsEnabled = true }), cancellationToken);
            await OrderRoutingTests.WaitForAsync(
                () => tool.GetJobsAsync(tenantId, cancellationToken),
                found => found.Schedules.SingleOrDefault(schedule => schedule.JobScheduleId == everySecond)?.LastOutcome == "Skipped: trading is switched off.");
            Assert.Empty(await tool.ListAsync<OrderDto>(tenantId, "Order", cancellationToken));
        }

        [Fact]
        public async Task TurningAScheduleOffOrDeletingItCancelsARunInProgress()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            SlowJob.Reset();
            var registry = JobRegistry.Discover(typeof(AtlasLedgerServer).Assembly);
            await using var server = ServerFixture.With(
                ["--Jobs:MarketHolidays=false"],
                services => services.AddSingleton(new JobRegistry([.. registry.All, new JobDescriptor("Slow", typeof(SlowJob), MarketDaysOnly: false)])));
            await server.InitializeAsync();
            var tool = new LedgerApiClient(server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            var id = Guid.NewGuid();
            static JobScheduleDto Row(Guid id, bool isEnabled, long rowVersion = 0) =>
                new JobScheduleDto { JobScheduleId = id, Job = "Slow", CronSchedule = "* * * * * ?", TimeZone = "Etc/UTC", IsEnabled = isEnabled, RowVersion = rowVersion };
            await tool.ApplyAsync(tenantId, new LedgerScript().Insert("JobSchedule", Row(id, isEnabled: true)), cancellationToken);

            // It starts within a second of the commit, and runs until it is stopped.
            await SlowJob.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            // Turned off: the run in progress is cancelled at once, and it is not scheduled again.
            var current = (await tool.ListAsync<JobScheduleDto>(tenantId, "JobSchedule", cancellationToken)).Single();
            await tool.ApplyAsync(tenantId, new LedgerScript().Update("JobSchedule", new { JobScheduleId = id }, Row(id, isEnabled: false, current.RowVersion)), cancellationToken);
            await SlowJob.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var jobs = await OrderRoutingTests.WaitForAsync(() => tool.GetJobsAsync(tenantId, cancellationToken), found => found.Schedules.SingleOrDefault() is { Status: "Disabled", LastOutcome: "Cancelled." });
            Assert.Equal(("Disabled", (DateTimeOffset?)null), (jobs.Schedules.Single().Status, jobs.Schedules.Single().NextRun));

            // On again: it runs again; deleted: that run is cancelled too, and the schedule is gone.
            SlowJob.Reset();
            current = (await tool.ListAsync<JobScheduleDto>(tenantId, "JobSchedule", cancellationToken)).Single();
            await tool.ApplyAsync(tenantId, new LedgerScript().Update("JobSchedule", new { JobScheduleId = id }, Row(id, isEnabled: true, current.RowVersion)), cancellationToken);
            await SlowJob.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            current = (await tool.ListAsync<JobScheduleDto>(tenantId, "JobSchedule", cancellationToken)).Single();
            await tool.ApplyAsync(tenantId, new LedgerScript().Delete("JobSchedule", new { JobScheduleId = id }, current.RowVersion), cancellationToken);
            await SlowJob.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Empty((await tool.GetJobsAsync(tenantId, cancellationToken)).Schedules);
        }

        private static DateTimeOffset FirstRun(DateTimeOffset after, ICalendar? calendar)
        {
            var trigger = (IOperableTrigger)TriggerBuilder.Create()
                .StartAt(after)
                .WithSchedule(CronScheduleBuilder.CronSchedule("0 45 15 ? * MON-FRI").InTimeZone(NewYork))
                .Build();
            var first = trigger.ComputeFirstFireTimeUtc(calendar);
            Assert.NotNull(first);
            return TimeZoneInfo.ConvertTime(first.Value, NewYork);
        }

        /// <summary>
        /// A job that runs until it is cancelled.
        /// </summary>
        private sealed class SlowJob : ITenantJob
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
    }
}
