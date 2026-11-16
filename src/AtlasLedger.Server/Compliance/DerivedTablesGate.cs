// <copyright file="DerivedTablesGate.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Compliance
{
    using System;
    using System.Collections.Concurrent;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using AtlasLedger.Server.Switches;
    using Bellerophon.Rules;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Holds the server's traffic at startup, when the rules start on, until each tenant's rules have run once.  What the
    /// rules derive (positions, prices, daily values, statistics) is kept in memory only, so after a restart those tables
    /// are empty until each tenant's first cycle; a client that synced in between would show an account with no holdings,
    /// then see them fill.  Like loading the tenants, this happens before the server listens.  It gives up waiting after
    /// <see cref="ComplianceOptions.StartupWait"/>, and a tenant whose first cycle fails doesn't hold the others.
    /// </summary>
    public sealed partial class DerivedTablesGate : IHostedService
    {
        private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

        private readonly Ledger ledger;

        private readonly RulesEngineHost host;

        private readonly ServiceSwitch rules;

        private readonly ComplianceOptions options;

        private readonly TimeProvider time;

        private readonly ILogger<DerivedTablesGate> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="DerivedTablesGate"/> class.
        /// </summary>
        /// <param name="ledger">The ledger.</param>
        /// <param name="host">The rules engines.</param>
        /// <param name="switchboard">The services' switches.</param>
        /// <param name="options">The rules' settings.</param>
        /// <param name="time">The clock.</param>
        /// <param name="logger">The logger.</param>
        public DerivedTablesGate(Ledger ledger, RulesEngineHost host, ServiceSwitchboard switchboard, IOptions<ComplianceOptions> options, TimeProvider time, ILogger<DerivedTablesGate> logger)
        {
            ArgumentNullException.ThrowIfNull(switchboard);
            ArgumentNullException.ThrowIfNull(options);
            this.ledger = ledger;
            this.host = host;
            this.rules = switchboard[ServiceSwitchboard.Rules];
            this.options = options.Value;
            this.time = time;
            this.logger = logger;
        }

        /// <summary>
        /// Waits until every loaded tenant's rules have completed (or failed) a cycle, or the wait runs out.
        /// </summary>
        /// <param name="cancellationToken">Stops the wait (the host is stopping).</param>
        /// <returns>A task that completes when traffic may start.</returns>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (!this.rules.IsOn)
            {
                return;   // nothing derives the tables: they stay empty until the rules are switched on
            }

            var failed = new ConcurrentDictionary<Guid, bool>();
            void OnFailed(object? sender, RulesCycleFailedEventArgs eventArgs) => failed[eventArgs.TenantId] = true;
            this.host.CycleFailed += OnFailed;
            try
            {
                var started = this.time.GetTimestamp();
                while (true)
                {
                    var waiting = this.ledger.Partitions.Count(partition =>
                        !partition.IsFaulted &&
                        !failed.ContainsKey(partition.TenantId) &&
                        this.host.FindEngine(partition.TenantId) is not { CycleCount: > 0 });
                    if (waiting == 0)
                    {
                        this.LogReady(this.ledger.Partitions.Count(), this.time.GetElapsedTime(started).TotalMilliseconds);
                        return;
                    }

                    if (this.time.GetElapsedTime(started) >= this.options.StartupWait)
                    {
                        this.LogGaveUp(waiting, this.options.StartupWait);
                        return;
                    }

                    await Task.Delay(Poll, this.time, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                this.host.CycleFailed -= OnFailed;
            }
        }

        /// <inheritdoc/>
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        [LoggerMessage(Level = LogLevel.Information, Message = "Rules: every tenant's derived tables are built ({TenantCount} tenant(s), {Milliseconds:F0} ms); accepting traffic.")]
        private partial void LogReady(int tenantCount, double milliseconds);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Rules: {TenantCount} tenant(s) still deriving their tables after {Wait}; accepting traffic anyway (their pages fill as the rules finish).")]
        private partial void LogGaveUp(int tenantCount, TimeSpan wait);
    }
}
