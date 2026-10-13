// <copyright file="InMemoryLedgerDatabase.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Persistence
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;

    /// <summary>
    /// Keeps the written rows in memory, with SQL Server's contract: the database when no connection string is configured
    /// (tests and quick runs).  A server rebuilt in the same process around the same instance recovers from it, which is how
    /// the tests exercise recovery without SQL Server; nothing survives the process.
    /// </summary>
    public sealed class InMemoryLedgerDatabase : ILedgerDatabase
    {
        private readonly Lock gate = new Lock();

        private readonly Dictionary<Guid, Dictionary<string, Dictionary<string, object>>> tenants = new Dictionary<Guid, Dictionary<string, Dictionary<string, object>>>();

        private readonly Dictionary<Guid, long> watermarks = new Dictionary<Guid, long>();

        /// <summary>
        /// Gets the number of cuts written.
        /// </summary>
        public int CutCount { get; private set; }

        /// <inheritdoc/>
        public Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc/>
        public Task<IReadOnlyList<Guid>> ReadTenantsAsync(CancellationToken cancellationToken)
        {
            lock (this.gate)
            {
                return Task.FromResult<IReadOnlyList<Guid>>(this.watermarks.Keys.OrderBy(tenantId => tenantId).ToList());
            }
        }

        /// <inheritdoc/>
        public Task<long> LoadAsync(LedgerPartition partition, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(partition);
            List<object> rows;
            long watermark;
            lock (this.gate)
            {
                watermark = this.watermarks.GetValueOrDefault(partition.TenantId);
                rows = this.tenants.TryGetValue(partition.TenantId, out var tables)
                    ? Ledger.TablesInDependencyOrder.SelectMany(table => tables[table].Values).ToList()
                    : new List<object>();
            }

            // Parents first, as the SQL loader reads them.
            partition.BeginLoad();
            foreach (var row in rows)
            {
                partition.LoadRow(row);
            }

            partition.CompleteLoad(watermark);
            return Task.FromResult(watermark);
        }

        /// <inheritdoc/>
        public Task WriteAsync(LedgerCut cut, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(cut);
            lock (this.gate)
            {
                if (!this.tenants.TryGetValue(cut.TenantId, out var tables))
                {
                    this.tenants[cut.TenantId] = tables = Ledger.TablesInDependencyOrder.ToDictionary(table => table, _ => new Dictionary<string, object>(StringComparer.Ordinal));
                }

                Apply(tables["Node"], cut.ChangedNodes, cut.DeletedNodes, dto => dto.NodeId.ToString());
                Apply(tables["Account"], cut.ChangedAccounts, cut.DeletedAccounts, dto => dto.AccountId.ToString());
                Apply(tables["Alert"], cut.ChangedAlerts, cut.DeletedAlerts, dto => dto.AlertId.ToString());
                Apply(tables["Edge"], cut.ChangedEdges, cut.DeletedEdges, dto => $"{dto.ParentNodeId}/{dto.ChildNodeId}");
                Apply(tables["Person"], cut.ChangedPersons, cut.DeletedPersons, dto => dto.PersonId.ToString());
                Apply(tables["Dossier"], cut.ChangedDossiers, cut.DeletedDossiers, dto => dto.PersonId.ToString());
                Apply(tables["Symbol"], cut.ChangedSymbols, cut.DeletedSymbols, dto => $"{dto.Scheme}/{dto.Code}");
                Apply(tables["User"], cut.ChangedUsers, cut.DeletedUsers, dto => dto.UserId.ToString());
                Apply(tables["Member"], cut.ChangedMembers, cut.DeletedMembers, dto => dto.UserId.ToString());
                this.watermarks[cut.TenantId] = Math.Max(cut.Sequence, this.watermarks.GetValueOrDefault(cut.TenantId));
                this.CutCount++;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Gets a tenant's durable watermark.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <returns>The watermark, or zero if the tenant has none.</returns>
        public long WatermarkOf(Guid tenantId)
        {
            lock (this.gate)
            {
                return this.watermarks.GetValueOrDefault(tenantId);
            }
        }

        /// <summary>
        /// Reads a tenant's rows of one table, as the recovery loader will.
        /// </summary>
        /// <typeparam name="T">The table's DTO.</typeparam>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="table">The table.</param>
        /// <returns>The rows.</returns>
        public IReadOnlyList<T> Read<T>(Guid tenantId, string table)
        {
            lock (this.gate)
            {
                return this.tenants.TryGetValue(tenantId, out var tables) ? tables[table].Values.Cast<T>().ToList() : new List<T>();
            }
        }

        private static void Apply<T>(Dictionary<string, object> rows, IReadOnlyList<T> changed, IReadOnlyList<T> deleted, Func<T, string> key)
            where T : notnull
        {
            foreach (var dto in changed)
            {
                rows[key(dto)] = dto;
            }

            foreach (var dto in deleted)
            {
                rows.Remove(key(dto));
            }
        }
    }
}
