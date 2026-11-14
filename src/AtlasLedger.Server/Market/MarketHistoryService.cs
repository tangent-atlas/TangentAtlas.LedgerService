// <copyright file="MarketHistoryService.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Market
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using AtlasLedger.Server.Jobs;
    using AtlasLedger.Server.Switches;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// The market history service's settings.
    /// </summary>
    public sealed class MarketHistoryOptions
    {
        /// <summary>The configuration section.</summary>
        public const string SectionName = "MarketHistory";

        /// <summary>The code of the risk-free rate: the 13-week Treasury bill's coupon equivalent (investment yield).</summary>
        public const string RiskFreeRate = Holdings.AccountHistoryFold.RiskFreeRate;

        /// <summary>Gets or sets the EODHD API token (a secret: user secrets or Key Vault, never in a file).</summary>
        public string? EodhdApiToken { get; set; }

        /// <summary>Gets or sets EODHD's API address.</summary>
        public Uri EodhdAddress { get; set; } = new Uri("https://eodhd.com/api/");

        /// <summary>Gets or sets the Treasury's address (its daily bill rates).</summary>
        public Uri TreasuryAddress { get; set; } = new Uri("https://home.treasury.gov/");

        /// <summary>Gets or sets the time of day in New York after which a day's closes are expected from the vendor.</summary>
        public TimeOnly ClosesPublishedAfter { get; set; } = new TimeOnly(16, 30);

        /// <summary>Gets or sets how often the service looks for what's missing (the vendor is asked only for what is).</summary>
        public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>Gets or sets how many days before a tenant's first event its history starts.</summary>
        public int DaysBeforeFirstEvent { get; set; } = 7;
    }

    /// <summary>
    /// Loads market history into every tenant: for each US ticker it knows, the closes from a week before its first event,
    /// its dividends and splits, and the risk-free rate (the Treasury's 13-week bill), as ephemeral facts for the rules; then, each market day after the
    /// close, that day's.  The latest close is also published as the ticker's price where nothing fresher is.  The vendor is
    /// asked once per ticker for what no tenant has yet, so a thousand tenants holding the same stocks cost the same calls
    /// as one.  Nothing is persisted: a restart loads it all again.
    /// </summary>
    internal sealed partial class MarketHistoryService : BackgroundService
    {
        private readonly Ledger ledger;

        private readonly IMarketHistorySource source;

        private readonly MarketHistoryOptions options;

        private readonly ServiceSwitch serviceSwitch;

        private readonly TimeProvider timeProvider;

        private readonly ILogger<MarketHistoryService> logger;

        private readonly ConcurrentDictionary<string, Series<DailyClose>> closes = new ConcurrentDictionary<string, Series<DailyClose>>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, Series<CashDividend>> dividends = new ConcurrentDictionary<string, Series<CashDividend>>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, Series<StockSplit>> splits = new ConcurrentDictionary<string, Series<StockSplit>>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, Series<DailyRate>> rates = new ConcurrentDictionary<string, Series<DailyRate>>(StringComparer.OrdinalIgnoreCase);

        private readonly SemaphoreSlim partitionCreated = new SemaphoreSlim(0);

        public MarketHistoryService(Ledger ledger, IMarketHistorySource source, IOptions<MarketHistoryOptions> options, ServiceSwitchboard switchboard, TimeProvider timeProvider, ILogger<MarketHistoryService> logger)
        {
            this.ledger = ledger;
            this.source = source;
            this.options = options.Value;
            this.serviceSwitch = switchboard[ServiceSwitchboard.MarketHistory];
            this.timeProvider = timeProvider;
            this.logger = logger;
        }

        public override void Dispose()
        {
            this.partitionCreated.Dispose();
            base.Dispose();
        }

        /// <summary>
        /// Loads what every tenant is missing, now.
        /// </summary>
        /// <param name="cancellationToken">Cancels the pass.</param>
        /// <returns>A task that completes when the pass is done.</returns>
        public async Task RefreshAsync(CancellationToken cancellationToken)
        {
            var now = this.timeProvider.GetUtcNow();
            var latest = MarketCalendar.LatestClosedDay(now, this.options.ClosesPublishedAfter);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, MarketCalendar.Zone).DateTime);
            var needs = new List<(LedgerPartition Partition, DateOnly From, IReadOnlyList<(string Ticker, string Currency)> Tickers)>();
            foreach (var partition in this.ledger.Partitions)
            {
                if (partition.IsFaulted)
                {
                    continue;
                }

                needs.Add(Needs(partition, today, this.options.DaysBeforeFirstEvent));
            }

            if (needs.Count == 0)
            {
                return;
            }

            var from = needs.Min(need => need.From);
            await this.LoadAsync(this.rates, MarketHistoryOptions.RiskFreeRate, from, today, () => this.source.GetRatesAsync(MarketHistoryOptions.RiskFreeRate, from, cancellationToken)).ConfigureAwait(false);
            foreach (var ticker in needs.SelectMany(need => need.Tickers.Select(item => item.Ticker)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var tickerFrom = needs.Where(need => need.Tickers.Any(item => string.Equals(item.Ticker, ticker, StringComparison.OrdinalIgnoreCase))).Min(need => need.From);
                await this.LoadClosesAsync(ticker, tickerFrom, latest, now, cancellationToken).ConfigureAwait(false);
                await this.LoadAsync(this.dividends, ticker, tickerFrom, today, () => this.source.GetDividendsAsync(ticker, tickerFrom, cancellationToken)).ConfigureAwait(false);
                await this.LoadAsync(this.splits, ticker, tickerFrom, today, () => this.source.GetSplitsAsync(ticker, tickerFrom, cancellationToken)).ConfigureAwait(false);
            }

            var published = 0;
            foreach (var (partition, tenantFrom, tickers) in needs)
            {
                published += this.Publish(partition, tenantFrom, tickers);
            }

            if (published > 0)
            {
                this.LogPublished(published, needs.Count, from, latest);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            this.ledger.PartitionCreated += this.OnPartitionCreated;
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (!this.serviceSwitch.IsOn)
                    {
                        await this.serviceSwitch.WaitForChangeAsync(stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        await this.RefreshAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        this.LogPassFailed(exception);
                    }

                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    await Task.WhenAny(
                        this.serviceSwitch.WaitForChangeAsync(linked.Token),
                        this.partitionCreated.WaitAsync(linked.Token),
                        Task.Delay(this.options.RefreshInterval, this.timeProvider, linked.Token)).ConfigureAwait(false);
                    await linked.CancelAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                this.ledger.PartitionCreated -= this.OnPartitionCreated;
            }
        }

        /// <summary>
        /// What a tenant needs: its US tickers (of assets that aren't cash) and the day its history starts.
        /// </summary>
        private static (LedgerPartition Partition, DateOnly From, IReadOnlyList<(string Ticker, string Currency)> Tickers) Needs(LedgerPartition partition, DateOnly today, int daysBefore)
        {
            lock (partition.SyncRoot)
            {
                var first = partition.Events.Select(row => (DateTimeOffset?)row.EffectiveTime).Min();
                var from = (first is { } time ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, MarketCalendar.Zone).DateTime) : today).AddDays(-daysBefore);
                var tickers = partition.Symbols
                    .Where(symbol => symbol.Scheme == SymbolScheme.Ticker)
                    .Select(symbol => (symbol.Code, Asset: partition.FindAsset(symbol.NodeId)))
                    .Where(item => item.Asset is { AssetType: not AssetType.Cash, QuoteCurrency: "USD" })
                    .Select(item => (item.Code, item.Asset!.QuoteCurrency))
                    .ToArray();
                return (partition, from, tickers);
            }
        }

        private async Task LoadClosesAsync(string ticker, DateOnly from, DateOnly latest, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var series = this.closes.GetOrAdd(ticker, _ => new Series<DailyClose>());
            DateOnly fetchFrom;
            lock (series)
            {
                if (series.From is { } cachedFrom && cachedFrom <= from)
                {
                    // Already from far enough back: only the days since, and not more often than the refresh interval.
                    if (series.Through >= latest || now - series.LastAttempt < this.options.RefreshInterval)
                    {
                        return;
                    }

                    fetchFrom = series.Through!.Value.AddDays(1);
                }
                else
                {
                    fetchFrom = from;
                }

                series.LastAttempt = now;
            }

            try
            {
                var fetched = await this.source.GetClosesAsync(ticker, fetchFrom, latest, cancellationToken).ConfigureAwait(false);
                lock (series)
                {
                    var byDate = series.Items.ToDictionary(item => item.Date);
                    foreach (var close in fetched)
                    {
                        byDate[close.Date] = close;
                    }

                    series.Items = byDate.Values.OrderBy(item => item.Date).ToList();
                    series.From = series.From is { } earlier && earlier < fetchFrom ? earlier : fetchFrom;

                    // Through the last day the vendor has: a day it hasn't published yet is asked for again later.
                    series.Through = fetched.Count > 0 ? fetched[^1].Date : series.Through ?? fetchFrom.AddDays(-1);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                this.LogLoadFailed(ticker, "closes", exception.Message);
            }
        }

        /// <summary>
        /// Loads a series that is fetched whole from a day (dividends, splits, rates): once a day, or again when a tenant
        /// needs it from further back.
        /// </summary>
        private async Task LoadAsync<T>(ConcurrentDictionary<string, Series<T>> cache, string key, DateOnly from, DateOnly today, Func<Task<IReadOnlyList<T>>> fetch)
        {
            var series = cache.GetOrAdd(key, _ => new Series<T>());
            lock (series)
            {
                if (series.From is { } cachedFrom && cachedFrom <= from && series.Through >= today)
                {
                    return;
                }
            }

            try
            {
                var fetched = await fetch().ConfigureAwait(false);
                lock (series)
                {
                    series.Items = fetched.ToList();
                    series.From = from;
                    series.Through = today;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or TaskCanceledException)
            {
                this.LogLoadFailed(key, typeof(T).Name, exception.Message);
            }
        }

        /// <summary>
        /// Publishes to a tenant what it is missing from the cache.  Publishing a value it already has would wake its rules for
        /// nothing, so only new ones are.
        /// </summary>
        private int Publish(LedgerPartition partition, DateOnly from, IReadOnlyList<(string Ticker, string Currency)> tickers)
        {
            var now = this.timeProvider.GetUtcNow();
            var count = 0;
            foreach (var rate in Items(this.rates, MarketHistoryOptions.RiskFreeRate).Where(rate => rate.Date >= from))
            {
                if (partition.FindInterestRate(MarketHistoryOptions.RiskFreeRate, rate.Date) is not { } existing || existing.Rate != rate.Rate)
                {
                    count++;
                    partition.PublishInterestRate(new InterestRate { Code = MarketHistoryOptions.RiskFreeRate, Date = rate.Date, Rate = rate.Rate, AsOf = now });
                }
            }

            foreach (var (ticker, currency) in tickers)
            {
                DailyClose? last = null;
                foreach (var close in Items(this.closes, ticker).Where(close => close.Date >= from))
                {
                    last = close;
                    if (partition.FindClosingPrice(SymbolScheme.Ticker, ticker, currency, close.Date) is not { } existing || existing.Close != close.Close)
                    {
                        count++;
                        partition.PublishClosingPrice(new ClosingPrice { Scheme = SymbolScheme.Ticker, Code = ticker, Currency = currency, Date = close.Date, Close = close.Close, AsOf = MarketCalendar.CloseTime(close.Date) });
                    }
                }

                // The latest close is the ticker's price, unless something fresher (a live feed) has priced it since.
                if (last is not null && (partition.FindPrice(SymbolScheme.Ticker, ticker, currency) is not { } price || price.AsOf < MarketCalendar.CloseTime(last.Date)))
                {
                    partition.PublishPrice(new Price { Scheme = SymbolScheme.Ticker, Code = ticker, Currency = currency, Last = last.Close, AsOf = MarketCalendar.CloseTime(last.Date) });
                }

                foreach (var dividend in Items(this.dividends, ticker).Where(dividend => dividend.ExDate >= from))
                {
                    if (partition.FindDividend(SymbolScheme.Ticker, ticker, dividend.ExDate) is not { } existing || existing.Amount != dividend.Amount || existing.PaymentDate != dividend.PaymentDate)
                    {
                        count++;
                        partition.PublishDividend(new Dividend { Scheme = SymbolScheme.Ticker, Code = ticker, Currency = dividend.Currency, ExDate = dividend.ExDate, PaymentDate = dividend.PaymentDate, Amount = dividend.Amount, AsOf = now });
                    }
                }

                foreach (var split in Items(this.splits, ticker).Where(split => split.Date >= from))
                {
                    if (partition.FindSplit(SymbolScheme.Ticker, ticker, split.Date) is not { } existing || existing.Ratio != split.Ratio)
                    {
                        count++;
                        partition.PublishSplit(new Split { Scheme = SymbolScheme.Ticker, Code = ticker, Date = split.Date, Ratio = split.Ratio, AsOf = now });
                    }
                }
            }

            return count;
        }

        private static IReadOnlyList<T> Items<T>(ConcurrentDictionary<string, Series<T>> cache, string key)
        {
            if (!cache.TryGetValue(key, out var series))
            {
                return [];
            }

            lock (series)
            {
                return series.Items.ToArray();
            }
        }

        private void OnPartitionCreated(object? sender, LedgerPartition partition) => this.partitionCreated.Release();

        [LoggerMessage(Level = LogLevel.Information, Message = "Market history: published {Count} new value(s) to {Tenants} tenant(s), from {From} through the close of {Latest}.")]
        private partial void LogPublished(int count, int tenants, DateOnly from, DateOnly latest);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Market history: loading {What} for {Key} failed ({Reason}); tried again on the next pass.")]
        private partial void LogLoadFailed(string key, string what, string reason);

        [LoggerMessage(Level = LogLevel.Error, Message = "Market history: a pass failed; tried again on the next.")]
        private partial void LogPassFailed(Exception exception);

        private sealed class Series<T>
        {
            public List<T> Items { get; set; } = new List<T>();

            public DateOnly? From { get; set; }

            public DateOnly? Through { get; set; }

            public DateTimeOffset LastAttempt { get; set; }
        }
    }
}
