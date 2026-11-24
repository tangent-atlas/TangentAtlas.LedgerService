// <copyright file="MarketHistoryEndpointTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias client;
extern alias server;

namespace AtlasLedger.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using client::AtlasLedger.Client.Api;
    using client::AtlasLedger.Client.TestData;
    using Microsoft.Extensions.DependencyInjection;
    using server::AtlasLedger.Server.Market;
    using Xunit;

    /// <summary>
    /// The market history endpoint: five years of a ticker's closes, dividends and splits, and the risk-free rate, for a
    /// member of the tenant; a ticker the vendor doesn't have is listed as missing rather than failing the rest.
    /// </summary>
    public sealed class MarketHistoryEndpointTests
    {
        [Fact]
        public async Task AMemberGetsFiveYearsOfTheTickersAskedFor()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var server = ServerFixture.With([], services => services.AddSingleton<IMarketHistorySource>(new Vendor()));
            await server.InitializeAsync();
            var tool = new LedgerApiClient(server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(Joe10State.Generic(DateTimeOffset.UtcNow.AddDays(-3))), cancellationToken);

            using var answer = await GetAsync(tool, server, $"v1/tenants/{tenantId}/market/history?tickers=spy,QQQ,NOPE", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            using var json = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(cancellationToken));
            var root = json.RootElement;
            var from = DateOnly.Parse(root.GetProperty("from").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            var through = DateOnly.Parse(root.GetProperty("through").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(through.DayNumber - from.DayNumber, (5 * 365) + 80, (5 * 366) + 100);   // five years and a quarter
            var tickers = root.GetProperty("tickers").EnumerateArray().ToArray();
            Assert.Equal(["SPY", "QQQ"], tickers.Select(ticker => ticker.GetProperty("ticker").GetString()));
            Assert.True(tickers[0].GetProperty("closes").GetArrayLength() > 1_300);
            Assert.Equal(21, tickers[0].GetProperty("dividends").GetArrayLength());
            Assert.True(root.GetProperty("riskFreeRates").GetArrayLength() > 0);
            Assert.True(root.GetProperty("missing").TryGetProperty("NOPE", out _));
        }

        [Fact]
        public async Task AnUnknownTenantIsNotFoundAndBadTickersAreRefused()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var server = ServerFixture.With([], services => services.AddSingleton<IMarketHistorySource>(new Vendor()));
            await server.InitializeAsync();
            var tool = new LedgerApiClient(server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(Joe10State.Generic(DateTimeOffset.UtcNow.AddDays(-3))), cancellationToken);

            using var unknown = await GetAsync(tool, server, $"v1/tenants/{Guid.NewGuid()}/market/history?tickers=SPY", cancellationToken);
            using var none = await GetAsync(tool, server, $"v1/tenants/{tenantId}/market/history", cancellationToken);
            using var bad = await GetAsync(tool, server, $"v1/tenants/{tenantId}/market/history?tickers=SPY;DROP", cancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        private static async Task<HttpResponseMessage> GetAsync(LedgerApiClient tool, ServerFixture server, string path, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            client::AtlasLedger.Model.Ledger.AddContractHeaders(request.Headers);
            await tool.AuthorizeAsync(request, cancellationToken);
            return await server.HttpClient.SendAsync(request, cancellationToken);
        }

        /// <summary>A vendor with every weekday's close, a quarterly dividend, and no NOPE.</summary>
        private sealed class Vendor : IMarketHistorySource
        {
            public Task<IReadOnlyList<DailyClose>> GetClosesAsync(string ticker, DateOnly from, DateOnly through, CancellationToken cancellationToken) =>
                ticker == "NOPE"
                    ? throw new HttpRequestException("404 from the vendor")
                    : Task.FromResult<IReadOnlyList<DailyClose>>(Enumerable.Range(0, through.DayNumber - from.DayNumber + 1)
                        .Select(from.AddDays)
                        .Where(day => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                        .Select(day => new DailyClose(day, 400m + (day.DayNumber % 30)))
                        .ToArray());

            public Task<IReadOnlyList<CashDividend>> GetDividendsAsync(string ticker, DateOnly from, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<CashDividend>>(Enumerable.Range(0, 21).Select(quarter => new CashDividend(from.AddMonths(3 * quarter).AddDays(10), null, 1.8m, "USD")).ToArray());

            public Task<IReadOnlyList<StockSplit>> GetSplitsAsync(string ticker, DateOnly from, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<StockSplit>>([]);

            public Task<IReadOnlyList<DailyRate>> GetRatesAsync(string code, DateOnly from, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<DailyRate>>([new DailyRate(from, 0.04m)]);
        }
    }
}
