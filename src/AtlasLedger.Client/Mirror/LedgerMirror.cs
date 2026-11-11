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
                        "AccountConnection" => await api.ListAsync<AccountConnectionDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "Asset" => await api.ListAsync<AssetDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "Cash" => await api.ListAsync<CashDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "Crypto" => await api.ListAsync<CryptoDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "Equity" => await api.ListAsync<EquityDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "FixedIncome" => await api.ListAsync<FixedIncomeDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "Fund" => await api.ListAsync<FundDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "Future" => await api.ListAsync<FutureDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "Option" => await api.ListAsync<OptionDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "PriceOverride" => await api.ListAsync<PriceOverrideDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "Order" => await api.ListAsync<OrderDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "Allocation" => await api.ListAsync<AllocationDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "BrokerOrder" => await api.ListAsync<BrokerOrderDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "ExecutionReport" => await api.ListAsync<ExecutionReportDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "Transaction" => await api.ListAsync<TransactionDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
                        "TransactionLine" => await api.ListAsync<TransactionLineDto>(tenantId, table, cancellationToken).ConfigureAwait(false),
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
                    case IReadOnlyList<AccountConnectionDto> accountConnections: foreach (var dto in accountConnections) { transaction.ApplyAccountConnection(dto); } break;
                    case IReadOnlyList<AssetDto> assets: foreach (var dto in assets) { transaction.ApplyAsset(dto); } break;
                    case IReadOnlyList<CashDto> cashAssets: foreach (var dto in cashAssets) { transaction.ApplyCash(dto); } break;
                    case IReadOnlyList<CryptoDto> cryptoAssets: foreach (var dto in cryptoAssets) { transaction.ApplyCrypto(dto); } break;
                    case IReadOnlyList<EquityDto> equities: foreach (var dto in equities) { transaction.ApplyEquity(dto); } break;
                    case IReadOnlyList<FixedIncomeDto> fixedIncomes: foreach (var dto in fixedIncomes) { transaction.ApplyFixedIncome(dto); } break;
                    case IReadOnlyList<FundDto> funds: foreach (var dto in funds) { transaction.ApplyFund(dto); } break;
                    case IReadOnlyList<FutureDto> futures: foreach (var dto in futures) { transaction.ApplyFuture(dto); } break;
                    case IReadOnlyList<OptionDto> options: foreach (var dto in options) { transaction.ApplyOption(dto); } break;
                    case IReadOnlyList<PriceOverrideDto> priceOverrides: foreach (var dto in priceOverrides) { transaction.ApplyPriceOverride(dto); } break;
                    case IReadOnlyList<OrderDto> orders: foreach (var dto in orders) { transaction.ApplyOrder(dto); } break;
                    case IReadOnlyList<AllocationDto> allocations: foreach (var dto in allocations) { transaction.ApplyAllocation(dto); } break;
                    case IReadOnlyList<BrokerOrderDto> brokerOrders: foreach (var dto in brokerOrders) { transaction.ApplyBrokerOrder(dto); } break;
                    case IReadOnlyList<ExecutionReportDto> executionReports: foreach (var dto in executionReports) { transaction.ApplyExecutionReport(dto); } break;
                    case IReadOnlyList<TransactionDto> transactions: foreach (var dto in transactions) { transaction.ApplyTransaction(dto); } break;
                    case IReadOnlyList<TransactionLineDto> transactionLines: foreach (var dto in transactionLines) { transaction.ApplyTransactionLine(dto); } break;
                }
            }

            transaction.Commit();
            return forbidden;
        }
    }
}
