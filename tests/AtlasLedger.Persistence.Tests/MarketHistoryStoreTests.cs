// <copyright file="MarketHistoryStoreTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias server;

namespace AtlasLedger.Persistence.Tests
{
    using System;
    using System.Linq;
    using System.Threading.Tasks;
    using AtlasLedger.Schema.Market;
    using Microsoft.EntityFrameworkCore;
    using server::AtlasLedger.Server.Market;
    using Xunit;

    /// <summary>
    /// The market history store in SQL Server (the market schema): what is saved is read back, a save corrects what changed,
    /// a whole range fetched again removes what the vendor no longer has, and the coverage is kept with the rows.
    /// </summary>
    public sealed class MarketHistoryStoreTests
    {
        private static readonly DateOnly Day = new(2026, 11, 2);

        private static readonly DateTimeOffset Now = new(2026, 11, 22, 23, 0, 0, TimeSpan.Zero);

        [Fact]
        public async Task ClosesAreSavedReadCorrectedAndReplaced()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var database = await TestDatabase.CreateAsync(cancellationToken: cancellationToken);
            await using (var context = MarketDbContextFactory.Create(database.ConnectionString))
            {
                await context.Database.MigrateAsync(cancellationToken);
            }

            var store = new SqlMarketHistoryStore(database.ConnectionString);
            Assert.Null(await store.GetCoverageAsync(MarketSeriesKind.Closes, "SPY", cancellationToken));

            var closes = Enumerable.Range(0, 5).Select(index => new DailyClose(Day.AddDays(index), 600m + index)).ToArray();
            var coverage = new SeriesCoverage(Day, Day.AddDays(4), Now, Now);
            await store.SaveAsync(MarketSeriesKind.Closes, "SPY", new MarketRows(Closes: closes), coverage, null, cancellationToken);
            Assert.Equal(closes, await store.ReadClosesAsync("SPY", Day, Day.AddDays(4), cancellationToken));
            Assert.Equal(coverage, await store.GetCoverageAsync(MarketSeriesKind.Closes, "SPY", cancellationToken));

            // A delta adds a day and corrects one; nothing else changes.
            var later = coverage with { Through = Day.AddDays(5), FetchedAt = Now.AddDays(1) };
            await store.SaveAsync(MarketSeriesKind.Closes, "SPY", new MarketRows(Closes: [new DailyClose(Day.AddDays(1), 650.5m), new DailyClose(Day.AddDays(5), 605m)]), later, null, cancellationToken);
            var read = await store.ReadClosesAsync("SPY", Day, Day.AddDays(5), cancellationToken);
            Assert.Equal([600m, 650.5m, 602m, 603m, 604m, 605m], read.Select(close => close.Close));
            Assert.Equal(later, await store.GetCoverageAsync(MarketSeriesKind.Closes, "SPY", cancellationToken));

            // The whole range fetched again without day 3: it goes.  Another ticker is untouched.
            await store.SaveAsync(MarketSeriesKind.Closes, "QQQ", new MarketRows(Closes: closes), coverage, null, cancellationToken);
            var again = read.Where(close => close.Date != Day.AddDays(3)).ToArray();
            await store.SaveAsync(MarketSeriesKind.Closes, "SPY", new MarketRows(Closes: again), later, (Day, Day.AddDays(5)), cancellationToken);
            Assert.Equal(again, await store.ReadClosesAsync("SPY", Day, Day.AddDays(5), cancellationToken));
            Assert.Equal(5, (await store.ReadClosesAsync("QQQ", Day, Day.AddDays(5), cancellationToken)).Count);
        }

        [Fact]
        public async Task DividendsSplitsAndRatesRoundTrip()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var database = await TestDatabase.CreateAsync(cancellationToken: cancellationToken);
            await using (var context = MarketDbContextFactory.Create(database.ConnectionString))
            {
                await context.Database.MigrateAsync(cancellationToken);
            }

            var store = new SqlMarketHistoryStore(database.ConnectionString);
            var coverage = new SeriesCoverage(Day, Day.AddDays(30), Now, Now);
            CashDividend[] dividends = [new(Day.AddDays(3), Day.AddDays(30), 1.75m, "USD"), new(Day.AddDays(20), null, 0.25m, "USD")];
            await store.SaveAsync(MarketSeriesKind.Dividends, "SPY", new MarketRows(Dividends: dividends), coverage, null, cancellationToken);
            await store.SaveAsync(MarketSeriesKind.Splits, "NVDA", new MarketRows(Splits: [new StockSplit(Day.AddDays(7), 10m)]), coverage, null, cancellationToken);
            await store.SaveAsync(MarketSeriesKind.Rates, "TBILL-13W", new MarketRows(Rates: [new DailyRate(Day, 0.0405m), new DailyRate(Day.AddDays(1), 0.0407m)]), coverage, null, cancellationToken);

            Assert.Equal(dividends, await store.ReadDividendsAsync("SPY", Day, cancellationToken));
            Assert.Equal([new StockSplit(Day.AddDays(7), 10m)], await store.ReadSplitsAsync("NVDA", Day, cancellationToken));
            Assert.Equal([0.0407m], (await store.ReadRatesAsync("TBILL-13W", Day.AddDays(1), cancellationToken)).Select(rate => rate.Rate));
            Assert.Equal(coverage, await store.GetCoverageAsync(MarketSeriesKind.Rates, "TBILL-13W", cancellationToken));
        }
    }
}
