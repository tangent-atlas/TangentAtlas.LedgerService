// <copyright file="SqlDatabaseTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias client;
extern alias server;

namespace AtlasLedger.Persistence.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using client::AtlasLedger.Client;
    using client::AtlasLedger.Client.Api;
    using client::AtlasLedger.Client.Scripts;
    using Microsoft.Data.SqlClient;
    using server::AtlasLedger.Model.Primary;
    using server::AtlasLedger.Server;
    using Xunit;
    using Ledger = server::AtlasLedger.Model.Ledger;
    using SymbolDto = client::AtlasLedger.Model.SymbolDto;

    /// <summary>
    /// Tests the generated SQL Server writer and loader against a real database: preparation, round trips, a crash before
    /// commit, a statement that fails mid-cut, randomized recovery drills, a missing schema, throughput, and the server
    /// recovering from SQL Server at startup.
    /// </summary>
    public sealed class SqlDatabaseTests
    {
        private static CancellationToken Token => TestContext.Current.CancellationToken;

        /// <summary>
        /// Preparation creates every table type, and doing it again changes nothing.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task PreparationCreatesTheTableTypes()
        {
            await using var test = await TestDatabase.CreateAsync(cancellationToken: Token);
            var database = new LedgerSqlDatabase(test.ConnectionString);
            await database.PrepareAsync(Token);
            await database.PrepareAsync(Token);

            var types = Convert.ToInt32(await test.ExecuteAsync("SELECT COUNT(*) FROM sys.table_types WHERE schema_id = SCHEMA_ID(N'bellerophon');", Token));
            Assert.Equal(LedgerSqlDatabase.TableTypes.Count, types);
        }

        /// <summary>
        /// Cuts written to the database load back into an identical partition: every table, null values, updates, deletes,
        /// and a key deleted and inserted again; the partition resumes after the watermark.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task CutsRoundTrip()
        {
            await using var test = await TestDatabase.CreateAsync(cancellationToken: Token);
            var database = new LedgerSqlDatabase(test.ConnectionString);
            await database.PrepareAsync(Token);
            var partition = new Ledger { UsesWriteBehind = true }.GetPartition(Guid.NewGuid());
            var model = Model.Populate(partition);
            await FlushAsync(database, partition);
            await AssertLoadsEqualAsync(database, partition);

            // Updates, deletes, a null set and cleared, and a key deleted and inserted again, in the next cut.
            Model.Change(partition, model);
            await FlushAsync(database, partition);
            var loaded = await AssertLoadsEqualAsync(database, partition);
            Assert.Equal(partition.DurableSequence, loaded.DurableSequence);
            var next = loaded.InsertNode(Model.NewNode("After"));
            Assert.True(next.RowVersion > partition.DurableSequence, "a loaded partition resumes after its watermark");
            Assert.Equal(new[] { partition.TenantId }, await database.ReadTenantsAsync(Token));
        }

        /// <summary>
        /// A cut written in a transaction that never commits (the process dies first) leaves the database, watermark
        /// included, exactly as the previous cut left it.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task ACrashBeforeCommitLeavesTheDatabaseAsItWas()
        {
            await using var test = await TestDatabase.CreateAsync(cancellationToken: Token);
            var database = new LedgerSqlDatabase(test.ConnectionString);
            await database.PrepareAsync(Token);
            var partition = new Ledger { UsesWriteBehind = true }.GetPartition(Guid.NewGuid());
            var model = Model.Populate(partition);
            await FlushAsync(database, partition);
            var durable = Model.Snapshot(partition);

            Model.Change(partition, model);
            var cut = partition.TakeCut();
            await using (var connection = new SqlConnection(test.ConnectionString))
            {
                await connection.OpenAsync(Token);
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(Token);
                await database.WriteAsync(cut, connection, transaction, Token);
                await transaction.RollbackAsync(Token);
            }

            var loaded = await LoadAsync(database, partition.TenantId);
            Assert.Equal(durable, Model.Snapshot(loaded));
            Assert.True(loaded.DurableSequence < cut.Sequence, "the watermark did not move");
        }

        /// <summary>
        /// A statement that fails partway through a cut rolls back the whole cut: rows already written in it, and the
        /// watermark, are undone.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task AFailedStatementRollsBackTheWholeCut()
        {
            await using var test = await TestDatabase.CreateAsync(cancellationToken: Token);
            var database = new LedgerSqlDatabase(test.ConnectionString);
            await database.PrepareAsync(Token);
            var partition = new Ledger { UsesWriteBehind = true }.GetPartition(Guid.NewGuid());
            var model = Model.Populate(partition);
            await FlushAsync(database, partition);
            var durable = Model.Snapshot(partition);

            // Symbol is written after Node in every cut; make its write fail.
            await test.ExecuteAsync("CREATE TRIGGER [dbo].[RefuseSymbols] ON [dbo].[Symbol] AFTER INSERT, UPDATE AS BEGIN THROW 50000, N'Refused for the test.', 1; END", Token);
            Model.Change(partition, model);
            var cut = partition.TakeCut();
            Assert.NotEmpty(cut.ChangedNodes);
            Assert.NotEmpty(cut.ChangedSymbols);
            var failure = await Assert.ThrowsAsync<SqlException>(() => database.WriteAsync(cut, Token));
            Assert.Contains("Refused for the test", failure.Message, StringComparison.Ordinal);

            Assert.Equal(durable, Model.Snapshot(await LoadAsync(database, partition.TenantId)));
            await test.ExecuteAsync("DROP TRIGGER [dbo].[RefuseSymbols];", Token);
            await database.WriteAsync(cut, Token);
            partition.MarkDurable(cut.Sequence);
            await AssertLoadsEqualAsync(database, partition);
        }

        /// <summary>
        /// The recovery drill: random inserts, updates, and deletes with reused keys (alerts raised and cleared on the same
        /// node and rule, symbols deleted and inserted again), rollbacks, and many tenants, flushed at random moments; every
        /// tenant loads back equal to its live partition.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task RecoveryDrillsLoadEveryTenantAsItWas()
        {
            await using var test = await TestDatabase.CreateAsync(cancellationToken: Token);
            var database = new LedgerSqlDatabase(test.ConnectionString);
            await database.PrepareAsync(Token);
            var ledger = new Ledger { UsesWriteBehind = true };
            for (var seed = 1; seed <= 6; seed++)
            {
                var random = new Random(seed);
                var partition = ledger.GetPartition(Guid.NewGuid());
                var model = Model.Populate(partition);
                for (var step = 0; step < 300; step++)
                {
                    Model.RandomStep(partition, model, random, step);
                    if (random.Next(12) == 0 || step == 299)
                    {
                        await FlushAsync(database, partition);
                    }
                }
            }

            Assert.Equal(6, (await database.ReadTenantsAsync(Token)).Count);
            foreach (var partition in ledger.Partitions)
            {
                await AssertLoadsEqualAsync(database, partition);
            }
        }

        /// <summary>
        /// A database without the schema's migrations is refused with a message that says to apply them.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task AMissingSchemaIsReported()
        {
            await using var test = await TestDatabase.CreateAsync(migrate: false, cancellationToken: Token);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new LedgerSqlDatabase(test.ConnectionString).PrepareAsync(Token));
            Assert.Contains("Apply the schema project's migrations", failure.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Throughput: a cut of 10,000 changed rows (the batch limit) is written in one round trip, and a tenant of that size
        /// loads back; the times are reported.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task LargeCutsAreWrittenInOneRoundTrip()
        {
            await using var test = await TestDatabase.CreateAsync(cancellationToken: Token);
            var database = new LedgerSqlDatabase(test.ConnectionString);
            await database.PrepareAsync(Token);
            var partition = new Ledger { UsesWriteBehind = true }.GetPartition(Guid.NewGuid());
            using (var transaction = partition.BeginTransaction())
            {
                for (var index = 0; index < 5_000; index++)
                {
                    var node = transaction.InsertNode(Model.NewNode("N" + index));
                    transaction.InsertAccount(new Account { AccountId = node.NodeId, AccountType = AccountType.ManagedAccount });
                }

                transaction.Commit();
            }

            var cut = partition.TakeCut();
            var watch = Stopwatch.StartNew();
            await database.WriteAsync(cut, Token);
            var write = watch.Elapsed;
            partition.MarkDurable(cut.Sequence);

            // The same rows again, every one an update.
            using (var transaction = partition.BeginTransaction())
            {
                foreach (var node in partition.Nodes.ToList())
                {
                    transaction.UpdateNode(Model.Changed(node, dto => dto.DisplayName = "U"));
                }

                transaction.Commit();
            }

            var updates = partition.TakeCut();
            watch.Restart();
            await database.WriteAsync(updates, Token);
            var update = watch.Elapsed;
            partition.MarkDurable(updates.Sequence);
            watch.Restart();
            await AssertLoadsEqualAsync(database, partition);
            var load = watch.Elapsed;
            TestContext.Current.SendDiagnosticMessage($"10,000 inserted rows written in {write.TotalMilliseconds:F0} ms; 5,000 updates in {update.TotalMilliseconds:F0} ms; 10,000 rows loaded in {load.TotalMilliseconds:F0} ms.");
            Assert.True(write < TimeSpan.FromSeconds(30) && update < TimeSpan.FromSeconds(30), $"write {write}, update {update}");
        }

        /// <summary>
        /// The server, configured with a connection string, makes its tenants durable in SQL Server, and a restarted server
        /// loads them before it accepts traffic.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task TheServerRecoversFromSqlServer()
        {
            await using var test = await TestDatabase.CreateAsync(cancellationToken: Token);
            var settings = new[]
            {
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                "--Logging:LogLevel:Default=None",
                $"--ConnectionStrings:Ledger={test.ConnectionString}",
            };
            var tenant = Guid.NewGuid();
            string[] before;
            await using (var first = server::AtlasLedger.Server.AtlasLedgerServer.Build(settings))
            {
                await first.StartAsync(Token);
                var tool = Tool(first);
                await tool.ApplyAsync(tenant, await LedgerScript.ReadAsync(SampleData.FirmA, Token), Token);
                await tool.ApplyAsync(tenant, new LedgerScript().Insert("Symbol", new SymbolDto { Scheme = "Custodian", Code = "9999", NodeId = Guid.Parse("5a000000-0000-0000-0000-000000000001") }), Token);
                before = Rows(await Commands.ExportAsync(tool, tenant, Token));
                await first.StopAsync(Token);
            }

            Assert.True(Convert.ToInt64(await test.ExecuteAsync($"SELECT [DurableSequence] FROM [bellerophon].[Watermark] WHERE [TenantId] = '{tenant}';", Token)) > 0);
            await using var second = server::AtlasLedger.Server.AtlasLedgerServer.Build(settings);
            await second.StartAsync(Token);
            Assert.Equal(before, Rows(await Commands.ExportAsync(Tool(second), tenant, Token)));
        }

        private static async Task FlushAsync(LedgerSqlDatabase database, LedgerPartition partition)
        {
            var cut = partition.TakeCut();
            await database.WriteAsync(cut, Token);
            partition.MarkDurable(cut.Sequence);
        }

        private static async Task<LedgerPartition> LoadAsync(LedgerSqlDatabase database, Guid tenantId)
        {
            var partition = new Ledger().GetPartition(tenantId);
            await database.LoadAsync(partition, Token);
            return partition;
        }

        private static async Task<LedgerPartition> AssertLoadsEqualAsync(LedgerSqlDatabase database, LedgerPartition live)
        {
            var loaded = await LoadAsync(database, live.TenantId);
            Assert.Equal(Model.Snapshot(live), Model.Snapshot(loaded));
            Assert.Equal(live.DurableSequence, loaded.DurableSequence);
            return loaded;
        }

        private static LedgerApiClient Tool(Microsoft.AspNetCore.Builder.WebApplication application) =>
            new LedgerApiClient(new HttpClient { BaseAddress = new Uri(application.Urls.First() + "/") }, DevelopmentCallers.Tool);

        private static string[] Rows(LedgerScript script) => script.Operations
            .Where(operation => operation.Table != "Alert")
            .Select(operation => $"{operation.Table} {string.Join(",", operation.Row!.Value.EnumerateObject().Select(property => $"{property.Name}={property.Value}"))}")
            .OrderBy(row => row, StringComparer.Ordinal)
            .ToArray();
    }
}
