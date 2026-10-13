// <copyright file="LedgerFlusher.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Persistence
{
    using System;
    using System.Collections.Concurrent;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Writes each tenant's committed changes to the database behind the ledger, one cut at a time, and marks them durable,
    /// which releases the writes waiting on them.
    /// </summary>
    /// <remarks>
    /// One loop runs per tenant, started when the tenant's partition appears.  Changes made while a cut is being written
    /// coalesce into the next one.  A write that keeps failing stops the tenant (fail-stop): its partition refuses further
    /// writes, because memory would otherwise drift ever further from the database.
    /// </remarks>
    internal sealed partial class LedgerFlusher : BackgroundService
    {
        private readonly Ledger ledger;

        private readonly ILedgerDatabase database;

        private readonly PersistenceOptions options;

        private readonly ILogger<LedgerFlusher> logger;

        private readonly ConcurrentDictionary<Guid, Task> loops = new ConcurrentDictionary<Guid, Task>();

        private CancellationToken stoppingToken;

        /// <summary>
        /// Initializes a new instance of the <see cref="LedgerFlusher"/> class.
        /// </summary>
        /// <param name="ledger">The ledger.</param>
        /// <param name="database">The database.</param>
        /// <param name="options">The persistence settings.</param>
        /// <param name="logger">The logger.</param>
        public LedgerFlusher(Ledger ledger, ILedgerDatabase database, IOptions<PersistenceOptions> options, ILogger<LedgerFlusher> logger)
        {
            this.ledger = ledger;
            this.database = database;
            this.options = options.Value;
            this.logger = logger;
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            this.stoppingToken = stoppingToken;
            this.ledger.PartitionCreated += this.OnPartitionCreated;
            try
            {
                foreach (var partition in this.ledger.Partitions)
                {
                    this.Start(partition);
                }

                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                this.ledger.PartitionCreated -= this.OnPartitionCreated;
            }

            await Task.WhenAll(this.loops.Values.ToArray()).ConfigureAwait(false);
        }

        private void OnPartitionCreated(object? sender, LedgerPartition partition) => this.Start(partition);

        private void Start(LedgerPartition partition)
        {
            if (!this.stoppingToken.IsCancellationRequested)
            {
                this.loops.GetOrAdd(partition.TenantId, _ => Task.Run(() => this.FlushAsync(partition, this.stoppingToken)));
            }
        }

        private async Task FlushAsync(LedgerPartition partition, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await partition.WaitForChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var cut = partition.TakeCut();
                if (!await this.WriteAsync(cut, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                partition.MarkDurable(cut.Sequence);
            }
        }

        /// <summary>
        /// Writes a cut, retrying with backoff; stops the tenant if every attempt fails.
        /// </summary>
        /// <returns>True if the cut was written.</returns>
        private async Task<bool> WriteAsync(LedgerCut cut, CancellationToken cancellationToken)
        {
            var delay = this.options.RetryDelay;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await this.database.WriteAsync(cut, cancellationToken).ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }
                catch (Exception exception) when (attempt < this.options.RetryAttempts)
                {
                    this.LogRetry(exception, cut.TenantId, attempt + 1, delay);
                }
                catch (Exception exception)
                {
                    this.LogStopped(exception, cut.TenantId, cut.Sequence);
                    this.ledger.GetPartition(cut.TenantId).Fault(exception);
                    return false;
                }

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                delay *= 2;
            }
        }

        [LoggerMessage(Level = LogLevel.Warning, Message = "Writing tenant {TenantId} to the database failed (attempt {Attempt}); retrying in {Delay}.")]
        private partial void LogRetry(Exception exception, Guid tenantId, int attempt, TimeSpan delay);

        [LoggerMessage(Level = LogLevel.Critical, Message = "Writing tenant {TenantId} through sequence {Sequence} failed on every attempt; the tenant is stopped until it is reloaded from the database.")]
        private partial void LogStopped(Exception exception, Guid tenantId, long sequence);
    }
}
