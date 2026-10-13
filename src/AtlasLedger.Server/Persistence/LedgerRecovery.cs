// <copyright file="LedgerRecovery.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Persistence
{
    using System;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Loads every tenant from the database at startup, before the server accepts traffic and before the flusher and the
    /// compliance engine start.  Registered as the first hosted service: the host starts hosted services in order, each
    /// finishing its start before the next, and the web server last.  If any tenant fails to load, the server does not start.
    /// </summary>
    internal sealed partial class LedgerRecovery : IHostedService
    {
        private readonly Ledger ledger;

        private readonly ILedgerDatabase database;

        private readonly ILogger<LedgerRecovery> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="LedgerRecovery"/> class.
        /// </summary>
        /// <param name="ledger">The in-memory ledger, empty at startup.</param>
        /// <param name="database">The database.</param>
        /// <param name="logger">The logger.</param>
        public LedgerRecovery(Ledger ledger, ILedgerDatabase database, ILogger<LedgerRecovery> logger)
        {
            this.ledger = ledger;
            this.database = database;
            this.logger = logger;
        }

        /// <inheritdoc/>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var watch = Stopwatch.StartNew();
            await this.database.PrepareAsync(cancellationToken).ConfigureAwait(false);
            var tenants = await this.database.ReadTenantsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var tenantId in tenants)
            {
                var partition = this.ledger.GetPartition(tenantId);
                try
                {
                    var watermark = await this.database.LoadAsync(partition, cancellationToken).ConfigureAwait(false);
                    this.LogTenantLoaded(tenantId, watermark);
                }
                catch (Exception exception)
                {
                    // A half-loaded partition must never serve: drop it, and let the server fail to start.
                    this.ledger.DiscardPartition(tenantId);
                    this.LogTenantFailed(exception, tenantId);
                    throw;
                }
            }

            this.LogRecovered(tenants.Count, watch.Elapsed.TotalMilliseconds);
        }

        /// <inheritdoc/>
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        [LoggerMessage(Level = LogLevel.Information, Message = "Loaded tenant {TenantId} through sequence {Watermark}.")]
        private partial void LogTenantLoaded(Guid tenantId, long watermark);

        [LoggerMessage(Level = LogLevel.Critical, Message = "Tenant {TenantId} failed to load; the server will not start.")]
        private partial void LogTenantFailed(Exception exception, Guid tenantId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Recovered {TenantCount} tenant(s) in {Milliseconds:F0} ms.")]
        private partial void LogRecovered(int tenantCount, double milliseconds);
    }
}
