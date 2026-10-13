// <copyright file="ComplianceService.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Compliance
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using Bellerophon.Rules;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Runs the compliance rules for every tenant: starts a rules engine when a tenant's partition appears, logs what the
    /// engines decide, trims each tenant's change journal to what its engine still needs, and stops the engines at shutdown.
    /// </summary>
    internal sealed partial class ComplianceService : BackgroundService
    {
        private readonly Ledger ledger;

        private readonly RulesEngineHost host;

        private readonly ComplianceOptions options;

        private readonly ILogger<ComplianceService> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ComplianceService"/> class.
        /// </summary>
        /// <param name="ledger">The ledger.</param>
        /// <param name="host">The rules engines.</param>
        /// <param name="options">The compliance settings.</param>
        /// <param name="logger">The logger.</param>
        public ComplianceService(Ledger ledger, RulesEngineHost host, IOptions<ComplianceOptions> options, ILogger<ComplianceService> logger)
        {
            this.ledger = ledger;
            this.host = host;
            this.options = options.Value;
            this.logger = logger;
        }

        /// <inheritdoc/>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
            await this.host.DisposeAsync().ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            this.host.CycleCompleted += this.OnCycleCompleted;
            this.host.CycleFailed += this.OnCycleFailed;
            this.ledger.PartitionCreated += this.OnPartitionCreated;
            try
            {
                foreach (var partition in this.ledger.Partitions)
                {
                    this.Start(partition);
                }

                using var timer = new PeriodicTimer(this.options.JournalTrimInterval);
                while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    this.TrimJournals();
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                this.ledger.PartitionCreated -= this.OnPartitionCreated;
                this.host.CycleCompleted -= this.OnCycleCompleted;
                this.host.CycleFailed -= this.OnCycleFailed;
            }
        }

        private void OnPartitionCreated(object? sender, LedgerPartition partition) => this.Start(partition);

        private void Start(LedgerPartition partition)
        {
            var tenant = new LedgerRulesTenant(partition);
            if (!this.host.Start(tenant))
            {
                // Already running.
                tenant.Dispose();
            }
        }

        /// <summary>
        /// Discards journal history no engine needs; without it, deleted rows would stay in memory forever.
        /// </summary>
        private void TrimJournals()
        {
            foreach (var partition in this.ledger.Partitions)
            {
                if (this.host.FindEngine(partition.TenantId) is { } engine && engine.AppliedSequence > 0)
                {
                    partition.TrimJournal(engine.AppliedSequence);
                }
            }
        }

        private void OnCycleCompleted(object? sender, RulesCycleEventArgs eventArgs)
        {
            var result = eventArgs.Result;
            if (result.RejectedActions is { } rejected)
            {
                this.LogRejected(result.TenantId, rejected.Message);
            }
            else if (result.RulesFired > 0)
            {
                this.LogDecisions(result.TenantId, result.RulesFired, result.FactChanges, result.CommittedSequence);
            }
        }

        private void OnCycleFailed(object? sender, RulesCycleFailedEventArgs eventArgs) => this.LogFailed(eventArgs.Exception, eventArgs.TenantId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Compliance for tenant {TenantId}: {RulesFired} decision(s) from {FactChanges} fact change(s), committed at {Sequence}.")]
        private partial void LogDecisions(Guid tenantId, int rulesFired, int factChanges, long sequence);

        [LoggerMessage(Level = LogLevel.Information, Message = "Compliance for tenant {TenantId}: decisions refused ({Reason}); the rules decide again with newer data.")]
        private partial void LogRejected(Guid tenantId, string reason);

        [LoggerMessage(Level = LogLevel.Error, Message = "Compliance for tenant {TenantId}: a rules cycle failed; the session is rebuilt from a snapshot.")]
        private partial void LogFailed(Exception exception, Guid tenantId);
    }
}
