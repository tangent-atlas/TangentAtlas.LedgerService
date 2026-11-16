// <copyright file="DerivedTablesGateTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias server;

namespace AtlasLedger.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Bellerophon.Rules;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;
    using server::AtlasLedger.Model.Primary;
    using server::AtlasLedger.Server.Compliance;
    using server::AtlasLedger.Server.Switches;
    using Xunit;

    /// <summary>
    /// At startup, with the rules on, the server takes no traffic until each tenant's rules have run once (what they derive
    /// is kept in memory only, so it is empty until then); it gives up after its wait, and stays out of the way with the
    /// rules off.
    /// </summary>
    public sealed class DerivedTablesGateTests
    {
        /// <summary>
        /// The gate holds while a tenant's first cycle runs, and opens when it completes.
        /// </summary>
        /// <returns>A task.</returns>
        [Fact]
        public async Task TrafficWaitsForEachTenantsFirstCycle()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var release = new ManualResetEventSlim();
            var ledger = new server::AtlasLedger.Model.Ledger();
            var partition = ledger.GetPartition(Guid.NewGuid());
            await using var host = new RulesEngineHost(new HeldSessions(release));
            host.Start(new LedgerRulesTenant(partition));

            var starting = Gate(ledger, host, "rules", TimeSpan.FromSeconds(30)).StartAsync(cancellationToken);
            await Task.Delay(300, cancellationToken);
            Assert.False(starting.IsCompleted, "the gate opened before the tenant's first cycle");

            release.Set();
            await starting.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }

        /// <summary>
        /// A first cycle that takes too long holds traffic only as long as the wait.
        /// </summary>
        /// <returns>A task.</returns>
        [Fact]
        public async Task TheGateGivesUpAfterItsWait()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var release = new ManualResetEventSlim();
            var ledger = new server::AtlasLedger.Model.Ledger();
            await using var host = new RulesEngineHost(new HeldSessions(release));
            host.Start(new LedgerRulesTenant(ledger.GetPartition(Guid.NewGuid())));

            await Gate(ledger, host, "rules", TimeSpan.FromMilliseconds(200)).StartAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            release.Set();
        }

        /// <summary>
        /// With the rules off nothing derives the tables, so there is nothing to wait for.
        /// </summary>
        /// <returns>A task.</returns>
        [Fact]
        public async Task WithTheRulesOffTheGateIsOpen()
        {
            var ledger = new server::AtlasLedger.Model.Ledger();
            ledger.GetPartition(Guid.NewGuid());
            await using var host = new RulesEngineHost(new HeldSessions(new ManualResetEventSlim()));

            await Gate(ledger, host, string.Empty, TimeSpan.FromSeconds(30)).StartAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        private static DerivedTablesGate Gate(server::AtlasLedger.Model.Ledger ledger, RulesEngineHost host, string startOn, TimeSpan wait) =>
            new DerivedTablesGate(
                ledger,
                host,
                new ServiceSwitchboard(TimeProvider.System, Options.Create(new ServiceSwitchOptions { StartOn = startOn })),
                Options.Create(new ComplianceOptions { StartupWait = wait }),
                TimeProvider.System,
                NullLogger<DerivedTablesGate>.Instance);

        /// <summary>Sessions whose first firing waits until released: a first cycle that takes a while.</summary>
        private sealed class HeldSessions(ManualResetEventSlim release) : IRulesSessionFactory
        {
            public IRulesSession CreateSession(IRulesTenant tenant) => new Session(release);

            private sealed class Session(ManualResetEventSlim release) : IRulesSession
            {
                public void Apply(IReadOnlyList<FactChange> changes)
                {
                }

                public int Fire()
                {
                    release.Wait(TimeSpan.FromSeconds(30));
                    return 0;
                }
            }
        }
    }
}
