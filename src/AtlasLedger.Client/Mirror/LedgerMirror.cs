// <copyright file="LedgerMirror.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Mirror
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Client.Api;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Replica;

    /// <summary>
    /// Fills the client's local copy of a tenant (the Replica partition) from the server, so the client reads and navigates
    /// its data without a round trip.
    /// </summary>
    /// <remarks>
    /// Today it refreshes the whole tenant, table by table, parents first.  The sync endpoint will replace this with "the
    /// changes since sequence N", applied the same way through the partition's <c>Apply</c> and <c>Remove</c> methods.
    /// </remarks>
    public static class LedgerMirror
    {
        /// <summary>
        /// Replaces the local copy of a tenant with the server's rows.
        /// </summary>
        /// <param name="api">The API, signed in as the caller whose view to mirror.</param>
        /// <param name="ledger">The local ledger.</param>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="cancellationToken">Cancels the refresh.</param>
        /// <returns>The tables the caller may not read, which the copy leaves empty.</returns>
        public static async Task<IReadOnlyList<string>> RefreshAsync(LedgerApiClient api, Ledger ledger, Guid tenantId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(ledger);

            // Read everything first, so the local copy changes all at once.
            var rows = new Dictionary<string, object>();
            var forbidden = new List<string>();
            foreach (var table in Ledger.TablesInDependencyOrder)
            {
                try
                {
                    rows[table] = table switch
                    {
                        "Node" => await api.ListAsync<NodeDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "Account" => await api.ListAsync<AccountDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "Alert" => await api.ListAsync<AlertDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "Edge" => await api.ListAsync<EdgeDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "Person" => await api.ListAsync<PersonDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "Symbol" => await api.ListAsync<SymbolDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "User" => await api.ListAsync<UserDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "Member" => await api.ListAsync<MemberDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        _ => throw new InvalidOperationException($"The mirror does not know the table {table}; update it with the model."),
                    };
                }
                catch (LedgerApiException exception) when (exception.StatusCode == HttpStatusCode.Forbidden)
                {
                    forbidden.Add(table);
                }
            }

            ledger.DiscardPartition(tenantId);
            var partition = ledger.GetPartition(tenantId);
            using var transaction = partition.BeginTransaction();
            foreach (var table in Ledger.TablesInDependencyOrder)
            {
                switch (rows.GetValueOrDefault(table))
                {
                    case IReadOnlyList<NodeDto> nodes: foreach (var dto in nodes) { transaction.ApplyNode(dto); } break;
                    case IReadOnlyList<AccountDto> accounts: foreach (var dto in accounts) { transaction.ApplyAccount(dto); } break;
                    case IReadOnlyList<AlertDto> alerts: foreach (var dto in alerts) { transaction.ApplyAlert(dto); } break;
                    case IReadOnlyList<EdgeDto> edges: foreach (var dto in edges) { transaction.ApplyEdge(dto); } break;
                    case IReadOnlyList<PersonDto> persons: foreach (var dto in persons) { transaction.ApplyPerson(dto); } break;
                    case IReadOnlyList<SymbolDto> symbols: foreach (var dto in symbols) { transaction.ApplySymbol(dto); } break;
                    case IReadOnlyList<UserDto> users: foreach (var dto in users) { transaction.ApplyUser(dto); } break;
                    case IReadOnlyList<MemberDto> members: foreach (var dto in members) { transaction.ApplyMember(dto); } break;
                }
            }

            transaction.Commit();
            return forbidden;
        }
    }
}
