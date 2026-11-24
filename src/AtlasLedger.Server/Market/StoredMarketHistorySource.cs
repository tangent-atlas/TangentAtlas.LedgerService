// <copyright file="StoredMarketHistorySource.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Market
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Server.Jobs;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Market history from our own database first, the vendor only for what it doesn't hold.  A series is fetched whole
    /// once, the first time it is needed (or from further back, when it is needed from further back); after that only
    /// what is new: a ticker's closes since its last stored close, and its dividends, splits and the rate once a day, for
    /// the recent weeks only.  Once a week a series is fetched whole again, to catch the vendor's corrections.  A restart
    /// asks the vendor for nothing it already has.  If the vendor can't be reached, what is stored is used.
    /// </summary>
    public sealed partial class StoredMarketHistorySource : IMarketHistorySource
    {
        /// <summary>How often a whole series is fetched again, for the vendor's corrections.</summary>
        public static readonly TimeSpan VerifyInterval = TimeSpan.FromDays(7);

        /// <summary>How far back the daily check of dividends, splits and the rate looks (late announcements, corrections).</summary>
        public const int RecentDays = 45;

        private readonly IMarketHistorySource vendor;

        private readonly IMarketHistoryStore store;

        private readonly TimeProvider timeProvider;

        private readonly ILogger<StoredMarketHistorySource> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="StoredMarketHistorySource"/> class.
        /// </summary>
        /// <param name="vendor">The vendor (EODHD, and the Treasury's rates).</param>
        /// <param name="store">Our database.</param>
        /// <param name="timeProvider">The clock.</param>
        /// <param name="logger">The log.</param>
        public StoredMarketHistorySource(IMarketHistorySource vendor, IMarketHistoryStore store, TimeProvider timeProvider, ILogger<StoredMarketHistorySource> logger)
        {
            this.vendor = vendor;
            this.store = store;
            this.timeProvider = timeProvider;
            this.logger = logger;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<DailyClose>> GetClosesAsync(string ticker, DateOnly from, DateOnly through, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(ticker);
            var now = this.timeProvider.GetUtcNow();
            var coverage = await this.store.GetCoverageAsync(MarketSeriesKind.Closes, ticker, cancellationToken).ConfigureAwait(false);
            try
            {
                if (coverage is null || now - coverage.VerifiedAt >= VerifyInterval)
                {
                    // Whole: the first time, or the weekly check for corrections.
                    var start = coverage is null ? from : Min(from, coverage.From);
                    var end = coverage is null ? through : Max(through, coverage.Through);
                    var fetched = await this.vendor.GetClosesAsync(ticker, start, end, cancellationToken).ConfigureAwait(false);
                    var last = fetched.Count > 0 ? fetched.Max(close => close.Date) : (DateOnly?)null;
                    var covered = new SeriesCoverage(start, Max(last ?? start.AddDays(-1), coverage?.Through ?? start.AddDays(-1)), now, now);
                    await this.SaveAsync(MarketSeriesKind.Closes, ticker, new MarketRows(Closes: fetched), covered, fetched.Count > 0 ? (start, last!.Value) : null, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var covered = coverage;
                    if (from < covered.From)
                    {
                        // Needed from further back than stored: only the earlier part.
                        var earlier = await this.vendor.GetClosesAsync(ticker, from, covered.From.AddDays(-1), cancellationToken).ConfigureAwait(false);
                        covered = covered with { From = from, FetchedAt = now };
                        await this.SaveAsync(MarketSeriesKind.Closes, ticker, new MarketRows(Closes: earlier), covered, null, cancellationToken).ConfigureAwait(false);
                    }

                    if (covered.Through < through)
                    {
                        // The days since the last stored close: the daily delta.
                        var newer = await this.vendor.GetClosesAsync(ticker, covered.Through.AddDays(1), through, cancellationToken).ConfigureAwait(false);
                        if (newer.Count > 0)
                        {
                            covered = covered with { Through = Max(covered.Through, newer.Max(close => close.Date)), FetchedAt = now };
                            await this.SaveAsync(MarketSeriesKind.Closes, ticker, new MarketRows(Closes: newer), covered, null, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception exception) when (IsVendorFailure(exception, cancellationToken) && coverage is not null)
            {
                this.LogUsingStored(ticker, "closes", exception.Message);
            }

            return await this.store.ReadClosesAsync(ticker, from, through, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<CashDividend>> GetDividendsAsync(string ticker, DateOnly from, CancellationToken cancellationToken)
        {
            await this.RefreshWholeFromAsync(
                MarketSeriesKind.Dividends,
                ticker,
                from,
                async start => new MarketRows(Dividends: await this.vendor.GetDividendsAsync(ticker, start, cancellationToken).ConfigureAwait(false)),
                rows => rows.Dividends!.Select(item => item.ExDate),
                cancellationToken).ConfigureAwait(false);
            return await this.store.ReadDividendsAsync(ticker, from, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<StockSplit>> GetSplitsAsync(string ticker, DateOnly from, CancellationToken cancellationToken)
        {
            await this.RefreshWholeFromAsync(
                MarketSeriesKind.Splits,
                ticker,
                from,
                async start => new MarketRows(Splits: await this.vendor.GetSplitsAsync(ticker, start, cancellationToken).ConfigureAwait(false)),
                rows => rows.Splits!.Select(item => item.Date),
                cancellationToken).ConfigureAwait(false);
            return await this.store.ReadSplitsAsync(ticker, from, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<DailyRate>> GetRatesAsync(string code, DateOnly from, CancellationToken cancellationToken)
        {
            await this.RefreshWholeFromAsync(
                MarketSeriesKind.Rates,
                code,
                from,
                async start => new MarketRows(Rates: await this.vendor.GetRatesAsync(code, start, cancellationToken).ConfigureAwait(false)),
                rows => rows.Rates!.Select(item => item.Date),
                cancellationToken).ConfigureAwait(false);
            return await this.store.ReadRatesAsync(code, from, cancellationToken).ConfigureAwait(false);
        }

        private static DateOnly Min(DateOnly first, DateOnly second) => first < second ? first : second;

        private static DateOnly Max(DateOnly first, DateOnly second) => first > second ? first : second;

        private static bool IsVendorFailure(Exception exception, CancellationToken cancellationToken) =>
            exception is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);

        /// <summary>
        /// A series the vendor gives from a day on (dividends, splits, a rate): whole the first time, when needed from
        /// further back, or at the weekly check; otherwise, once a day, only its recent weeks.
        /// </summary>
        private async Task RefreshWholeFromAsync(MarketSeriesKind kind, string code, DateOnly from, Func<DateOnly, Task<MarketRows>> fetch, Func<MarketRows, IEnumerable<DateOnly>> dates, CancellationToken cancellationToken)
        {
            var now = this.timeProvider.GetUtcNow();
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, MarketCalendar.Zone).DateTime);
            var coverage = await this.store.GetCoverageAsync(kind, code, cancellationToken).ConfigureAwait(false);
            var whole = coverage is null || from < coverage.From || now - coverage.VerifiedAt >= VerifyInterval;
            if (!whole && coverage!.Through >= today)
            {
                return;   // already asked today
            }

            var start = whole ? (coverage is null ? from : Min(from, coverage.From)) : Max(coverage!.From, coverage.Through.AddDays(-RecentDays));
            try
            {
                var rows = await fetch(start).ConfigureAwait(false);
                var covered = new SeriesCoverage(coverage is null ? start : Min(start, coverage.From), today, now, whole ? now : coverage!.VerifiedAt);

                // Fetched whole again, what the vendor no longer has goes; a delta only adds and corrects.
                var replace = whole && coverage is not null && dates(rows).Any() ? (start, today) : ((DateOnly, DateOnly)?)null;
                await this.SaveAsync(kind, code, rows, covered, replace, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsVendorFailure(exception, cancellationToken) && coverage is not null)
            {
                this.LogUsingStored(code, kind.ToString().ToLowerInvariant(), exception.Message);
            }
        }

        private Task SaveAsync(MarketSeriesKind kind, string code, MarketRows rows, SeriesCoverage coverage, (DateOnly From, DateOnly Through)? replace, CancellationToken cancellationToken)
        {
            this.LogSaved(code, kind.ToString().ToLowerInvariant(), MarketRowDates.Of(rows).Count(), coverage.From, coverage.Through);
            return this.store.SaveAsync(kind, code, rows, coverage, replace, cancellationToken);
        }

        [LoggerMessage(Level = LogLevel.Debug, Message = "Market history: stored {Count} {What} of {Code}; held from {From} through {Through}.")]
        private partial void LogSaved(string code, string what, int count, DateOnly from, DateOnly through);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Market history: the vendor couldn't be reached for {What} of {Code} ({Reason}); using what is stored.")]
        private partial void LogUsingStored(string code, string what, string reason);
    }
}
