// <copyright file="SqlServerLedgerDatabase.cs" company="Tangent Atlas">
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
    /// The ledger's SQL Server database: the generated <see cref="LedgerSqlDatabase"/>, behind the server's interface.
    /// </summary>
    public sealed class SqlServerLedgerDatabase : ILedgerDatabase
    {
        private readonly LedgerSqlDatabase database;

        /// <summary>
        /// Initializes a new instance of the <see cref="SqlServerLedgerDatabase"/> class.
        /// </summary>
        /// <param name="connectionString">The database's connection string.</param>
        public SqlServerLedgerDatabase(string connectionString)
        {
            this.database = new LedgerSqlDatabase(connectionString);
        }

        /// <inheritdoc/>
        public Task PrepareAsync(CancellationToken cancellationToken) => this.database.PrepareAsync(cancellationToken);

        /// <inheritdoc/>
        public Task<IReadOnlyList<Guid>> ReadTenantsAsync(CancellationToken cancellationToken) => this.database.ReadTenantsAsync(cancellationToken);

        /// <inheritdoc/>
        public Task<long> LoadAsync(LedgerPartition partition, CancellationToken cancellationToken) => this.database.LoadAsync(partition, cancellationToken);

        /// <inheritdoc/>
        public Task WriteAsync(LedgerCut cut, CancellationToken cancellationToken) => this.database.WriteAsync(cut, cancellationToken);
    }
}
