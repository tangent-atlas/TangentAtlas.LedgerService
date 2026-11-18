namespace AtlasLedger.LoadTest;

using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

/// <summary>
/// Clears the load test's tenants out of the ledger's database: every row of every tenant <c>ledger-load load</c> made
/// (their keys are <see cref="Tenants.IdOf"/> of an index), in every table with a TenantId, the watermark among them, in
/// one transaction.  Other tenants are not touched, unless every tenant is asked for.
/// </summary>
/// <remarks>
/// This is test-data housekeeping, done to the database and not through the ledger: the event log is append-only, so no
/// API deletes a tenant's history.  The ledger must be stopped first: it holds every tenant in memory from startup, and a
/// running ledger would go on serving (and could write back) what the database no longer has.  Start it again afterwards
/// and it loads what is left.
/// </remarks>
public static class LoadTestDatabase
{
    /// <summary>The largest load-test index looked for when finding the load test's tenants.</summary>
    public const int MaximumIndex = 100_000;

    /// <summary>
    /// Deletes tenants from the database.
    /// </summary>
    /// <param name="connectionString">The database.</param>
    /// <param name="everyTenant">True to delete every tenant, not only the load test's.</param>
    /// <param name="dryRun">True to count what would be deleted and delete nothing.</param>
    /// <param name="output">Where to report.</param>
    /// <returns>The number of tenants deleted (or that would be).</returns>
    public static async Task<int> ClearAsync(string connectionString, bool everyTenant, bool dryRun, TextWriter output)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var tables = await TenantTablesAsync(connection);
        var present = await TenantsPresentAsync(connection, tables);
        var loadTest = Enumerable.Range(0, MaximumIndex + 1).Select(Tenants.IdOf).ToHashSet();
        var doomed = present.Where(tenant => everyTenant || loadTest.Contains(tenant)).Order().ToArray();
        output.WriteLine($"{connection.DataSource}/{connection.Database}: {present.Count} tenant(s), {present.Count(loadTest.Contains)} of them the load test's.");
        if (doomed.Length == 0)
        {
            output.WriteLine("Nothing to delete.");
            return 0;
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        await ExecuteAsync(connection, transaction, "CREATE TABLE #Doomed (TenantId uniqueidentifier NOT NULL PRIMARY KEY);");
        foreach (var chunk in doomed.Chunk(500))
        {
            await using var insert = new SqlCommand($"INSERT INTO #Doomed (TenantId) VALUES {string.Join(", ", chunk.Select((_, index) => $"(@t{index})"))};", connection, transaction);
            for (var index = 0; index < chunk.Length; index++)
            {
                insert.Parameters.Add(new SqlParameter($"@t{index}", SqlDbType.UniqueIdentifier) { Value = chunk[index] });
            }

            await insert.ExecuteNonQueryAsync();
        }

        var total = 0L;
        foreach (var table in tables)
        {
            var sql = dryRun
                ? $"SELECT COUNT_BIG(*) FROM {table} WHERE TenantId IN (SELECT TenantId FROM #Doomed);"
                : $"DELETE FROM {table} WHERE TenantId IN (SELECT TenantId FROM #Doomed); SELECT CAST(@@ROWCOUNT AS bigint);";
            await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
            var rows = (long)(await command.ExecuteScalarAsync() ?? 0L);
            total += rows;
            if (rows > 0)
            {
                output.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-28} {1,12:#,0} row(s)", table, rows));
            }
        }

        if (dryRun)
        {
            await transaction.RollbackAsync();
            output.WriteLine(string.Format(CultureInfo.InvariantCulture, "Would delete {0:#,0} row(s) of {1} tenant(s). Nothing was deleted (--dry-run).", total, doomed.Length));
        }
        else
        {
            await transaction.CommitAsync();
            output.WriteLine(string.Format(CultureInfo.InvariantCulture, "Deleted {0:#,0} row(s) of {1} tenant(s).", total, doomed.Length));
        }

        return doomed.Length;
    }

    /// <summary>
    /// Whether a ledger server answers at an address: it must not, while its database is cleared.
    /// </summary>
    /// <param name="server">The server's address.</param>
    /// <returns>True if it answers.</returns>
    public static async Task<bool> IsServerRunningAsync(string server)
    {
        using var http = new HttpClient { BaseAddress = new Uri(server.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var response = await http.GetAsync("version");
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    // Every table that partitions by tenant, schema-qualified and quoted: the ledger's tables and Bellerophon's watermark.
    private static async Task<IReadOnlyList<string>> TenantTablesAsync(SqlConnection connection)
    {
        const string sql = """
            SELECT QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)
            FROM INFORMATION_SCHEMA.COLUMNS c
            JOIN INFORMATION_SCHEMA.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
            WHERE c.COLUMN_NAME = 'TenantId' AND t.TABLE_TYPE = 'BASE TABLE'
            ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        if (tables.Count == 0)
        {
            throw new InvalidOperationException($"{connection.Database} has no tables with a TenantId: is it the ledger's database, with its migrations applied?");
        }

        return tables;
    }

    private static async Task<HashSet<Guid>> TenantsPresentAsync(SqlConnection connection, IReadOnlyList<string> tables)
    {
        var sql = string.Join(" UNION ", tables.Select(table => $"SELECT TenantId FROM {table}")) + ";";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync();
        var tenants = new HashSet<Guid>();
        while (await reader.ReadAsync())
        {
            tenants.Add(reader.GetGuid(0));
        }

        return tenants;
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }
}
