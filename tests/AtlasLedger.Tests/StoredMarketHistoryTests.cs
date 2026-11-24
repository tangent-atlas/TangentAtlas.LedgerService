// <copyright file="StoredMarketHistoryTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias server;

namespace AtlasLedger.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging.Abstractions;
    using server::AtlasLedger.Server.Market;
    using Xunit;

    /// <summary>
    /// Market history kept in our own database: the vendor is asked for a series once, then only for what is new; a restart
    /// asks it for nothing; once a week a series is fetched whole again for corrections; a vendor outage falls back to what
    /// is stored.
    /// </summary>
    public sealed class StoredMarketHistoryTests
    {
        private static readonly DateOnly Start = new(2021, 11, 1);

        private static readonly DateTimeOffset Monday = new(2026, 11, 23, 23, 0, 0, TimeSpan.Zero);   // 6 pm New York, after the close

        [Fact]
        public async Task ASeriesIsFetchedOnceAndARestartReadsTheDatabase()
        {
            var (vendor, store, clock) = Parts();
            var first = Source(vendor, store, clock);

            var closes = await first.GetClosesAsync("SPY", Start, Day(Monday), Cancel);
            Assert.Equal(Vendor.Days(Start, Day(Monday)).Count, closes.Count);
            Assert.Equal([(Start, Day(Monday))], vendor.Requests("SPY"));

            // Asked again, and after a restart (a new source over the same database): nothing from the vendor.
            await first.GetClosesAsync("SPY", Start, Day(Monday), Cancel);
            var afterRestart = await Source(vendor, store, clock).GetClosesAsync("SPY", Start, Day(Monday), Cancel);
            Assert.Equal(closes, afterRestart);
            Assert.Single(vendor.Requests("SPY"));
        }

        [Fact]
        public async Task TheNextDayAsksOnlyForTheNewClose()
        {
            var (vendor, store, clock) = Parts();
            await Source(vendor, store, clock).GetClosesAsync("SPY", Start, Day(Monday), Cancel);

            clock.Now = Monday.AddDays(1);
            var closes = await Source(vendor, store, clock).GetClosesAsync("SPY", Start, Day(clock.Now), Cancel);

            Assert.Equal((Day(Monday).AddDays(1), Day(clock.Now)), vendor.Requests("SPY")[^1]);
            Assert.Equal(Day(clock.Now), closes[^1].Date);
        }

        [Fact]
        public async Task NeededFromFurtherBackOnlyTheEarlierPartIsFetched()
        {
            var (vendor, store, clock) = Parts();
            var source = Source(vendor, store, clock);
            await source.GetClosesAsync("SPY", Start, Day(Monday), Cancel);

            var earlier = Start.AddYears(-1);
            var closes = await source.GetClosesAsync("SPY", earlier, Day(Monday), Cancel);

            Assert.Equal((earlier, Start.AddDays(-1)), vendor.Requests("SPY")[^1]);
            Assert.Equal(Vendor.Days(earlier, Day(Monday)).Count, closes.Count);
        }

        [Fact]
        public async Task AWeekLaterTheWholeSeriesIsFetchedAgainAndCorrectionsReplaceWhatWasStored()
        {
            var (vendor, store, clock) = Parts();
            await Source(vendor, store, clock).GetClosesAsync("SPY", Start, Day(Monday), Cancel);
            var corrected = Vendor.Days(Start, Day(Monday))[10];
            var dropped = Vendor.Days(Start, Day(Monday))[20];
            vendor.Corrections[corrected] = 999m;
            vendor.Missing.Add(dropped);

            clock.Now = Monday.AddDays(7);
            var closes = await Source(vendor, store, clock).GetClosesAsync("SPY", Start, Day(clock.Now), Cancel);

            Assert.Equal((Start, Day(clock.Now)), vendor.Requests("SPY")[^1]);
            Assert.Equal(999m, closes.Single(close => close.Date == corrected).Close);
            Assert.DoesNotContain(closes, close => close.Date == dropped);
        }

        [Fact]
        public async Task DividendsAreAskedForOnceADayAndThenOnlyForRecentWeeks()
        {
            var (vendor, store, clock) = Parts();
            var source = Source(vendor, store, clock);
            var dividends = await source.GetDividendsAsync("SPY", Start, Cancel);
            await source.GetDividendsAsync("SPY", Start, Cancel);
            Assert.Equal(20, dividends.Count);   // quarterly for five years
            Assert.Equal([Start], vendor.DividendRequests);

            clock.Now = Monday.AddDays(1);
            var later = await Source(vendor, store, clock).GetDividendsAsync("SPY", Start, Cancel);
            Assert.Equal([Start, Day(Monday).AddDays(-StoredMarketHistorySource.RecentDays)], vendor.DividendRequests);
            Assert.Equal(dividends, later);
        }

        [Fact]
        public async Task WhenTheVendorCantBeReachedWhatIsStoredIsUsed()
        {
            var (vendor, store, clock) = Parts();
            await Source(vendor, store, clock).GetClosesAsync("SPY", Start, Day(Monday), Cancel);

            vendor.IsDown = true;
            clock.Now = Monday.AddDays(1);
            var closes = await Source(vendor, store, clock).GetClosesAsync("SPY", Start, Day(clock.Now), Cancel);
            Assert.Equal(Day(Monday), closes[^1].Date);

            // A series never stored has nothing to fall back on: the failure goes to the history service, which tries again.
            await Assert.ThrowsAsync<HttpRequestException>(() => Source(vendor, store, clock).GetClosesAsync("QQQ", Start, Day(clock.Now), Cancel));
        }

        private static CancellationToken Cancel => TestContext.Current.CancellationToken;

        private static DateOnly Day(DateTimeOffset time) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime);

        private static (Vendor Vendor, InMemoryMarketHistoryStore Store, Clock Clock) Parts() => (new Vendor(), new InMemoryMarketHistoryStore(), new Clock { Now = Monday });

        private static StoredMarketHistorySource Source(Vendor vendor, InMemoryMarketHistoryStore store, Clock clock) =>
            new(vendor, store, clock, NullLogger<StoredMarketHistorySource>.Instance);

        private sealed class Clock : TimeProvider
        {
            public DateTimeOffset Now { get; set; }

            public override DateTimeOffset GetUtcNow() => this.Now;
        }

        /// <summary>A vendor with a close every weekday, a dividend each quarter, and a record of what it was asked.</summary>
        private sealed class Vendor : IMarketHistorySource
        {
            private readonly List<(string Ticker, DateOnly From, DateOnly Through)> closeRequests = [];

            public Dictionary<DateOnly, decimal> Corrections { get; } = [];

            public HashSet<DateOnly> Missing { get; } = [];

            public List<DateOnly> DividendRequests { get; } = [];

            public bool IsDown { get; set; }

            public static List<DateOnly> Days(DateOnly from, DateOnly through) =>
                Enumerable.Range(0, through.DayNumber - from.DayNumber + 1).Select(from.AddDays).Where(day => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToList();

            public List<(DateOnly From, DateOnly Through)> Requests(string ticker) =>
                this.closeRequests.Where(request => request.Ticker == ticker).Select(request => (request.From, request.Through)).ToList();

            public Task<IReadOnlyList<DailyClose>> GetClosesAsync(string ticker, DateOnly from, DateOnly through, CancellationToken cancellationToken)
            {
                this.ThrowIfDown();
                this.closeRequests.Add((ticker, from, through));
                return Task.FromResult<IReadOnlyList<DailyClose>>(Days(from, through)
                    .Where(day => !this.Missing.Contains(day))
                    .Select(day => new DailyClose(day, this.Corrections.TryGetValue(day, out var corrected) ? corrected : 100m + (day.DayNumber % 50)))
                    .ToArray());
            }

            public Task<IReadOnlyList<CashDividend>> GetDividendsAsync(string ticker, DateOnly from, CancellationToken cancellationToken)
            {
                this.ThrowIfDown();
                this.DividendRequests.Add(from);
                return Task.FromResult<IReadOnlyList<CashDividend>>(Enumerable.Range(0, 20)
                    .Select(quarter => Start.AddMonths(3 * quarter).AddDays(14))
                    .Where(day => day >= from)
                    .Select(day => new CashDividend(day, day.AddDays(30), 1.75m, "USD"))
                    .ToArray());
            }

            public Task<IReadOnlyList<StockSplit>> GetSplitsAsync(string ticker, DateOnly from, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<StockSplit>>([]);

            public Task<IReadOnlyList<DailyRate>> GetRatesAsync(string code, DateOnly from, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<DailyRate>>([]);

            private void ThrowIfDown()
            {
                if (this.IsDown)
                {
                    throw new HttpRequestException("The vendor is down.");
                }
            }
        }
    }
}
