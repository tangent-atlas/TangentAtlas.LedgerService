// <copyright file="ILedgerDatabase.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Persistence
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model.Primary;

    /// <summary>
    /// The durable store behind the ledger: the flusher writes cuts to it, and recovery loads every tenant from it at startup.
    /// </summary>
    public interface ILedgerDatabase
    {
        /// <summary>
        /// Prepares the store once at startup, before anything is read or written.
        /// </summary>
        /// <param name="cancellationToken">Cancels the work.</param>
        /// <returns>A task that completes when the store is ready.</returns>
        Task PrepareAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Reads the tenants the store holds.
        /// </summary>
        /// <param name="cancellationToken">Cancels the work.</param>
        /// <returns>The tenants.</returns>
        Task<IReadOnlyList<Guid>> ReadTenantsAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Loads one tenant into its empty partition, parents first, and resumes the partition after the tenant's watermark.
        /// </summary>
        /// <param name="partition">The tenant's empty partition.</param>
        /// <param name="cancellationToken">Cancels the work.</param>
        /// <returns>The tenant's durable watermark.</returns>
        Task<long> LoadAsync(LedgerPartition partition, CancellationToken cancellationToken);

        /// <summary>
        /// Writes a cut and the tenant's watermark in one transaction.
        /// </summary>
        /// <param name="cut">The cut.</param>
        /// <param name="cancellationToken">Cancels the work.</param>
        /// <returns>A task that completes when the cut is durable.</returns>
        Task WriteAsync(LedgerCut cut, CancellationToken cancellationToken);
    }
}
