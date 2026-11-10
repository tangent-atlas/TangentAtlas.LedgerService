// <copyright file="RulesService.cs" company="Tangent Atlas">
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
    /// Runs every tenant's rules (holdings, prices, compliance), always: starts a rules engine for every tenant, and for
    /// each new one as it appears; stops them at shutdown; trims each tenant's change journal to what its engine still
    /// needs; and logs what the engines decide.  The compliance switch (<see cref="ServiceSwitchboard.Compliance"/>) only
    /// decides whether the compliance rules take part: it is a fact in working memory (<see cref="ComplianceOn"/>).
    /// </summary>
    /// <remarks>
    /// Every change of state happens on this service's one loop, so a switch change and a new tenant never race.  Alerts
    /// already raised stay as they are while compliance is off; the rules reconcile them when it comes on again.
    /// </remarks>
    internal sealed partial class RulesService : BackgroundService
    {
        private readonly Ledger ledger;

        private readonly RulesEngineHost host;

        private readonly ComplianceOptions options;

        private readonly ServiceSwitch serviceSwitch;

        private readonly SemaphoreSlim partitionCreated = new SemaphoreSlim(0);

        private bool? isComplianceOn;

        private readonly ILogger<RulesService> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RulesService"/> class.
        /// </summary>
        /// <param name="ledger">The ledger.</param>
        /// <param name="host">The rules engines.</param>
        /// <param name="options">The compliance settings.</param>
        /// <param name="switchboard">The services' switches.</param>
        /// <param name="logger">The logger.</param>
        public RulesService(Ledger ledger, RulesEngineHost host, IOptions<ComplianceOptions> options, ServiceSwitchboard switchboard, ILogger<RulesService> logger)
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
            // The engines always run: holdings are core state.  The compliance switch is a fact in each session's working
            // memory, put there when the session is made, so a change of the switch restarts the engines, and each starts
            // again from a snapshot with the fact in or out.  Switching is rare; a restart costs one rebuild.
            var isOn = this.serviceSwitch.IsOn;
            if (this.isComplianceOn is { } wasOn && wasOn != isOn)
            {
                foreach (var tenantId in this.host.Tenants)
                {
                    await this.host.StopAsync(tenantId).ConfigureAwait(false);
                }
            }

            foreach (var partition in this.ledger.Partitions)
            {
                this.Start(partition);
            }

            if (this.isComplianceOn != isOn)
            {
                this.isComplianceOn = isOn;
                if (isOn)
                {
                    this.LogSwitchedOn(this.serviceSwitch.OnUntil, this.host.Tenants.Count);
                }
                else
                {
                    this.LogSwitchedOff(this.host.Tenants.Count);
                }
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
                    if (this.host.FindEngine(partition.TenantId) is { } engine && engine.AppliedSequence > 0)
                    {
                        partition.TrimJournal(engine.AppliedSequence);
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

        [LoggerMessage(Level = LogLevel.Information, Message = "Compliance: switched on until {OnUntil}; the rules of {TenantCount} tenant(s) include it.")]
        private partial void LogSwitchedOn(DateTimeOffset? onUntil, int tenantCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "Compliance: switched off; the rules of {TenantCount} tenant(s) keep running without it.")]
        private partial void LogSwitchedOff(int tenantCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "Rules for tenant {TenantId}: {RulesFired} rule(s) fired from {FactChanges} fact change(s), committed at {Sequence}.")]
        private partial void LogDecisions(Guid tenantId, int rulesFired, int factChanges, long sequence);

        [LoggerMessage(Level = LogLevel.Information, Message = "Rules for tenant {TenantId}: decisions refused ({Reason}); the rules decide again with newer data.")]
        private partial void LogRejected(Guid tenantId, string reason);

        [LoggerMessage(Level = LogLevel.Error, Message = "Rules for tenant {TenantId}: a cycle failed; the session is rebuilt from a snapshot.")]
        private partial void LogFailed(Exception exception, Guid tenantId);
    }
}
