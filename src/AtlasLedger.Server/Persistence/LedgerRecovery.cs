// <copyright file="LedgerRecovery.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Persistence
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Loads every tenant from the database at startup, before the server accepts traffic and before the flusher and the
    /// compliance engine start.  Registered as the first hosted service: the host starts hosted services in order, each
    /// finishing its start before the next, and the web server last.  If any tenant fails to load, the server does not start.
    /// </summary>
    /// <remarks>
    /// A failed load is retried as a whole, with the flusher's backoff (<see cref="PersistenceOptions.RetryAttempts"/> and
    /// <see cref="PersistenceOptions.RetryDelay"/>: about 100 seconds in Azure), so a database that is resuming from a pause,
    /// failing over, or briefly refusing connections does not stop the server.  Before each retry, every tenant loaded so
    /// far is discarded, so no partition is ever loaded twice or served half-loaded.  Only when every attempt fails does
    /// the server fail to start.  (Leaving the retry to the platform is costly: App Service holds a site that keeps failing
    /// to start for one, two, then four minutes or more between attempts.)
    /// </remarks>
    internal sealed partial class LedgerRecovery : IHostedService
    {
        private readonly Ledger ledger;

        private readonly ILedgerDatabase database;

        private readonly PersistenceOptions options;

        private readonly ILogger<LedgerRecovery> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="LedgerRecovery"/> class.
        /// </summary>
        /// <param name="ledger">The in-memory ledger, empty at startup.</param>
        /// <param name="database">The database.</param>
        /// <param name="options">The persistence settings: how often, and how patiently, to retry.</param>
        /// <param name="logger">The logger.</param>
        public LedgerRecovery(Ledger ledger, ILedgerDatabase database, IOptions<PersistenceOptions> options, ILogger<LedgerRecovery> logger)
        {
            ArgumentNullException.ThrowIfNull(options);
            this.ledger = ledger;
            this.database = database;
            this.options = options.Value;
            this.logger = logger;
        }

        /// <inheritdoc/>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var watch = Stopwatch.StartNew();
            var delay = this.options.RetryDelay;
            for (var attempt = 0; ; attempt++)
            {
                var loaded = new List<Guid>();
                try
                {
                    var tenantCount = await this.LoadAllAsync(loaded, cancellationToken).ConfigureAwait(false);
                    this.LogRecovered(tenantCount, watch.Elapsed.TotalMilliseconds);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    this.Discard(loaded);
                    throw;
                }
                catch (Exception exception) when (attempt < this.options.RetryAttempts)
                {
                    this.Discard(loaded);
                    this.LogRetry(exception, attempt + 1, delay);
                }
                catch (Exception exception)
                {
                    this.Discard(loaded);
                    this.LogFailed(exception, attempt + 1);
                    throw;
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay *= 2;
            }
        }

        /// <inheritdoc/>
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>
        /// One attempt: prepares the database and loads every tenant, recording each partition it fills.
        /// </summary>
        private async Task<int> LoadAllAsync(List<Guid> loaded, CancellationToken cancellationToken)
        {
            await this.database.PrepareAsync(cancellationToken).ConfigureAwait(false);
            var tenants = await this.database.ReadTenantsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var tenantId in tenants)
            {
                // Recorded before loading: a load that fails halfway leaves a partially filled partition to discard.
                loaded.Add(tenantId);
                var watermark = await this.database.LoadAsync(this.ledger.GetPartition(tenantId), cancellationToken).ConfigureAwait(false);
                this.LogTenantLoaded(tenantId, watermark);
            }

            return tenants.Count;
        }

        /// <summary>
        /// Drops every partition an attempt touched: a half-loaded tenant must never serve, and the next attempt needs empty
        /// partitions.
        /// </summary>
        private void Discard(List<Guid> loaded)
        {
            foreach (var tenantId in loaded)
            {
                this.ledger.DiscardPartition(tenantId);
            }
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Loaded tenant {TenantId} through sequence {Watermark}.")]
        private partial void LogTenantLoaded(Guid tenantId, long watermark);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading the tenants failed (attempt {Attempt}); every tenant loaded so far was discarded, and loading starts over in {Delay}.")]
        private partial void LogRetry(Exception exception, int attempt, TimeSpan delay);

        [LoggerMessage(Level = LogLevel.Critical, Message = "Loading the tenants failed on every attempt ({Attempts}); the server will not start.")]
        private partial void LogFailed(Exception exception, int attempts);

        [LoggerMessage(Level = LogLevel.Information, Message = "Recovered {TenantCount} tenant(s) in {Milliseconds:F0} ms.")]
        private partial void LogRecovered(int tenantCount, double milliseconds);
    }
}
