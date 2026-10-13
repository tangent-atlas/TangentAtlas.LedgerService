// <copyright file="InMemoryDrillTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias server;

namespace AtlasLedger.Persistence.Tests
{
    using System;
    using System.Threading.Tasks;
    using server::AtlasLedger.Model.Primary;
    using server::AtlasLedger.Server.Persistence;
    using Xunit;
    using Ledger = server::AtlasLedger.Model.Ledger;

    /// <summary>
    /// The recovery drill against the in-memory database, which needs no SQL Server: it checks the drill itself, and the
    /// database contract every store must meet, wherever the tests run.
    /// </summary>
    public sealed class InMemoryDrillTests
    {
        /// <summary>
        /// Random changes flushed at random moments load back equal to the live partitions, versions and watermarks included.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task RecoveryDrillsLoadEveryTenantAsItWas()
        {
            var token = TestContext.Current.CancellationToken;
            ILedgerDatabase database = new InMemoryLedgerDatabase();
            var ledger = new Ledger { UsesWriteBehind = true };
            for (var seed = 1; seed <= 6; seed++)
            {
                var random = new Random(seed);
                var partition = ledger.GetPartition(Guid.NewGuid());
                var keys = Model.Populate(partition);
                Model.Change(partition, keys);
                for (var step = 0; step < 300; step++)
                {
                    Model.RandomStep(partition, keys, random, step);
                    if (random.Next(12) == 0 || step == 299)
                    {
                        var cut = partition.TakeCut();
                        await database.WriteAsync(cut, token);
                        partition.MarkDurable(cut.Sequence);
                    }
                }
            }

            Assert.Equal(6, (await database.ReadTenantsAsync(token)).Count);
            foreach (var live in ledger.Partitions)
            {
                var loaded = new Ledger().GetPartition(live.TenantId);
                Assert.Equal(live.DurableSequence, await database.LoadAsync(loaded, token));
                Assert.Equal(Model.Snapshot(live), Model.Snapshot(loaded));
                Assert.Equal(live.DurableSequence, loaded.DurableSequence);
            }
        }
    }
}
