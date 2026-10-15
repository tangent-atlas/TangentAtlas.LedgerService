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
    using AtlasLedger.Server.Switches;
    using Bellerophon.Rules;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Runs the compliance rules for every tenant while its switch (<see cref="ServiceSwitchboard.Compliance"/>) is on:
    /// starts a rules engine for every tenant, and for each new one as it appears; stops them all when the switch goes off
    /// and at shutdown; and logs what the engines decide.  Whether the engines run or not, it trims each tenant's change
    /// journal to what an engine still needs, which is nothing while they are off.
    /// </summary>
    /// <remarks>
    /// Every change of state happens on this service's one loop, so turning on, turning off, and a new tenant never race.
    /// Alerts already raised stay as they are while the engines are off; the engines reconcile them when they start again.
    /// </remarks>
    internal sealed partial class ComplianceService : BackgroundService
    {
        private readonly Ledger ledger;

        private readonly RulesEngineHost host;

        private readonly ComplianceOptions options;

        private readonly ServiceSwitch serviceSwitch;

        private readonly SemaphoreSlim partitionCreated = new SemaphoreSlim(0);

        private bool isRunning;

        private readonly ILogger<ComplianceService> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ComplianceService"/> class.
        /// </summary>
        /// <param name="ledger">The ledger.</param>
        /// <param name="host">The rules engines.</param>
        /// <param name="options">The compliance settings.</param>
        /// <param name="switchboard">The services' switches.</param>
        /// <param name="logger">The logger.</param>
        public ComplianceService(Ledger ledger, RulesEngineHost host, IOptions<ComplianceOptions> options, ServiceSwitchboard switchboard, ILogger<ComplianceService> logger)
        {
            this.ledger = ledger;
            this.host = host;
            this.options = options.Value;
            this.serviceSwitch = switchboard[ServiceSwitchboard.Compliance];
            this.logger = logger;
        }

        /// <inheritdoc/>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
            await this.host.DisposeAsync().ConfigureAwait(false);
            this.partitionCreated.Dispose();
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            this.host.CycleCompleted += this.OnCycleCompleted;
            this.host.CycleFailed += this.OnCycleFailed;
            this.ledger.PartitionCreated += this.OnPartitionCreated;
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await this.ReconcileAsync().ConfigureAwait(false);
                    this.TrimJournals();
                    await this.WaitForWorkAsync(stoppingToken).ConfigureAwait(false);
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

        private void OnPartitionCreated(object? sender, LedgerPartition partition) => this.partitionCreated.Release();

        /// <summary>
        /// Waits for the switch to change, a new tenant, or the next journal trim, whichever comes first.
        /// </summary>
        private async Task WaitForWorkAsync(CancellationToken stoppingToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            await Task.WhenAny(
                this.serviceSwitch.WaitForChangeAsync(linked.Token),
                this.partitionCreated.WaitAsync(linked.Token),
                Task.Delay(this.options.JournalTrimInterval, linked.Token)).ConfigureAwait(false);

            // Cancel the waits that lost, so an abandoned semaphore wait cannot swallow a later new tenant's signal.
            await linked.CancelAsync().ConfigureAwait(false);
            stoppingToken.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Makes the running engines match the switch: one per tenant while on, none while off.
        /// </summary>
        private async Task ReconcileAsync()
        {
            if (this.serviceSwitch.IsOn)
            {
                foreach (var partition in this.ledger.Partitions)
                {
                    this.Start(partition);
                }

                if (!this.isRunning)
                {
                    this.isRunning = true;
                    this.LogSwitchedOn(this.serviceSwitch.OnUntil, this.host.Tenants.Count);
                }
            }
            else if (this.isRunning || this.host.Tenants.Count > 0)
            {
                var count = this.host.Tenants.Count;
                foreach (var tenantId in this.host.Tenants)
                {
                    await this.host.StopAsync(tenantId).ConfigureAwait(false);
                }

                this.isRunning = false;
                this.LogSwitchedOff(count);
            }
        }

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
        /// Discards journal history no engine needs; without it, deleted rows would stay in memory forever.  With no engine,
        /// nothing needs any of it.  (The flusher writes from its own set of changed rows, not from the journal.)
        /// </summary>
        private void TrimJournals()
        {
            foreach (var partition in this.ledger.Partitions)
            {
                if (partition.IsFaulted)
                {
                    continue;
                }

                try
                {
                    if (this.host.FindEngine(partition.TenantId) is { } engine)
                    {
                        if (engine.AppliedSequence > 0)
                        {
                            partition.TrimJournal(engine.AppliedSequence);
                        }
                    }
                    else if (!this.serviceSwitch.IsOn)
                    {
                        partition.TrimJournal(long.MaxValue);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The partition is loading or has just faulted: trim it next time.
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

        [LoggerMessage(Level = LogLevel.Information, Message = "Compliance: switched on until {OnUntil}; {TenantCount} tenant engine(s) running.")]
        private partial void LogSwitchedOn(DateTimeOffset? onUntil, int tenantCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "Compliance: switched off; {TenantCount} tenant engine(s) stopped.")]
        private partial void LogSwitchedOff(int tenantCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "Compliance for tenant {TenantId}: {RulesFired} decision(s) from {FactChanges} fact change(s), committed at {Sequence}.")]
        private partial void LogDecisions(Guid tenantId, int rulesFired, int factChanges, long sequence);

        [LoggerMessage(Level = LogLevel.Information, Message = "Compliance for tenant {TenantId}: decisions refused ({Reason}); the rules decide again with newer data.")]
        private partial void LogRejected(Guid tenantId, string reason);

        [LoggerMessage(Level = LogLevel.Error, Message = "Compliance for tenant {TenantId}: a rules cycle failed; the session is rebuilt from a snapshot.")]
        private partial void LogFailed(Exception exception, Guid tenantId);
    }
}
