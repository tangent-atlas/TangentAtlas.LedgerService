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
    using client::AtlasLedger.Client;
    using client::AtlasLedger.Client.Api;
    using client::AtlasLedger.Client.Scripts;
    using client::AtlasLedger.Client.TestData;
    using client::AtlasLedger.Model;
    using Xunit;

    /// <summary>
    /// The transaction log, end to end: the Joe 10 test account is built from a state file, loaded through the API like any
    /// script, and the rules add its transactions up into holdings, US dollars among them.
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
        public void TheGenericJoe10NeedsNoStateFile()
        {
            // Tuesday, November 10, 2026, 8:30 pm in New York: the last close was that afternoon; on Sunday it was Friday's.
            var tuesday = Joe10State.Generic(new DateTimeOffset(2026, 11, 11, 1, 30, 0, TimeSpan.Zero));
            Assert.Equal(Joe10State.CloseOf(new DateOnly(2026, 11, 10)), tuesday.Close);
            Assert.Equal(Joe10State.CloseOf(new DateOnly(2026, 11, 6)), Joe10State.Generic(new DateTimeOffset(2026, 11, 8, 15, 0, 0, TimeSpan.Zero)).Close);
            Assert.Equal((10, 1m, 0.5m), (tuesday.Holdings.Count, tuesday.Holdings.Sum(holding => holding.Weight), tuesday.StockShare));
            Assert.Equal(50, Joe10Account.Purchases(tuesday, 1_000_000m).Sum(purchase => purchase.Shares) / 100);   // $50,000 each at $100
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
            Assert.Equal(2 + (2 * 3), Joe10Account.History(state, 1_000_000m).Operations.Count);   // the deposit and its line, then each purchase and its two lines
        }

        [Fact]
        public async Task TheRulesAddTheTransactionsUpToHoldingsWithCashAsAnAssetLikeAnyOther()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var tool = new LedgerApiClient(this.server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            var state = ReadState();
            await tool.SetServiceAsync("rules", isOn: true, cancellationToken: cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.History(state, 1_000_000m), cancellationToken);

            var holdings = await WaitForAsync(tool, tenantId, found => found.Holdings.Count == 3, cancellationToken);
            Assert.Equal(3_000m, Quantity(holdings, Joe10Account.AssetIdOf("AAA")));
            Assert.Equal(6_000m, Quantity(holdings, Joe10Account.AssetIdOf("BBB")));
            var dollars = holdings.Holdings.Single(holding => holding.AssetId == Joe10Account.DollarId);
            Assert.Equal(AssetType.Cash, dollars.AssetType);
            Assert.Equal(500_020m, dollars.Quantity);
            Assert.Equal("Market", dollars.PriceSource);   // USD in USD = 1, from the initial prices, not assumed
            Assert.Equal(1m, dollars.Price);
            Assert.All(holdings.Holdings, holding => Assert.Equal("USD", holding.Currency));   // valued in the account's currency
            Assert.Equal(
                [("CAD", "CAD", 1m), ("EUR", "EUR", 1m), ("GBP", "GBP", 1m), ("USD", "USD", 1m)],
                holdings.Rates.Select(rate => (rate.FromCurrency, rate.ToCurrency, rate.Rate)));   // the initial prices; no cross rates assumed
            Assert.Equal(1_000_000m, holdings.Holdings.Sum(holding => holding.MarketValue!.Value));
            Assert.All(holdings.Holdings.Where(holding => holding.AssetType == AssetType.Equity), holding => Assert.Equal("LastTrade", holding.PriceSource));

            // Loading the history again changes nothing: each transaction is idempotent on its source and source ID.
            await tool.ApplyAsync(tenantId, Joe10Account.History(state, 1_000_000m), cancellationToken);
            await Task.Delay(300, cancellationToken);   // a change would start a cycle; there is none to wait for
            holdings = await tool.GetHoldingsAsync(tenantId, cancellationToken);
            Assert.Equal(3_000m, Quantity(holdings, Joe10Account.AssetIdOf("AAA")));
            Assert.Equal(500_020m, Quantity(holdings, Joe10Account.DollarId));

            // An override sets a holding until it expires; the others are untouched.
            var now = DateTimeOffset.UtcNow;
            var overrideId = Guid.NewGuid();
            await tool.ApplyAsync(tenantId, new LedgerScript()
                .Insert("Transaction", new TransactionDto
                {
                    TransactionId = overrideId,
                    Kind = TransactionKind.Override,
                    ExpiresTime = now.AddHours(1),
                    EffectiveTime = now.AddMinutes(-5),
                    EnteredTime = now,
                    Source = EventSource.Operator,
                    SourceId = "override-1",
                    Reason = "The broker shows 2,500 until its correction posts tomorrow.",
                })
                .Insert("TransactionLine", new TransactionLineDto { TransactionId = overrideId, LineNumber = 1, AccountId = Joe10Account.AccountId, AssetId = Joe10Account.AssetIdOf("AAA"), Effect = LineEffect.SetTo, Quantity = 2_500m }), cancellationToken);
            holdings = await WaitForAsync(tool, tenantId, found => Quantity(found, Joe10Account.AssetIdOf("AAA")) == 2_500m, cancellationToken);
            Assert.True(holdings.Holdings.Single(holding => holding.AssetId == Joe10Account.AssetIdOf("AAA")).IsOverridden);
            Assert.Equal(6_000m, Quantity(holdings, Joe10Account.AssetIdOf("BBB")));
        }

        [Fact]
        public async Task TradingThroughOrdersReachesTheSameHoldingsAsBookingThePurchases()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var tool = new LedgerApiClient(this.server.HttpClient, DevelopmentCallers.Tool);
            var (direct, viaOrders) = (Guid.NewGuid(), Guid.NewGuid());
            var state = ReadState();
            await tool.SetServiceAsync("rules", isOn: true, cancellationToken: cancellationToken);
            foreach (var tenantId in new[] { direct, viaOrders })
            {
                await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
            }

            await tool.ApplyAsync(direct, Joe10Account.History(state, 1_000_000m), cancellationToken);
            await tool.ApplyAsync(viaOrders, Joe10Account.Trading(state, 1_000_000m), cancellationToken);

            // The rules book the fills as purchases (AAA's in two broker orders, each filled in two parts: four bookings).
            var expected = await WaitForAsync(tool, direct, found => found.Holdings.Count == 3, cancellationToken);
            var holdings = await WaitForAsync(tool, viaOrders, found => found.Holdings.Count == 3 && Quantity(found, Joe10Account.DollarId) == 500_020m, cancellationToken);
            Assert.Equal(
                expected.Holdings.Select(holding => (holding.AssetId, holding.Quantity, holding.CostBasis, holding.MarketValue)),
                holdings.Holdings.Select(holding => (holding.AssetId, holding.Quantity, holding.CostBasis, holding.MarketValue)));
            Assert.Equal(1_000_000m, holdings.Holdings.Sum(holding => holding.MarketValue!.Value));

            var orders = await tool.GetOrdersAsync(viaOrders, cancellationToken);
            Assert.Equal(2, orders.Orders.Count);
            Assert.All(orders.Orders, order => Assert.Equal(("Filled", 0m, 0m), (order.Status, order.Unplaced, order.Unallocated)));
            var aaa = orders.Orders.Single(order => order.AssetId == Joe10Account.AssetIdOf("AAA"));
            Assert.Equal(["Filled", "Filled"], aaa.BrokerOrders.Select(brokerOrder => brokerOrder.Status));
            Assert.Equal((3_000m, 0m), (aaa.Allocations.Single().Filled, aaa.Allocations.Single().Remaining));
            using (var printed = new StringWriter())
            {
                await Commands.OrdersAsync(tool, viaOrders, printed, cancellationToken);
                Assert.Contains("Buy 3,000 AAA  Filled  filled 3,000, placed 3,000, unplaced 0  (Rebalance)", printed.ToString(), StringComparison.Ordinal);
            }

            var bookings = (await tool.ListAsync<TransactionDto>(viaOrders, "Transaction", cancellationToken)).Where(transaction => transaction.Source == EventSource.Rules).ToArray();
            Assert.Equal(5, bookings.Length);
            Assert.All(bookings, booking => Assert.NotNull(booking.ExecutionReportId));

            // Loading the trading events again changes nothing: every row is the same row, and no fill is booked twice.
            await tool.ApplyAsync(viaOrders, Joe10Account.Trading(state, 1_000_000m), cancellationToken);
            await Task.Delay(500, cancellationToken);
            Assert.Equal(5, (await tool.ListAsync<TransactionDto>(viaOrders, "Transaction", cancellationToken)).Count(transaction => transaction.Source == EventSource.Rules));
            Assert.Equal(3_000m, Quantity(await tool.GetHoldingsAsync(viaOrders, cancellationToken), Joe10Account.AssetIdOf("AAA")));
        }

        [Fact]
        public async Task AnExchangeThatDoesNotBalanceRaisesAnAlert()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var tool = new LedgerApiClient(this.server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            await tool.SetServiceAsync("rules", isOn: true, cancellationToken: cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(ReadState()), cancellationToken);
            var buyId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await tool.ApplyAsync(tenantId, new LedgerScript()
                .Insert("Transaction", new TransactionDto { TransactionId = buyId, Kind = TransactionKind.Buy, EffectiveTime = now, EnteredTime = now, Source = EventSource.Simulator, SourceId = "lopsided" })
                .Insert("TransactionLine", new TransactionLineDto { TransactionId = buyId, LineNumber = 1, AccountId = Joe10Account.AccountId, AssetId = Joe10Account.AssetIdOf("AAA"), Effect = LineEffect.Change, Quantity = 10m, Amount = 1_000m })
                .Insert("TransactionLine", new TransactionLineDto { TransactionId = buyId, LineNumber = 2, AccountId = Joe10Account.AccountId, AssetId = Joe10Account.DollarId, Effect = LineEffect.Change, Quantity = -900m, Amount = -900m }), cancellationToken);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            AlertDto? alert = null;
            while (alert is null && DateTimeOffset.UtcNow < deadline)
            {
                alert = (await tool.ListAsync<AlertDto>(tenantId, "Alert", cancellationToken)).FirstOrDefault(item => item.Rule.StartsWith("UnbalancedTransaction", StringComparison.Ordinal));
                await Task.Delay(100, cancellationToken);
            }

            Assert.NotNull(alert);
            Assert.Equal(Joe10Account.AccountId, alert.NodeId);
            Assert.Contains("lopsided", alert.Message, StringComparison.Ordinal);
        }

        private static decimal Quantity(TenantHoldings holdings, Guid assetId) =>
            holdings.Holdings.Single(holding => holding.AssetId == assetId).Quantity;

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
