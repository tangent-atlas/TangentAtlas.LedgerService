// <copyright file="TestDatabase.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Persistence.Tests
{
    using System;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Schema;
    using Microsoft.Data.SqlClient;
    using Microsoft.EntityFrameworkCore;
    using Xunit;

    /// <summary>
    /// A database of its own for one test: created on the test server, migrated to the current schema, and dropped when
    /// disposed.  Skips the test when no SQL Server can be reached.
    /// </summary>
    internal sealed class TestDatabase : IAsyncDisposable
    {
        /// <summary>
        /// The environment variable naming the test server: a connection string without a database.
        /// </summary>
        public const string ServerVariable = "BELLEROPHON_SQL_SERVER";

        private const string LocalDb = "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true";

        private readonly string server;

        private TestDatabase(string server, string name)
        {
            this.server = server;
            this.Name = name;
            this.ConnectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString;
        }

        /// <summary>
        /// Gets the database's name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the database's connection string.
        /// </summary>
        public string ConnectionString { get; }

        /// <summary>
        /// Creates a database for one test.
        /// </summary>
        /// <param name="migrate">Whether to apply the schema's migrations.</param>
        /// <param name="cancellationToken">Cancels the work.</param>
        /// <returns>The database.</returns>
        public static async Task<TestDatabase> CreateAsync(bool migrate = true, CancellationToken cancellationToken = default)
        {
            var server = Environment.GetEnvironmentVariable(ServerVariable) is { Length: > 0 } configured
                ? configured
                : OperatingSystem.IsWindows() ? LocalDb : null;
            if (server == null)
            {
                Assert.Skip($"No SQL Server for the database tests: set {ServerVariable} to a connection string without a database (LocalDB is used by default on Windows).");
            }

            var name = "BellerophonTest_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                await using var connection = new SqlConnection(new SqlConnectionStringBuilder(server) { InitialCatalog = "master", ConnectTimeout = 60 }.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE DATABASE [{name}];";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqlException exception)
            {
                Assert.Skip($"SQL Server could not be reached for the database tests ({exception.Message}). Set {ServerVariable} to a reachable server.");
            }

            var database = new TestDatabase(server, name);
            if (migrate)
            {
                await using var context = LedgerDbContextFactory.Create(database.ConnectionString);
                await context.Database.MigrateAsync(cancellationToken);
            }

            return database;
        }

        /// <summary>
        /// Runs one statement against the database.
        /// </summary>
        /// <param name="sql">The statement.</param>
        /// <param name="cancellationToken">Cancels the work.</param>
        /// <returns>The first column of the first row, if any.</returns>
        public async Task<object?> ExecuteAsync(string sql, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqlConnection(this.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is DBNull ? null : result;
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await using var connection = new SqlConnection(new SqlConnectionStringBuilder(this.server) { InitialCatalog = "master" }.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"IF DB_ID(N'{this.Name}') IS NOT NULL BEGIN ALTER DATABASE [{this.Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{this.Name}]; END";
            await command.ExecuteNonQueryAsync();
        }
    }
}
