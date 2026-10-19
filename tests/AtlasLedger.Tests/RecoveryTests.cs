// <copyright file="RecoveryTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias client;
extern alias server;

namespace AtlasLedger.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using client::AtlasLedger.Client;
    using client::AtlasLedger.Client.Api;
    using client::AtlasLedger.Client.Scripts;
    using client::AtlasLedger.Model;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.Extensions.DependencyInjection;
    using server::AtlasLedger.Server;
    using server::AtlasLedger.Server.Persistence;
    using Xunit;

    /// <summary>
    /// Tests recovery at startup through the server's database contract, with the database kept in memory so the tests run
    /// anywhere: a second server built around the first one's database loads every tenant before it accepts traffic.  The
    /// same tests against SQL Server are in AtlasLedger.Persistence.Tests.
    /// </summary>
    public sealed class RecoveryTests
    {
        private static readonly string[] Settings =
        {
            "--environment", "Development",
            "--urls", "http://127.0.0.1:0",
            "--Logging:LogLevel:Default=None",
            "--Persistence:RetryAttempts=3",
            "--Persistence:RetryDelay=00:00:00.010",
        };

        /// <summary>
        /// A restarted server holds exactly what the first one made durable, and carries on from it: new writes take versions
        /// after every stored one.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task AServerRecoversItsTenantsAtStartup()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var database = new InMemoryLedgerDatabase();
            var (firmA, firmB) = (Guid.NewGuid(), Guid.NewGuid());
            string[] before;
            await using (var first = await StartAsync(database, cancellationToken))
            {
                var tool = Tool(first);
                var script = await LedgerScript.ReadAsync(SampleData.FirmA, cancellationToken);
                await tool.ApplyAsync(firmA, script, cancellationToken);
                await tool.ApplyAsync(firmB, script, cancellationToken);
                var symbol = new LedgerScript().Insert("Symbol", new SymbolDto { Scheme = "Custodian", Code = "9999", NodeId = Guid.Parse("5a000000-0000-0000-0000-000000000001") });
                await tool.ApplyAsync(firmA, symbol, cancellationToken);
                before = Rows(await Commands.ExportAsync(tool, firmA, cancellationToken));
                await first.StopAsync(cancellationToken);
            }

            await using var second = await StartAsync(database, cancellationToken);
            var restarted = Tool(second);
            var after = await Commands.ExportAsync(restarted, firmA, cancellationToken);
            Assert.Equal(before, Rows(after));
            Assert.NotEmpty(Rows(await Commands.ExportAsync(restarted, firmB, cancellationToken)));

            var highest = after.Operations.Max(operation => operation.Row!.Value.GetProperty("rowVersion").GetInt64());
            var added = await restarted.ApplyAsync(firmA, new LedgerScript().Insert("Symbol", new SymbolDto { Scheme = "Custodian", Code = "7777", NodeId = Guid.Parse("5a000000-0000-0000-0000-000000000002") }), cancellationToken);
            Assert.True(added.Single().GetProperty("rowVersion").GetInt64() > highest, "the restarted tenant resumes after its watermark");
        }

        /// <summary>
        /// A database that fails partway through loading (a connection reset, a database resuming from a pause) does not stop
        /// the server: recovery discards what it loaded and starts over, and the server comes up with every tenant whole.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task RecoveryStartsOverWhenTheDatabaseFailsPartway()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var database = new InMemoryLedgerDatabase();
            var (firmA, firmB) = (Guid.NewGuid(), Guid.NewGuid());
            string[] beforeA, beforeB;
            await using (var first = await StartAsync(database, cancellationToken))
            {
                var tool = Tool(first);
                var script = await LedgerScript.ReadAsync(SampleData.FirmA, cancellationToken);
                await tool.ApplyAsync(firmA, script, cancellationToken);
                await tool.ApplyAsync(firmB, script, cancellationToken);
                beforeA = Rows(await Commands.ExportAsync(tool, firmA, cancellationToken));
                beforeB = Rows(await Commands.ExportAsync(tool, firmB, cancellationToken));
                await first.StopAsync(cancellationToken);
            }

            // The first attempt loads one tenant whole, then the connection drops halfway through the next.
            var flaky = new FlakyDatabase(database, failuresBeforeSuccess: 1);
            await using var second = AtlasLedgerServer.Build(Settings, services => services.AddSingleton<ILedgerDatabase>(flaky));
            await second.StartAsync(cancellationToken);
            var restarted = Tool(second);
            Assert.Equal(1, flaky.Failures);
            Assert.Equal(beforeA, Rows(await Commands.ExportAsync(restarted, firmA, cancellationToken)));
            Assert.Equal(beforeB, Rows(await Commands.ExportAsync(restarted, firmB, cancellationToken)));
        }

        /// <summary>
        /// A database that keeps failing still stops the server from starting, once the retries run out.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task ADatabaseThatKeepsFailingStopsTheServer()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var flaky = new FlakyDatabase(new InMemoryLedgerDatabase(), failuresBeforeSuccess: int.MaxValue);
            await using var application = AtlasLedgerServer.Build(Settings, services => services.AddSingleton<ILedgerDatabase>(flaky));
            await Assert.ThrowsAsync<System.IO.IOException>(() => application.StartAsync(cancellationToken));
            Assert.Equal(4, flaky.Failures);   // the first attempt and three retries
        }

        /// <summary>
        /// A tenant that cannot be loaded stops the server from starting, rather than serving without it.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task ATenantThatFailsToLoadStopsTheServer()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var application = AtlasLedgerServer.Build(Settings, services => services.AddSingleton<ILedgerDatabase>(new BrokenDatabase()));
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => application.StartAsync(cancellationToken));
            Assert.Contains("stored rows are inconsistent", failure.Message, StringComparison.Ordinal);
        }

        private static async Task<WebApplication> StartAsync(InMemoryLedgerDatabase database, CancellationToken cancellationToken)
        {
            var application = AtlasLedgerServer.Build(Settings, services => services.AddSingleton<ILedgerDatabase>(database));
            await application.StartAsync(cancellationToken);
            return application;
        }

        private static LedgerApiClient Tool(WebApplication application) =>
            new LedgerApiClient(new HttpClient { BaseAddress = new Uri(application.Urls.First() + "/") }, DevelopmentCallers.Tool);

        private static string[] Rows(LedgerScript script) => script.Operations
            .Where(operation => operation.Table != "Alert")
            .Select(operation => $"{operation.Table} {string.Join(",", operation.Row!.Value.EnumerateObject().Select(property => $"{property.Name}={property.Value}"))}")
            .OrderBy(row => row, StringComparer.Ordinal)
            .ToArray();

        /// <summary>
        /// Fails the first few attempts: <see cref="PrepareAsync"/> when there is nothing to load, otherwise partway through
        /// loading the second tenant, after its partition has received rows.
        /// </summary>
        private sealed class FlakyDatabase : ILedgerDatabase
        {
            private readonly InMemoryLedgerDatabase inner;

            private readonly int failuresBeforeSuccess;

            private int loadsThisAttempt;

            public FlakyDatabase(InMemoryLedgerDatabase inner, int failuresBeforeSuccess)
            {
                this.inner = inner;
                this.failuresBeforeSuccess = failuresBeforeSuccess;
            }

            public int Failures { get; private set; }

            public async Task PrepareAsync(CancellationToken cancellationToken)
            {
                this.loadsThisAttempt = 0;
                await this.inner.PrepareAsync(cancellationToken);
                if ((await this.inner.ReadTenantsAsync(cancellationToken)).Count < 2)
                {
                    this.FailIfDue();
                }
            }

            public Task<IReadOnlyList<Guid>> ReadTenantsAsync(CancellationToken cancellationToken) => this.inner.ReadTenantsAsync(cancellationToken);

            public async Task<long> LoadAsync(server::AtlasLedger.Model.Primary.LedgerPartition partition, CancellationToken cancellationToken)
            {
                var watermark = await this.inner.LoadAsync(partition, cancellationToken);
                if (++this.loadsThisAttempt == 2)
                {
                    this.FailIfDue();   // after the rows went in: the partition is half-loaded from recovery's point of view
                }

                return watermark;
            }

            public Task WriteAsync(server::AtlasLedger.Model.Primary.LedgerCut cut, CancellationToken cancellationToken) => this.inner.WriteAsync(cut, cancellationToken);

            private void FailIfDue()
            {
                if (this.Failures < this.failuresBeforeSuccess)
                {
                    this.Failures++;
                    throw new System.IO.IOException("Connection reset by peer.");
                }
            }
        }

        private sealed class BrokenDatabase : ILedgerDatabase
        {
            public Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task<IReadOnlyList<Guid>> ReadTenantsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>(new[] { Guid.NewGuid() });

            public Task<long> LoadAsync(server::AtlasLedger.Model.Primary.LedgerPartition partition, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("The stored rows are inconsistent.");

            public Task WriteAsync(server::AtlasLedger.Model.Primary.LedgerCut cut, CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
