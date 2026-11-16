// <copyright file="LedgerMirror.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Mirror
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Client.Api;
    using AtlasLedger.Model;

    /// <summary>
    /// Fills the client's local copy of a tenant (the Replica partition) from the server, so the client reads and navigates
    /// its data without a round trip: the generated sync client's snapshot, of every table the caller may read.
    /// </summary>
    public static class LedgerMirror
    {
        /// <summary>
        /// Brings the local copy of a tenant up to date with the server.
        /// </summary>
        /// <param name="api">The API, signed in as the caller whose view to mirror.</param>
        /// <param name="ledger">The local ledger.</param>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="cancellationToken">Cancels the refresh.</param>
        /// <returns>A task that completes when the copy is current.</returns>
        public static async Task RefreshAsync(LedgerApiClient api, Ledger ledger, Guid tenantId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(ledger);
            await api.Sync(ledger, tenantId).SyncAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        }
    }
}
