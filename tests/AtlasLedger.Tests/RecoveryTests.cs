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
            "--MarketFeed:Enabled=false",
            "--Logging:LogLevel:Default=None",
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
                var script = await LedgerScript.ReadAsync(Walkthrough.FirmAScript, cancellationToken);
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
