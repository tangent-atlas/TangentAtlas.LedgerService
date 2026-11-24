// <copyright file="MarketHistoryController.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Market
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using AtlasLedger.Server.Jobs;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Options;

    /// <summary>A close: the price as traded that day.</summary>
    public sealed record MarketCloseDto(DateOnly Date, decimal Close);

    /// <summary>A cash dividend per share, by ex-date.</summary>
    public sealed record MarketDividendDto(DateOnly ExDate, decimal Amount);

    /// <summary>A split, by the date it takes effect.</summary>
    public sealed record MarketSplitDto(DateOnly Date, decimal Ratio);

    /// <summary>A rate a year, by day.</summary>
    public sealed record MarketRateDto(DateOnly Date, decimal Rate);

    /// <summary>One ticker's history.</summary>
    public sealed record MarketTickerDto(string Ticker, IReadOnlyList<MarketCloseDto> Closes, IReadOnlyList<MarketDividendDto> Dividends, IReadOnlyList<MarketSplitDto> Splits);

    /// <summary>
    /// Market history for a preview: the tickers asked for, from a day through the last close, and the risk-free rate; any
    /// ticker that couldn't be had is listed in Missing with why.
    /// </summary>
    public sealed record MarketHistoryDto(DateOnly From, DateOnly Through, IReadOnlyList<MarketTickerDto> Tickers, IReadOnlyList<MarketRateDto> RiskFreeRates, IReadOnlyDictionary<string, string> Missing);

    /// <summary>
    /// Market history for a member of a tenant, for what-if previews: daily closes, dividends and splits of any US tickers,
    /// and the risk-free rate, from our database (the vendor asked only for what it doesn't hold yet, once).  Public data
    /// licensed to Tangent Atlas; under the tenant's route so only its members, signed in, can ask.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("v1/tenants/{tenantId:guid}/market/history")]
    public sealed partial class MarketHistoryController : ControllerBase
    {
        /// <summary>The most tickers in one request.</summary>
        public const int MaximumTickers = 20;

        /// <summary>The furthest back a request may start.</summary>
        public const int MaximumYears = 11;

        private readonly Ledger ledger;

        private readonly IMarketHistorySource source;

        private readonly MarketHistoryOptions options;

        private readonly TimeProvider timeProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="MarketHistoryController"/> class.
        /// </summary>
        /// <param name="ledger">The ledger.</param>
        /// <param name="source">The market history (our database, then the vendor).</param>
        /// <param name="options">The market history's options.</param>
        /// <param name="timeProvider">The clock.</param>
        public MarketHistoryController(Ledger ledger, IMarketHistorySource source, IOptions<MarketHistoryOptions> options, TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(options);
            this.ledger = ledger;
            this.source = source;
            this.options = options.Value;
            this.timeProvider = timeProvider;
        }

        /// <summary>
        /// Gets the history of some tickers.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="tickers">The tickers, separated by commas (SPY,QQQ).</param>
        /// <param name="from">The first day (yyyy-MM-dd); five years and three months ago if omitted (room for the thermostat's window).</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The history.</returns>
        [HttpGet]
        [Authorize(Policy = LedgerAccessPolicies.EventRead)]
        public async Task<ActionResult<MarketHistoryDto>> GetAsync(Guid tenantId, [FromQuery] string? tickers, [FromQuery] DateOnly? from, CancellationToken cancellationToken)
        {
            if (this.ledger.FindPartition(tenantId) is null)
            {
                return this.NotFound();
            }

            var asked = (tickers ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ticker => ticker.ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (asked.Length == 0 || asked.Length > MaximumTickers || asked.Any(ticker => !TickerPattern().IsMatch(ticker)))
            {
                return this.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Name the tickers.", detail: $"From 1 to {MaximumTickers} tickers, separated by commas (letters, digits, a dot or a dash).");
            }

            var now = this.timeProvider.GetUtcNow();
            var through = MarketCalendar.LatestClosedDay(now, this.options.ClosesPublishedAfter);
            var start = from ?? through.AddYears(-5).AddMonths(-3);
            if (start > through || start < through.AddYears(-MaximumYears))
            {
                return this.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The start is out of range.", detail: $"From the last {MaximumYears} years.");
            }

            var histories = new List<MarketTickerDto>();
            var missing = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var ticker in asked)
            {
                try
                {
                    var closes = await this.source.GetClosesAsync(ticker, start, through, cancellationToken).ConfigureAwait(false);
                    if (closes.Count == 0)
                    {
                        missing[ticker] = "No closes for it in the period.";
                        continue;
                    }

                    var dividends = await this.source.GetDividendsAsync(ticker, start, cancellationToken).ConfigureAwait(false);
                    var splits = await this.source.GetSplitsAsync(ticker, start, cancellationToken).ConfigureAwait(false);
                    histories.Add(new MarketTickerDto(
                        ticker,
                        closes.Select(close => new MarketCloseDto(close.Date, close.Close)).ToArray(),
                        dividends.Select(dividend => new MarketDividendDto(dividend.ExDate, dividend.Amount)).ToArray(),
                        splits.Select(split => new MarketSplitDto(split.Date, split.Ratio)).ToArray()));
                }
                catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    missing[ticker] = "Its history couldn't be had from the market data vendor.";
                }
            }

            IReadOnlyList<DailyRate> rates;
            try
            {
                rates = await this.source.GetRatesAsync(MarketHistoryOptions.RiskFreeRate, start, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                rates = [];
                missing[MarketHistoryOptions.RiskFreeRate] = "The risk-free rate couldn't be had: the Sharpe ratio counts it as zero.";
            }

            return new MarketHistoryDto(start, through, histories, rates.Select(rate => new MarketRateDto(rate.Date, rate.Rate)).ToArray(), missing);
        }

        [GeneratedRegex("^[A-Z0-9][A-Z0-9.\\-]{0,9}$")]
        private static partial Regex TickerPattern();
    }
}
