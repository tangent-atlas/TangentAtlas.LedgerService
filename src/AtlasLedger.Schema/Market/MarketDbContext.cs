// <copyright file="MarketDbContext.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Schema.Market
{
    using System;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.EntityFrameworkCore.Design;

    /// <summary>
    /// The market history kept in our own database, in the <c>market</c> schema: the vendor's daily closes, cash dividends
    /// and splits by ticker, and the risk-free rate, as fetched.  Public data licensed to Tangent Atlas, the same for every
    /// tenant, so these tables have no <c>TenantId</c>.  Only daily and historical values are kept; live prices and anything
    /// derived from them never are.  Entity Framework creates and evolves the tables; the server reads and writes them with its
    /// own SQL.
    /// </summary>
    public sealed class MarketDbContext : DbContext
    {
        /// <summary>The schema the tables are in.</summary>
        public const string Schema = "market";

        /// <summary>
        /// Initializes a new instance of the <see cref="MarketDbContext"/> class.
        /// </summary>
        /// <param name="options">The options.</param>
        public MarketDbContext(DbContextOptions<MarketDbContext> options)
            : base(options)
        {
        }

        /// <summary>Gets the closes.</summary>
        public DbSet<MarketClose> Closes => this.Set<MarketClose>();

        /// <summary>Gets the dividends.</summary>
        public DbSet<MarketDividend> Dividends => this.Set<MarketDividend>();

        /// <summary>Gets the splits.</summary>
        public DbSet<MarketSplit> Splits => this.Set<MarketSplit>();

        /// <summary>Gets the rates.</summary>
        public DbSet<MarketRate> Rates => this.Set<MarketRate>();

        /// <summary>Gets what has been fetched of each series.</summary>
        public DbSet<MarketSeries> Series => this.Set<MarketSeries>();

        /// <inheritdoc/>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ArgumentNullException.ThrowIfNull(modelBuilder);
            modelBuilder.HasDefaultSchema(Schema);

            modelBuilder.Entity<MarketClose>(table =>
            {
                table.ToTable("Close");
                table.HasKey(row => new { row.Ticker, row.Date });
                table.Property(row => row.Ticker).HasMaxLength(32);
                table.Property(row => row.Close).HasPrecision(18, 6);
            });

            modelBuilder.Entity<MarketDividend>(table =>
            {
                table.ToTable("Dividend");
                table.HasKey(row => new { row.Ticker, row.ExDate });
                table.Property(row => row.Ticker).HasMaxLength(32);
                table.Property(row => row.Currency).HasMaxLength(3);
                table.Property(row => row.Amount).HasPrecision(18, 6);
            });

            modelBuilder.Entity<MarketSplit>(table =>
            {
                table.ToTable("Split");
                table.HasKey(row => new { row.Ticker, row.Date });
                table.Property(row => row.Ticker).HasMaxLength(32);
                table.Property(row => row.Ratio).HasPrecision(18, 6);
            });

            modelBuilder.Entity<MarketRate>(table =>
            {
                table.ToTable("Rate");
                table.HasKey(row => new { row.Code, row.Date });
                table.Property(row => row.Code).HasMaxLength(32);
                table.Property(row => row.Rate).HasPrecision(9, 6);
            });

            modelBuilder.Entity<MarketSeries>(table =>
            {
                table.ToTable("Series");
                table.HasKey(row => new { row.Kind, row.Code });
                table.Property(row => row.Code).HasMaxLength(32);
            });
        }
    }

    /// <summary>A ticker's official close on a market day, as traded that day (not adjusted).</summary>
    public sealed class MarketClose
    {
        /// <summary>Gets or sets the ticker (SPY).</summary>
        public string Ticker { get; set; } = string.Empty;

        /// <summary>Gets or sets the market day.</summary>
        public DateOnly Date { get; set; }

        /// <summary>Gets or sets the close, in US dollars.</summary>
        public decimal Close { get; set; }
    }

    /// <summary>A cash dividend per share, to holders at the close before its ex-date.</summary>
    public sealed class MarketDividend
    {
        /// <summary>Gets or sets the ticker.</summary>
        public string Ticker { get; set; } = string.Empty;

        /// <summary>Gets or sets the ex-date.</summary>
        public DateOnly ExDate { get; set; }

        /// <summary>Gets or sets the payment date, when known.</summary>
        public DateOnly? PaymentDate { get; set; }

        /// <summary>Gets or sets the amount per share.</summary>
        public decimal Amount { get; set; }

        /// <summary>Gets or sets the currency (ISO 4217).</summary>
        public string Currency { get; set; } = string.Empty;
    }

    /// <summary>A split effective at the open of its date: each share becomes Ratio shares.</summary>
    public sealed class MarketSplit
    {
        /// <summary>Gets or sets the ticker.</summary>
        public string Ticker { get; set; } = string.Empty;

        /// <summary>Gets or sets the date.</summary>
        public DateOnly Date { get; set; }

        /// <summary>Gets or sets the ratio (2 for 2-for-1).</summary>
        public decimal Ratio { get; set; }
    }

    /// <summary>A published interest rate for a day, a fraction a year (the 13-week Treasury bill, TBILL-13W).</summary>
    public sealed class MarketRate
    {
        /// <summary>Gets or sets the rate's code.</summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>Gets or sets the day.</summary>
        public DateOnly Date { get; set; }

        /// <summary>Gets or sets the rate.</summary>
        public decimal Rate { get; set; }
    }

    /// <summary>
    /// What has been fetched of one series (a ticker's closes, dividends or splits, or a rate): every day from From through
    /// Through has been asked of the vendor, and what it had is stored.  FetchedAt is the last fetch; VerifiedAt the last
    /// time the whole series was fetched again to catch the vendor's corrections.
    /// </summary>
    public sealed class MarketSeries
    {
        /// <summary>Gets or sets the kind of series: 0 closes, 1 dividends, 2 splits, 3 rates (a number, as every enum is stored).</summary>
        public int Kind { get; set; }

        /// <summary>Gets or sets the ticker or rate code.</summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>Gets or sets the first day asked for.</summary>
        public DateOnly From { get; set; }

        /// <summary>Gets or sets the last day the vendor had (closes), or the day it was last asked (the rest).</summary>
        public DateOnly Through { get; set; }

        /// <summary>Gets or sets when it was last fetched.</summary>
        public DateTimeOffset FetchedAt { get; set; }

        /// <summary>Gets or sets when the whole series was last fetched again.</summary>
        public DateTimeOffset VerifiedAt { get; set; }
    }

    /// <summary>
    /// Creates the market context for the Entity Framework tools (<c>dotnet ef ... --context MarketDbContext</c>), from the
    /// same connection as the ledger's schema.
    /// </summary>
    public sealed class MarketDbContextFactory : IDesignTimeDbContextFactory<MarketDbContext>
    {
        /// <summary>
        /// Creates a context for a database.
        /// </summary>
        /// <param name="connectionString">The database's connection string.</param>
        /// <returns>The context.</returns>
        public static MarketDbContext Create(string connectionString)
        {
            ArgumentException.ThrowIfNullOrEmpty(connectionString);
            var options = new DbContextOptionsBuilder<MarketDbContext>()
                .UseSqlServer(connectionString, sql => sql
                    .MigrationsAssembly(typeof(MarketDbContextFactory).Assembly.GetName().Name)
                    .MigrationsHistoryTable("__EFMigrationsHistory", MarketDbContext.Schema))
                .Options;
            return new MarketDbContext(options);
        }

        /// <inheritdoc/>
        public MarketDbContext CreateDbContext(string[] args) =>
            Create(Environment.GetEnvironmentVariable(LedgerDbContextFactory.ConnectionVariable) is { Length: > 0 } connection ? connection : LedgerDbContextFactory.DevelopmentConnection);
    }
}
