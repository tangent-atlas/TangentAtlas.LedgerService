// <copyright file="MarketHistorySource.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Market
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// A day's closing price.
    /// </summary>
    /// <param name="Date">The market day.</param>
    /// <param name="Close">The price at the close, as traded that day.</param>
    public sealed record DailyClose(DateOnly Date, decimal Close);

    /// <summary>
    /// A cash dividend per share.
    /// </summary>
    /// <param name="ExDate">The first day the shares trade without it.</param>
    /// <param name="PaymentDate">The day it is paid, if known.</param>
    /// <param name="Amount">The amount per share.</param>
    /// <param name="Currency">Its currency.</param>
    public sealed record CashDividend(DateOnly ExDate, DateOnly? PaymentDate, decimal Amount, string Currency);

    /// <summary>
    /// A split.
    /// </summary>
    /// <param name="Date">The day it takes effect, at the open.</param>
    /// <param name="Ratio">The shares each share becomes.</param>
    public sealed record StockSplit(DateOnly Date, decimal Ratio);

    /// <summary>
    /// A rate published for a day.
    /// </summary>
    /// <param name="Date">The day.</param>
    /// <param name="Rate">The rate, as a fraction a year.</param>
    public sealed record DailyRate(DateOnly Date, decimal Rate);

    /// <summary>
    /// Where market history comes from: a vendor's closing prices and corporate actions for US tickers, and a published
    /// interest rate.
    /// </summary>
    public interface IMarketHistorySource
    {
        /// <summary>Gets a ticker's closes from one day through another.</summary>
        /// <param name="ticker">The ticker, as the tenant knows it (BRK.B).</param>
        /// <param name="from">The first day.</param>
        /// <param name="through">The last day.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The closes the vendor has, in date order.</returns>
        Task<IReadOnlyList<DailyClose>> GetClosesAsync(string ticker, DateOnly from, DateOnly through, CancellationToken cancellationToken);

        /// <summary>Gets a ticker's cash dividends with an ex-date from a day on (announced ones included).</summary>
        /// <param name="ticker">The ticker.</param>
        /// <param name="from">The first ex-date.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The dividends.</returns>
        Task<IReadOnlyList<CashDividend>> GetDividendsAsync(string ticker, DateOnly from, CancellationToken cancellationToken);

        /// <summary>Gets a ticker's splits from a day on.</summary>
        /// <param name="ticker">The ticker.</param>
        /// <param name="from">The first day.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The splits.</returns>
        Task<IReadOnlyList<StockSplit>> GetSplitsAsync(string ticker, DateOnly from, CancellationToken cancellationToken);

        /// <summary>Gets a published rate's values from a day on.</summary>
        /// <param name="code">The rate's code.</param>
        /// <param name="from">The first day.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The rates, in date order.</returns>
        Task<IReadOnlyList<DailyRate>> GetRatesAsync(string code, DateOnly from, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Market history from EODHD (closes, dividends and splits of US tickers) and the US Treasury (bill rates).  EODHD's
    /// <c>close</c> is the price as traded that day, which is what the books need; splits and dividends are applied as events.
    /// </summary>
    public sealed class EodhdTreasurySource : IMarketHistorySource
    {
        private readonly HttpClient httpClient;

        private readonly MarketHistoryOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="EodhdTreasurySource"/> class.
        /// </summary>
        /// <param name="httpClient">The HTTP client.</param>
        /// <param name="options">The options: the EODHD token and the addresses.</param>
        public EodhdTreasurySource(HttpClient httpClient, IOptions<MarketHistoryOptions> options)
        {
            ArgumentNullException.ThrowIfNull(options);
            this.httpClient = httpClient;
            this.options = options.Value;
        }

        /// <summary>
        /// The vendor's name for a US ticker: a class letter after a hyphen, and the exchange code (BRK.B is BRK-B.US).
        /// </summary>
        /// <param name="ticker">The ticker.</param>
        /// <returns>EODHD's symbol.</returns>
        public static string VendorSymbol(string ticker)
        {
            ArgumentNullException.ThrowIfNull(ticker);
            return $"{ticker.Replace('.', '-')}.US";
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<DailyClose>> GetClosesAsync(string ticker, DateOnly from, DateOnly through, CancellationToken cancellationToken)
        {
            var rows = await this.GetEodhdAsync<EodRow>($"eod/{VendorSymbol(ticker)}", $"from={Day(from)}&to={Day(through)}", cancellationToken).ConfigureAwait(false);
            return rows.Where(row => row.Close is > 0m).Select(row => new DailyClose(DateOnly.Parse(row.Date, CultureInfo.InvariantCulture), row.Close!.Value)).OrderBy(close => close.Date).ToArray();
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<CashDividend>> GetDividendsAsync(string ticker, DateOnly from, CancellationToken cancellationToken)
        {
            var rows = await this.GetEodhdAsync<DividendRow>($"div/{VendorSymbol(ticker)}", $"from={Day(from)}", cancellationToken).ConfigureAwait(false);
            return rows
                .Where(row => (row.UnadjustedValue ?? row.Value) is > 0m)
                .Select(row => new CashDividend(
                    DateOnly.Parse(row.Date, CultureInfo.InvariantCulture),
                    DateOnly.TryParse(row.PaymentDate, CultureInfo.InvariantCulture, out var paid) ? paid : null,
                    (row.UnadjustedValue ?? row.Value)!.Value,
                    string.IsNullOrEmpty(row.Currency) ? "USD" : row.Currency))
                .ToArray();
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<StockSplit>> GetSplitsAsync(string ticker, DateOnly from, CancellationToken cancellationToken)
        {
            var rows = await this.GetEodhdAsync<SplitRow>($"splits/{VendorSymbol(ticker)}", $"from={Day(from)}", cancellationToken).ConfigureAwait(false);
            return rows.Select(row => new StockSplit(DateOnly.Parse(row.Date, CultureInfo.InvariantCulture), Ratio(row.Split))).Where(split => split.Ratio > 0m).ToArray();
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<DailyRate>> GetRatesAsync(string code, DateOnly from, CancellationToken cancellationToken)
        {
            // The Treasury's daily bill rates, a year to a file: Date (MM/dd/yyyy) and, per maturity, the bank discount and the
            // coupon equivalent, in percent.  The coupon equivalent is the investment yield, the annual return of holding the bill.
            if (code != MarketHistoryOptions.RiskFreeRate)
            {
                throw new InvalidOperationException($"Rate {code} isn't one the Treasury publishes here.");
            }

            var rates = new List<DailyRate>();
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            for (var year = from.Year; year <= today.Year; year++)
            {
                var address = new Uri(this.options.TreasuryAddress, $"resource-center/data-chart-center/interest-rates/daily-treasury-rates.csv/{year}/all?type=daily_treasury_bill_rates&field_tdr_date_value={year}&page&_format=csv");
                var text = await this.httpClient.GetStringAsync(address, cancellationToken).ConfigureAwait(false);
                rates.AddRange(ReadTreasuryBillRates(text).Where(rate => rate.Date >= from));
            }

            return rates.OrderBy(rate => rate.Date).ToArray();
        }

        /// <summary>
        /// Reads the Treasury's daily bill rates file: the 13-week coupon equivalent of each day, as a fraction.
        /// </summary>
        /// <param name="text">The file.</param>
        /// <returns>The rates.</returns>
        public static IReadOnlyList<DailyRate> ReadTreasuryBillRates(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            using var reader = new StringReader(text);
            var header = (reader.ReadLine() ?? string.Empty).Split(',').Select(name => name.Trim('"', ' ')).ToList();
            var column = header.FindIndex(name => string.Equals(name, "13 WEEKS COUPON EQUIVALENT", StringComparison.OrdinalIgnoreCase));
            var rates = new List<DailyRate>();
            if (column < 0)
            {
                return rates;
            }

            while (reader.ReadLine() is { } line)
            {
                var parts = line.Split(',');
                if (parts.Length > column
                    && DateOnly.TryParseExact(parts[0].Trim('"'), "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    && decimal.TryParse(parts[column].Trim('"'), NumberStyles.Number, CultureInfo.InvariantCulture, out var percent))
                {
                    rates.Add(new DailyRate(date, percent / 100m));
                }
            }

            return rates;
        }

        /// <summary>
        /// Reads EODHD's split text: new shares over old ("4.000000/1.000000").
        /// </summary>
        /// <param name="text">The text.</param>
        /// <returns>The ratio, or 0 if it can't be read.</returns>
        public static decimal Ratio(string? text)
        {
            var parts = (text ?? string.Empty).Split('/');
            return parts.Length == 2
                && decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var shares)
                && decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var per)
                && per > 0m
                ? shares / per
                : 0m;
        }

        private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private async Task<IReadOnlyList<T>> GetEodhdAsync<T>(string path, string query, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(this.options.EodhdApiToken))
            {
                throw new InvalidOperationException("MarketHistory:EodhdApiToken is not set.");
            }

            var address = new Uri(this.options.EodhdAddress, $"{path}?{query}&fmt=json&api_token={Uri.EscapeDataString(this.options.EodhdApiToken)}");
            using var response = await this.httpClient.GetAsync(address, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // The address carries the token: say which request failed without it.
                throw new HttpRequestException($"EODHD {path}: {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<List<T>>(cancellationToken).ConfigureAwait(false) ?? [];
        }

        private sealed record EodRow([property: JsonPropertyName("date")] string Date, [property: JsonPropertyName("close")] decimal? Close);

        private sealed record DividendRow(
            [property: JsonPropertyName("date")] string Date,
            [property: JsonPropertyName("paymentDate")] string? PaymentDate,
            [property: JsonPropertyName("value")] decimal? Value,
            [property: JsonPropertyName("unadjustedValue")] decimal? UnadjustedValue,
            [property: JsonPropertyName("currency")] string? Currency);

        private sealed record SplitRow([property: JsonPropertyName("date")] string Date, [property: JsonPropertyName("split")] string? Split);
    }
}
