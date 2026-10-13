// <copyright file="LedgerDbContextFactory.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Schema
{
    using System;
    using AtlasLedger.Model.Schema;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.EntityFrameworkCore.Design;

    /// <summary>
    /// Creates the schema context for the Entity Framework tools (<c>dotnet ef</c>), and for code that applies migrations.
    /// </summary>
    public sealed class LedgerDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
    {
        /// <summary>
        /// The environment variable the tools read the connection string from.
        /// </summary>
        public const string ConnectionVariable = "LEDGER_SCHEMA_CONNECTION";

        /// <summary>
        /// The connection string used when the environment variable is not set: a local development database.
        /// </summary>
        public const string DevelopmentConnection = "Server=(localdb)\\MSSQLLocalDB;Database=AtlasLedger;Integrated Security=true;TrustServerCertificate=true";

        /// <summary>
        /// Creates a context for a database.
        /// </summary>
        /// <param name="connectionString">The database's connection string.</param>
        /// <returns>The context.</returns>
        public static LedgerDbContext Create(string connectionString)
        {
            ArgumentException.ThrowIfNullOrEmpty(connectionString);
            var options = new DbContextOptionsBuilder<LedgerDbContext>()
                .UseSqlServer(connectionString, sql => sql.MigrationsAssembly(typeof(LedgerDbContextFactory).Assembly.GetName().Name))
                .Options;
            return new LedgerDbContext(options);
        }

        /// <inheritdoc/>
        public LedgerDbContext CreateDbContext(string[] args) =>
            Create(Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } connection ? connection : DevelopmentConnection);
    }
}
