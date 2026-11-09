// <copyright file="HoldingsEndToEndTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias client;

namespace AtlasLedger.Tests
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using client::AtlasLedger.Client.Api;
    using client::AtlasLedger.Client.Scripts;
    using client::AtlasLedger.Client.TestData;
    using client::AtlasLedger.Model;
    using Xunit;

    /// <summary>
    /// The event stream, end to end: the Joe 10 test account is built from a state file, loaded through the API like any
    /// script, and the rules add its events up into holdings and cash.
    /// </summary>
    public sealed class HoldingsEndToEndTests : IClassFixture<ServerFixture>
    {
        // Two companies at round prices: 60% and 40% of the stocks, half the account in stocks.
        private const string StateJson = """
            {
              "joe10": 1.0,
              "last_date": "2026-11-06",
              "portfolio": {
                "stocks_after": 0.5,
                "units": { "AAA": 0.003, "BBB": 0.0060006000600060006 },
                "weights": { "AAA": 0.6, "BBB": 0.4 }
              }
            }
            """;

        private readonly ServerFixture server;

        public HoldingsEndToEndTests(ServerFixture server)
        {
            this.server = server;
        }

        [Fact]
        public void TheStateFileGivesTheClosesPricesAndTheCloseInUtc()
        {
            var state = ReadState();

            Assert.Equal(new DateTimeOffset(2026, 11, 6, 21, 0, 0, TimeSpan.Zero), state.Close);   // 4 pm in New York, standard time
            Assert.Equal(new DateTimeOffset(2026, 7, 6, 20, 0, 0, TimeSpan.Zero), Joe10State.CloseOf(new DateOnly(2026, 7, 6)));   // daylight time
            Assert.Equal([("AAA", 100m), ("BBB", 33.33m)], state.Holdings.Select(holding => (holding.Ticker, holding.Price)));
        }

        [Fact]
        public void PurchasesAreWholeSharesRoundedDownAndTheRestStaysInCash()
        {
            var purchases = Joe10Account.Purchases(ReadState(), 1_000_000m);

            Assert.Equal([("AAA", 3_000m), ("BBB", 6_000m)], purchases.Select(purchase => (purchase.Ticker, purchase.Shares)));   // 200,000 ÷ 33.33 = 6,000.6
            Assert.Equal(499_980m, purchases.Sum(purchase => purchase.Shares * purchase.Price));
        }

        [Fact]
        public void BuildingTwiceGivesTheSameScripts()
        {
            var state = ReadState();

            Assert.Equal(Text(Joe10Account.Reference(state)), Text(Joe10Account.Reference(state)));
            Assert.Equal(Text(Joe10Account.History(state, 1_000_000m)), Text(Joe10Account.History(state, 1_000_000m)));
            Assert.Equal(1 + 2, Joe10Account.History(state, 1_000_000m).Operations.Count);   // the deposit, then a purchase per company
        }

        [Fact]
        public async Task TheRulesAddTheEventsUpToHoldingsAndCash()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var tool = new LedgerApiClient(this.server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            var state = ReadState();
            await tool.SetServiceAsync("compliance", isOn: true, cancellationToken: cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.History(state, 1_000_000m), cancellationToken);

            var holdings = await WaitForAsync(tool, tenantId, found => found.Holdings.Count == 2, cancellationToken);
            Assert.Equal(3_000m, Shares(holdings, "AAA"));
            Assert.Equal(6_000m, Shares(holdings, "BBB"));
            Assert.Equal(500_020m, holdings.Cash.Single().Cash);
            Assert.Equal(1_000_000m, holdings.Holdings.Sum(holding => holding.MarketValue!.Value) + holdings.Cash.Single().Cash);
            Assert.All(holdings.Holdings, holding => Assert.Equal("LastTrade", holding.PriceSource));   // no market feed yet

            // Loading the history again changes nothing: each event is idempotent on its source and source ID.
            await tool.ApplyAsync(tenantId, Joe10Account.History(state, 1_000_000m), cancellationToken);
            await Task.Delay(300, cancellationToken);   // a change would start a cycle; there is none to wait for
            holdings = await tool.GetHoldingsAsync(tenantId, cancellationToken);
            Assert.Equal(3_000m, Shares(holdings, "AAA"));
            Assert.Equal(500_020m, holdings.Cash.Single().Cash);

            // An operator's override sets a holding until it expires; the others are untouched.
            var now = DateTimeOffset.UtcNow;
            await tool.ApplyAsync(tenantId, new LedgerScript().Insert("PositionOverride", new PositionOverrideDto
            {
                PositionOverrideId = Guid.NewGuid(),
                AccountId = Joe10Account.AccountId,
                SecurityId = Joe10Account.SecurityIdOf("AAA"),
                Quantity = 2_500m,
                ExpiresTime = now.AddHours(1),
                EffectiveTime = now.AddMinutes(-5),
                EnteredTime = now,
                Source = EventSource.Operator,
                SourceId = "override-1",
                Reason = "The broker shows 2,500 until its correction posts tomorrow.",
            }), cancellationToken);
            holdings = await WaitForAsync(tool, tenantId, found => Shares(found, "AAA") == 2_500m, cancellationToken);
            Assert.True(holdings.Holdings.Single(holding => holding.SecurityId == Joe10Account.SecurityIdOf("AAA")).IsOverridden);
            Assert.Equal(6_000m, Shares(holdings, "BBB"));
        }

        [Fact]
        public async Task WithoutTheRulesTheHoldingsAnswer409()
        {
            var tool = new LedgerApiClient(this.server.HttpClient, DevelopmentCallers.Tool);
            var exception = await Assert.ThrowsAsync<LedgerApiException>(() => tool.GetHoldingsAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
            Assert.Contains(exception.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.NotFound });
        }

        private static decimal Shares(TenantHoldings holdings, string ticker) =>
            holdings.Holdings.Single(holding => holding.SecurityId == Joe10Account.SecurityIdOf(ticker)).Quantity;

        private static Joe10State ReadState()
        {
            var path = Path.Combine(Path.GetTempPath(), $"joe10-state-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, StateJson);
            try
            {
                return Joe10State.Read(path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static string Text(LedgerScript script)
        {
            using var stream = new MemoryStream();
            script.WriteAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        private static async Task<TenantHoldings> WaitForAsync(LedgerApiClient api, Guid tenantId, Func<TenantHoldings, bool> condition, CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (true)
            {
                try
                {
                    var holdings = await api.GetHoldingsAsync(tenantId, cancellationToken);
                    if (condition(holdings))
                    {
                        return holdings;
                    }
                }
                catch (LedgerApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict && DateTimeOffset.UtcNow < deadline)
                {
                    // The rules haven't run for the tenant yet.
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException("The holdings didn't reach the expected state in time.");
                }

                await Task.Delay(100, cancellationToken);
            }
        }
    }
}
