// <copyright file="MarketHistoryStore.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Market
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;

    /// <summary>The kinds of series the market history store keeps (stored as numbers).</summary>
    public enum MarketSeriesKind
    {
        /// <summary>A ticker's daily closes.</summary>
        Closes = 0,

        /// <summary>A ticker's cash dividends.</summary>
        Dividends = 1,

        /// <summary>A ticker's splits.</summary>
        Splits = 2,

        /// <summary>A published interest rate (the risk-free rate).</summary>
        Rates = 3,
    }

    /// <summary>
    /// What has been fetched of a series: every day from From through Through has been asked of the vendor and what it had
    /// is stored.  FetchedAt is the last fetch; VerifiedAt the last time the whole series was fetched again (to catch the
    /// vendor's corrections).
    /// </summary>
    public sealed record SeriesCoverage(DateOnly From, DateOnly Through, DateTimeOffset FetchedAt, DateTimeOffset VerifiedAt);

    /// <summary>
    /// The market history kept in our own database: the vendor's daily closes, dividends and splits by ticker, and the
    /// risk-free rate, the same for every tenant.  Read at start-up instead of the vendor; written with what is fetched.
    /// </summary>
    public interface IMarketHistoryStore
    {
        /// <summary>What has been fetched of a series; null if nothing has.</summary>
        Task<SeriesCoverage?> GetCoverageAsync(MarketSeriesKind kind, string code, CancellationToken cancellationToken);

        /// <summary>A ticker's closes from one day through another, oldest first.</summary>
        Task<IReadOnlyList<DailyClose>> ReadClosesAsync(string ticker, DateOnly from, DateOnly through, CancellationToken cancellationToken);

        /// <summary>A ticker's dividends with an ex-date from a day on, oldest first.</summary>
        Task<IReadOnlyList<CashDividend>> ReadDividendsAsync(string ticker, DateOnly from, CancellationToken cancellationToken);

        /// <summary>A ticker's splits from a day on, oldest first.</summary>
        Task<IReadOnlyList<StockSplit>> ReadSplitsAsync(string ticker, DateOnly from, CancellationToken cancellationToken);

        /// <summary>A rate from a day on, oldest first.</summary>
        Task<IReadOnlyList<DailyRate>> ReadRatesAsync(string code, DateOnly from, CancellationToken cancellationToken);

        /// <summary>
        /// Stores what was fetched of a series, and its coverage, in one transaction: new rows inserted, changed rows
        /// updated, and, when <paramref name="replace"/> is given (a whole range fetched again), rows in that range the
        /// vendor no longer has deleted.
        /// </summary>
        Task SaveAsync(MarketSeriesKind kind, string code, MarketRows rows, SeriesCoverage coverage, (DateOnly From, DateOnly Through)? replace, CancellationToken cancellationToken);
    }

    /// <summary>The rows of one fetch, of one kind (the others empty).</summary>
    public sealed record MarketRows(
        IReadOnlyList<DailyClose>? Closes = null,
        IReadOnlyList<CashDividend>? Dividends = null,
        IReadOnlyList<StockSplit>? Splits = null,
        IReadOnlyList<DailyRate>? Rates = null);

    /// <summary>
    /// The store in memory, for the in-memory ledger and the tests: kept for the life of the process, so a restart fetches
    /// again (as the ledger did before history was stored).
    /// </summary>
    public sealed class InMemoryMarketHistoryStore : IMarketHistoryStore
    {
        private readonly Lock gate = new();
        private readonly Dictionary<(MarketSeriesKind, string), SeriesCoverage> coverage = new();
        private readonly Dictionary<(MarketSeriesKind, string), SortedDictionary<DateOnly, object>> rows = new();

        /// <summary>Gets how many saves have been made (for tests).</summary>
        public int Saves { get; private set; }

        /// <inheritdoc/>
        public Task<SeriesCoverage?> GetCoverageAsync(MarketSeriesKind kind, string code, CancellationToken cancellationToken)
        {
            lock (this.gate)
            {
                return Task.FromResult(this.coverage.TryGetValue((kind, Key(code)), out var found) ? found : null);
            }
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<DailyClose>> ReadClosesAsync(string ticker, DateOnly from, DateOnly through, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DailyClose>>(this.Read<DailyClose>(MarketSeriesKind.Closes, ticker, from).Where(close => close.Date <= through).ToArray());

        /// <inheritdoc/>
        public Task<IReadOnlyList<CashDividend>> ReadDividendsAsync(string ticker, DateOnly from, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CashDividend>>(this.Read<CashDividend>(MarketSeriesKind.Dividends, ticker, from));

        /// <inheritdoc/>
        public Task<IReadOnlyList<StockSplit>> ReadSplitsAsync(string ticker, DateOnly from, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StockSplit>>(this.Read<StockSplit>(MarketSeriesKind.Splits, ticker, from));

        /// <inheritdoc/>
        public Task<IReadOnlyList<DailyRate>> ReadRatesAsync(string code, DateOnly from, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DailyRate>>(this.Read<DailyRate>(MarketSeriesKind.Rates, code, from));

        /// <inheritdoc/>
        public Task SaveAsync(MarketSeriesKind kind, string code, MarketRows rows, SeriesCoverage coverage, (DateOnly From, DateOnly Through)? replace, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(rows);
            lock (this.gate)
            {
                var key = (kind, Key(code));
                if (!this.rows.TryGetValue(key, out var series))
                {
                    series = new SortedDictionary<DateOnly, object>();
                    this.rows[key] = series;
                }

                var items = MarketRowDates.Of(rows).ToArray();
                if (replace is { } range)
                {
                    var kept = items.Select(item => item.Date).ToHashSet();
                    foreach (var date in series.Keys.Where(date => date >= range.From && date <= range.Through && !kept.Contains(date)).ToArray())
                    {
                        series.Remove(date);
                    }
                }

                foreach (var (date, item) in items)
                {
                    series[date] = item;
                }

                this.coverage[key] = coverage;
                this.Saves++;
            }

            return Task.CompletedTask;
        }

        private static string Key(string code) => code.ToUpperInvariant();

        private T[] Read<T>(MarketSeriesKind kind, string code, DateOnly from)
        {
            lock (this.gate)
            {
                return this.rows.TryGetValue((kind, Key(code)), out var series)
                    ? series.Where(pair => pair.Key >= from).Select(pair => (T)pair.Value).ToArray()
                    : [];
            }
        }
    }

    /// <summary>The date each kind of row is keyed by.</summary>
    internal static class MarketRowDates
    {
        public static IEnumerable<(DateOnly Date, object Item)> Of(MarketRows rows) =>
            (rows.Closes ?? []).Select(item => (item.Date, (object)item))
                .Concat((rows.Dividends ?? []).Select(item => (item.ExDate, (object)item)))
                .Concat((rows.Splits ?? []).Select(item => (item.Date, (object)item)))
                .Concat((rows.Rates ?? []).Select(item => (item.Date, (object)item)))
                .GroupBy(pair => pair.Item1)
                .Select(group => group.Last());
    }

    /// <summary>
    /// The store in the ledger's SQL database, in the <c>market</c> schema (created by the schema's MarketDbContext
    /// migrations).  Reads are plain queries; a save bulk-copies the rows into a temporary table, then updates what changed
    /// and inserts what is new (no MERGE), and records the coverage, in one transaction.
    /// </summary>
    public sealed class SqlMarketHistoryStore : IMarketHistoryStore
    {
        private static readonly Dictionary<MarketSeriesKind, Shape> Shapes = new()
        {
            [MarketSeriesKind.Closes] = new("market.[Close]", "Ticker", "Date", [("Close", "decimal(18,6)")]),
            [MarketSeriesKind.Dividends] = new("market.Dividend", "Ticker", "ExDate", [("Amount", "decimal(18,6)"), ("PaymentDate", "date NULL"), ("Currency", "nvarchar(3)")]),
            [MarketSeriesKind.Splits] = new("market.Split", "Ticker", "Date", [("Ratio", "decimal(18,6)")]),
            [MarketSeriesKind.Rates] = new("market.Rate", "Code", "Date", [("Rate", "decimal(9,6)")]),
        };

        private readonly string connectionString;

        /// <summary>
        /// Initializes a new instance of the <see cref="SqlMarketHistoryStore"/> class.
        /// </summary>
        /// <param name="connectionString">The ledger database's connection string.</param>
        public SqlMarketHistoryStore(string connectionString)
        {
            ArgumentException.ThrowIfNullOrEmpty(connectionString);
            this.connectionString = connectionString;
        }

        /// <inheritdoc/>
        public async Task<SeriesCoverage?> GetCoverageAsync(MarketSeriesKind kind, string code, CancellationToken cancellationToken)
        {
            await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new SqlCommand("SELECT [From], Through, FetchedAt, VerifiedAt FROM market.Series WHERE Kind = @kind AND Code = @code;", connection);
            command.Parameters.Add("@kind", SqlDbType.Int).Value = (int)kind;
            command.Parameters.Add("@code", SqlDbType.NVarChar, 32).Value = code;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? new SeriesCoverage(Day(reader, 0), Day(reader, 1), reader.GetDateTimeOffset(2), reader.GetDateTimeOffset(3))
                : null;
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<DailyClose>> ReadClosesAsync(string ticker, DateOnly from, DateOnly through, CancellationToken cancellationToken) =>
            this.ReadAsync(
                "SELECT [Date], [Close] FROM market.[Close] WHERE Ticker = @code AND [Date] >= @from AND [Date] <= @through ORDER BY [Date];",
                ticker,
                from,
                through,
                reader => new DailyClose(Day(reader, 0), reader.GetDecimal(1)),
                cancellationToken);

        /// <inheritdoc/>
        public Task<IReadOnlyList<CashDividend>> ReadDividendsAsync(string ticker, DateOnly from, CancellationToken cancellationToken) =>
            this.ReadAsync(
                "SELECT ExDate, PaymentDate, Amount, Currency FROM market.Dividend WHERE Ticker = @code AND ExDate >= @from ORDER BY ExDate;",
                ticker,
                from,
                null,
                reader => new CashDividend(Day(reader, 0), reader.IsDBNull(1) ? null : Day(reader, 1), reader.GetDecimal(2), reader.GetString(3)),
                cancellationToken);

        /// <inheritdoc/>
        public Task<IReadOnlyList<StockSplit>> ReadSplitsAsync(string ticker, DateOnly from, CancellationToken cancellationToken) =>
            this.ReadAsync(
                "SELECT [Date], Ratio FROM market.Split WHERE Ticker = @code AND [Date] >= @from ORDER BY [Date];",
                ticker,
                from,
                null,
                reader => new StockSplit(Day(reader, 0), reader.GetDecimal(1)),
                cancellationToken);

        /// <inheritdoc/>
        public Task<IReadOnlyList<DailyRate>> ReadRatesAsync(string code, DateOnly from, CancellationToken cancellationToken) =>
            this.ReadAsync(
                "SELECT [Date], Rate FROM market.Rate WHERE Code = @code AND [Date] >= @from ORDER BY [Date];",
                code,
                from,
                null,
                reader => new DailyRate(Day(reader, 0), reader.GetDecimal(1)),
                cancellationToken);

        /// <inheritdoc/>
        public async Task SaveAsync(MarketSeriesKind kind, string code, MarketRows rows, SeriesCoverage coverage, (DateOnly From, DateOnly Through)? replace, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(rows);
            ArgumentNullException.ThrowIfNull(coverage);
            var shape = Shapes[kind];
            await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // The fetched rows, into a temporary table shaped like the target.
            var columns = string.Join(", ", shape.Values.Select(value => $"[{value.Name}] {value.Type}"));
            await ExecuteAsync(connection, transaction, $"CREATE TABLE #Rows ([{shape.DateColumn}] date NOT NULL PRIMARY KEY, {columns});", null, cancellationToken).ConfigureAwait(false);
            var table = Table(kind, rows);
            if (table.Rows.Count > 0)
            {
                using var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction) { DestinationTableName = "#Rows" };
                foreach (DataColumn column in table.Columns)
                {
                    copy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                }

                await copy.WriteToServerAsync(table, cancellationToken).ConfigureAwait(false);
            }

            // Update what changed, insert what is new, and (a whole range fetched again) delete what the vendor no longer has.
            var set = string.Join(", ", shape.Values.Select(value => $"t.[{value.Name}] = r.[{value.Name}]"));
            var differs = string.Join(" OR ", shape.Values.Select(value => $"NOT (t.[{value.Name}] = r.[{value.Name}] OR (t.[{value.Name}] IS NULL AND r.[{value.Name}] IS NULL))"));
            var names = string.Join(", ", shape.Values.Select(value => $"[{value.Name}]"));
            var sql = $"""
                UPDATE t SET {set} FROM {shape.Table} t JOIN #Rows r ON t.[{shape.CodeColumn}] = @code AND t.[{shape.DateColumn}] = r.[{shape.DateColumn}] WHERE {differs};
                INSERT INTO {shape.Table} ([{shape.CodeColumn}], [{shape.DateColumn}], {names})
                    SELECT @code, r.[{shape.DateColumn}], {string.Join(", ", shape.Values.Select(value => $"r.[{value.Name}]"))} FROM #Rows r
                    WHERE NOT EXISTS (SELECT 1 FROM {shape.Table} t WHERE t.[{shape.CodeColumn}] = @code AND t.[{shape.DateColumn}] = r.[{shape.DateColumn}]);
                """;
            if (replace is not null)
            {
                sql += $"""

                    DELETE t FROM {shape.Table} t WHERE t.[{shape.CodeColumn}] = @code AND t.[{shape.DateColumn}] >= @replaceFrom AND t.[{shape.DateColumn}] <= @replaceThrough
                        AND NOT EXISTS (SELECT 1 FROM #Rows r WHERE r.[{shape.DateColumn}] = t.[{shape.DateColumn}]);
                    """;
            }

            sql += """

                UPDATE market.Series SET [From] = @from, Through = @through, FetchedAt = @fetchedAt, VerifiedAt = @verifiedAt WHERE Kind = @kind AND Code = @code;
                IF @@ROWCOUNT = 0 INSERT INTO market.Series (Kind, Code, [From], Through, FetchedAt, VerifiedAt) VALUES (@kind, @code, @from, @through, @fetchedAt, @verifiedAt);
                DROP TABLE #Rows;
                """;
            await ExecuteAsync(
                connection,
                transaction,
                sql,
                parameters =>
                {
                    parameters.Add("@code", SqlDbType.NVarChar, 32).Value = code;
                    parameters.Add("@kind", SqlDbType.Int).Value = (int)kind;
                    parameters.Add("@from", SqlDbType.Date).Value = coverage.From.ToDateTime(TimeOnly.MinValue);
                    parameters.Add("@through", SqlDbType.Date).Value = coverage.Through.ToDateTime(TimeOnly.MinValue);
                    parameters.Add("@fetchedAt", SqlDbType.DateTimeOffset).Value = coverage.FetchedAt;
                    parameters.Add("@verifiedAt", SqlDbType.DateTimeOffset).Value = coverage.VerifiedAt;
                    if (replace is { } range)
                    {
                        parameters.Add("@replaceFrom", SqlDbType.Date).Value = range.From.ToDateTime(TimeOnly.MinValue);
                        parameters.Add("@replaceThrough", SqlDbType.Date).Value = range.Through.ToDateTime(TimeOnly.MinValue);
                    }
                },
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        private static DateOnly Day(SqlDataReader reader, int ordinal) => DateOnly.FromDateTime(reader.GetDateTime(ordinal));

        private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, Action<SqlParameterCollection>? parameters, CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(sql, connection, transaction);
            parameters?.Invoke(command.Parameters);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The rows as a table for the bulk copy: the date column, then the kind's values.</summary>
        private static DataTable Table(MarketSeriesKind kind, MarketRows rows)
        {
            var shape = Shapes[kind];
            var table = new DataTable();
            table.Columns.Add(shape.DateColumn, typeof(DateTime));
            foreach (var (name, type) in shape.Values)
            {
                table.Columns.Add(name, type.StartsWith("decimal", StringComparison.Ordinal) ? typeof(decimal) : type.StartsWith("date", StringComparison.Ordinal) ? typeof(DateTime) : typeof(string));
            }

            foreach (var (date, item) in MarketRowDates.Of(rows))
            {
                object[] values = item switch
                {
                    DailyClose close => [close.Close],
                    CashDividend dividend => [dividend.Amount, dividend.PaymentDate is { } paid ? paid.ToDateTime(TimeOnly.MinValue) : DBNull.Value, dividend.Currency],
                    StockSplit split => [split.Ratio],
                    DailyRate rate => [rate.Rate],
                    _ => throw new InvalidOperationException($"{item.GetType().Name} is not a market history row."),
                };
                table.Rows.Add([date.ToDateTime(TimeOnly.MinValue), .. values]);
            }

            return table;
        }

        private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
        {
            var connection = new SqlConnection(this.connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }

        private async Task<IReadOnlyList<T>> ReadAsync<T>(string sql, string code, DateOnly from, DateOnly? through, Func<SqlDataReader, T> read, CancellationToken cancellationToken)
        {
            await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@code", SqlDbType.NVarChar, 32).Value = code;
            command.Parameters.Add("@from", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
            if (through is { } last)
            {
                command.Parameters.Add("@through", SqlDbType.Date).Value = last.ToDateTime(TimeOnly.MinValue);
            }

            var items = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(read(reader));
            }

            return items;
        }

        private sealed record Shape(string Table, string CodeColumn, string DateColumn, IReadOnlyList<(string Name, string Type)> Values);
    }
}
