// <copyright file="HoldingsEndToEndTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias client;

namespace AtlasLedger.Tests
{
    using System;
    using System.Collections.Generic;
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
            Assert.Equal(1 + 2, Joe10Account.History(state, 1_000_000m).Count);   // the deposit, then each purchase: one transaction each
            Assert.Equal(Text(Joe10Account.Trading(state, 1_000_000m)), Text(Joe10Account.Trading(state, 1_000_000m)));
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
            await tool.CommitTransactionsAsync(tenantId, Joe10Account.History(state, 1_000_000m), cancellationToken);

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
            await tool.CommitTransactionsAsync(tenantId, Joe10Account.History(state, 1_000_000m), cancellationToken);
            await Task.Delay(300, cancellationToken);   // a change would start a cycle; there is none to wait for
            holdings = await tool.GetHoldingsAsync(tenantId, cancellationToken);
            Assert.Equal(3_000m, Quantity(holdings, Joe10Account.AssetIdOf("AAA")));
            Assert.Equal(500_020m, Quantity(holdings, Joe10Account.DollarId));

            // An override sets a holding until it expires; the others are untouched.
            var now = DateTimeOffset.UtcNow;
            var overrideId = Guid.NewGuid();
            var overriding = new PositionOverrideEvent
            {
                AccountId = Joe10Account.AccountId,
                TradeDate = DateOnly.FromDateTime(now.UtcDateTime),
                ExpiresTime = now.AddHours(1),
                Reason = "The broker shows 2,500 until its correction posts tomorrow.",
                Items = [new ActivityItem(Joe10Account.AssetIdOf("AAA"), 2_500m, null)],
            };
            await tool.ApplyAsync(tenantId, new LedgerScript().AppendEvent(overriding, overrideId, EventSource.Operator, "override-1", now.AddMinutes(-5), now), cancellationToken);
            holdings = await WaitForAsync(tool, tenantId, found => Quantity(found, Joe10Account.AssetIdOf("AAA")) == 2_500m, cancellationToken);
            Assert.True(holdings.Holdings.Single(holding => holding.AssetId == Joe10Account.AssetIdOf("AAA")).IsOverridden);
            Assert.Equal(6_000m, Quantity(holdings, Joe10Account.AssetIdOf("BBB")));
        }

        /// <summary>
        /// What the rules derive is in the model's derived tables, which a client's Replica follows through sync: the
        /// positions, prices and net contributions arrive, an override changes the position in the Replica, and a client
        /// can't write them.
        /// </summary>
        /// <returns>A task.</returns>
        /// <summary>
        /// The whole test household: the Joe 10 portfolio holds its Play Money account, a checking account and a card; the
        /// SPY Benchmark beside it holds a Play Money account of SPY.  The card's balance is owed (negative dollars); the
        /// Joe 10 still governs only its brokerage account, so its model still trades it; the benchmark's model governs its
        /// own account.
        /// </summary>
        [Fact]
        public async Task TheHouseholdAddsCheckingACardAndTheSpyBenchmarkAndTheJoe10StillTrades()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var tool = new LedgerApiClient(this.server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            var state = ReadState();
            var spy = new SpyState(500m, 0.6m, 0.25m);
            await tool.SetServiceAsync("rules", isOn: true, cancellationToken: cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Household.Reference(state), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Weights(state), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Household.Weights(spy), cancellationToken);
            await tool.CommitTransactionsAsync(tenantId, Joe10Account.History(state, 1_000_000m), cancellationToken);
            await tool.CommitTransactionsAsync(tenantId, Joe10Household.History(state, spy, 1_000_000m), cancellationToken);

            var holdings = await WaitForAsync(tool, tenantId, found => found.Accounts.Count == 4 && found.Holdings.Any(holding => holding.AssetId == Joe10Household.SpyId), cancellationToken);
            decimal Held(Guid accountId, Guid assetId) => holdings.Holdings.Single(holding => holding.AccountId == accountId && holding.AssetId == assetId).Quantity;
            Assert.Equal(1_200m, Held(Joe10Household.BenchmarkAccountId, Joe10Household.SpyId));   // 60% of $1,000,000 at $500
            Assert.Equal(400_000m, Held(Joe10Household.BenchmarkAccountId, Joe10Account.DollarId));
            Assert.Equal(25_000m, Held(Joe10Household.CheckingId, Joe10Account.DollarId));
            Assert.Equal(-18_500m, Held(Joe10Household.CardId, Joe10Account.DollarId));

            AccountSummary Summary(Guid accountId) => holdings.Accounts.Single(account => account.AccountId == accountId);
            Assert.Equal("Joe 10", Summary(Joe10Account.AccountId).ModelName);
            Assert.Equal("SPY at 15%", Summary(Joe10Household.BenchmarkAccountId).ModelName);
            Assert.Null(Summary(Joe10Household.CheckingId).ModelId);   // counted in the Joe 10 portfolio, never traded
            Assert.Null(Summary(Joe10Household.CardId).ModelId);
            Assert.Equal(-18_500m, Summary(Joe10Household.CardId).MarketValue);
        }

        [Fact]
        public void TheBenchmarksShareInSpyIsTheThermostatsFromItsCloses()
        {
            // 22 closes: SPY alternating up and down 1%, so its daily returns' standard deviation is about 1% (about 16% a year).
            var path = Path.Combine(Path.GetTempPath(), $"spy-closes-{Guid.NewGuid():N}.json");
            var day = new DateOnly(2026, 10, 7);
            var rows = new List<string>();
            var adjusted = 500m;
            for (var index = 0; index < 22; index++, day = day.AddDays(1))
            {
                adjusted = index == 0 ? adjusted : adjusted * (index % 2 == 0 ? 1.01m : 0.99m);
                rows.Add($$"""{ "date": "{{day:yyyy-MM-dd}}", "close": {{(adjusted + 2m).ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "adjusted_close": {{adjusted.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }""");
            }

            File.WriteAllText(path, $"[{string.Join(",", rows)}]");
            try
            {
                var close = day.AddDays(-1);
                var spy = SpyState.Read(path, close);
                Assert.InRange(spy.Volatility!.Value, 0.158m, 0.166m);
                Assert.Equal(Math.Min(1m, 0.15m / spy.Volatility.Value), spy.StockShare);
                Assert.Equal(adjusted + 2m, spy.Price);   // bought at the close, not the adjusted close
                Assert.Throws<InvalidOperationException>(() => SpyState.Read(path, close.AddDays(5)));   // no row for that close
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public async Task TheDerivedTablesReachAReplicaThroughSync()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var tool = new LedgerApiClient(this.server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            var state = ReadState();
            await tool.SetServiceAsync("rules", isOn: true, cancellationToken: cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
            await tool.CommitTransactionsAsync(tenantId, Joe10Account.History(state, 1_000_000m), cancellationToken);
            await WaitForAsync(tool, tenantId, found => found.Holdings.Count == 3, cancellationToken);

            var replica = new client::AtlasLedger.Model.Ledger();
            var sync = tool.Sync(replica, tenantId, new[] { "Position", "EffectivePrice", "AccountFlow" });
            Assert.Contains("Asset", sync.Tables);   // the positions' parents come with them
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (sync.Partition.Positions.Count < 3 && DateTimeOffset.UtcNow < deadline)
            {
                await sync.SyncAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }

            decimal Held(Guid assetId) => sync.Partition.Positions.Single(position => position.AssetId == assetId).Quantity;
            Assert.Equal(3_000m, Held(Joe10Account.AssetIdOf("AAA")));
            Assert.Equal(500_020m, Held(Joe10Account.DollarId));
            Assert.Equal(1m, sync.Partition.EffectivePrices.Single(price => price.AssetId == Joe10Account.DollarId).Price);
            Assert.Equal(1_000_000m, sync.Partition.AccountFlows.Single().NetContributions);

            var now = DateTimeOffset.UtcNow;
            var overriding = new PositionOverrideEvent
            {
                AccountId = Joe10Account.AccountId,
                TradeDate = DateOnly.FromDateTime(now.UtcDateTime),
                ExpiresTime = now.AddHours(1),
                Reason = "A test of the Replica following a change.",
                Items = [new ActivityItem(Joe10Account.AssetIdOf("AAA"), 2_500m, null)],
            };
            await tool.ApplyAsync(tenantId, new LedgerScript().AppendEvent(overriding, Guid.NewGuid(), EventSource.Operator, "override-replica", now.AddMinutes(-5), now), cancellationToken);
            deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (Held(Joe10Account.AssetIdOf("AAA")) != 2_500m && DateTimeOffset.UtcNow < deadline)
            {
                await sync.SyncAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }

            Assert.Equal(2_500m, Held(Joe10Account.AssetIdOf("AAA")));
            Assert.Equal(6_000m, Held(Joe10Account.AssetIdOf("BBB")));
            var refused = await Assert.ThrowsAsync<LedgerApiException>(() => tool.ApplyAsync(tenantId, new LedgerScript().Insert("AccountFlow", new client::AtlasLedger.Model.AccountFlowDto { AccountId = Joe10Account.AccountId, NetContributions = 1m }), cancellationToken));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);   // the server writes derived tables; clients only read them
        }

        /// <summary>
        /// The simulated market feed moves prices a few pennies at a time from where they stand, and each move reaches a
        /// client's Replica through sync like any other change: what the website's pages show as prices tick.  It is the
        /// simulated broker's feed: the same account at a real broker (Schwab) is not priced by it.
        /// </summary>
        /// <returns>A task.</returns>
        [Fact]
        public async Task TheMarketFeedMovesPricesByPenniesThroughSync()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var server = ServerFixture.With(["--Services:StartOn=rules,market-feed", "--MarketFeed:TickInterval=00:00:00.100", "--MarketFeed:MoveChance=1", "--MarketFeed:MaximumMove=0.05"]);
            await server.InitializeAsync();
            var tool = new LedgerApiClient(server.HttpClient, DevelopmentCallers.Tool);
            var (tenantId, atSchwab) = (Guid.NewGuid(), Guid.NewGuid());
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(ReadState()), cancellationToken);
            await tool.CommitTransactionsAsync(tenantId, Joe10Account.History(ReadState(), 1_000_000m), cancellationToken);
            await tool.ApplyAsync(atSchwab, Joe10Account.Reference(ReadState(), EventSource.Schwab), cancellationToken);
            await tool.CommitTransactionsAsync(atSchwab, Joe10Account.History(ReadState(), 1_000_000m), cancellationToken);

            var replica = new client::AtlasLedger.Model.Ledger();
            var sync = tool.Sync(replica, tenantId, new[] { "EffectivePrice" });
            var real = tool.Sync(new client::AtlasLedger.Model.Ledger(), atSchwab, new[] { "EffectivePrice" });
            var aaa = Joe10Account.AssetIdOf("AAA");
            decimal? PriceOf(Guid assetId) => sync.Partition.EffectivePrices.SingleOrDefault(price => price.AssetId == assetId)?.Price;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (PriceOf(aaa) is null && DateTimeOffset.UtcNow < deadline)
            {
                await sync.SyncAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }

            var seen = new List<decimal> { PriceOf(aaa) ?? throw new InvalidOperationException("AAA was never priced.") };
            var started = DateTimeOffset.UtcNow;
            deadline = started.AddSeconds(15);
            while (seen.Count < 6 && DateTimeOffset.UtcNow < deadline)
            {
                await sync.SyncAsync(TimeSpan.FromSeconds(2), cancellationToken);
                if (PriceOf(aaa) is { } now && now != seen[^1])
                {
                    seen.Add(now);
                }
            }

            // Several moves arrived, each a whole number of pennies, and the price stayed within a few pennies a tick of
            // where it started (sync can fold several ticks into one change, so a change may be more than one move).
            Assert.True(seen.Count >= 6, $"Only {seen.Count - 1} price changes reached the Replica: {string.Join(", ", seen)}.");
            var ticks = (decimal)Math.Ceiling((DateTimeOffset.UtcNow - started).TotalMilliseconds / 100) + 2;
            Assert.All(seen.Zip(seen.Skip(1)), pair => Assert.Equal(0m, (pair.Second - pair.First) % 0.01m));
            Assert.All(seen, price => Assert.InRange(Math.Abs(price - seen[0]), 0m, ticks * 0.05m));
            Assert.Equal(1m, PriceOf(Joe10Account.DollarId));   // a currency's price is fixed: one dollar is a dollar

            // At Schwab, the feed leaves AAA alone: its price is its last trade's, as it was, however long the feed runs.
            await real.SyncAsync(TimeSpan.FromSeconds(2), cancellationToken);
            var lastTrade = real.Partition.EffectivePrices.Single(price => price.AssetId == aaa);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            await real.SyncAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
            var after = real.Partition.EffectivePrices.Single(price => price.AssetId == aaa);
            Assert.Equal((PriceSource.LastTrade, lastTrade.Price), (after.Source, after.Price));
        }

        /// <summary>
        /// The website's sync, as the person it signs in: the tables its pages read (TangentAtlas.Web's LiveLedgers.Tables)
        /// in a tenant where the person is a user, with Ledger.Read; and a 404 for a tenant where they are not.
        /// </summary>
        /// <returns>A task.</returns>
        [Fact]
        public async Task APersonSyncsTheWebsitesTablesInTheirOwnTenantOnly()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var tool = new LedgerApiClient(this.server.HttpClient, DevelopmentCallers.Tool);
            var person = new LedgerApiClient(this.server.HttpClient, DevelopmentCallers.Advisor);
            var (theirs, another) = (Guid.NewGuid(), Guid.NewGuid());
            var state = ReadState();
            await tool.SetServiceAsync("rules", isOn: true, cancellationToken: cancellationToken);
            foreach (var tenantId in new[] { theirs, another })
            {
                await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
                await tool.CommitTransactionsAsync(tenantId, Joe10Account.History(state, 1_000_000m), cancellationToken);
            }

            await tool.ApplyAsync(theirs, Joe10Account.Weights(state), cancellationToken);
            var userId = Guid.NewGuid();
            await tool.ApplyAsync(
                theirs,
                new LedgerScript()
                    .Insert("User", new client::AtlasLedger.Model.UserDto { UserId = userId, IdentityIssuer = DevelopmentCallers.Issuer, IdentitySubject = DevelopmentCallers.AdvisorSubject })
                    .Insert("Favorite", new client::AtlasLedger.Model.FavoriteDto { UserId = userId, PortfolioId = Joe10Account.PortfolioId, Position = 0 }),
                cancellationToken);
            await WaitForAsync(tool, theirs, found => found.Holdings.Count == 3, cancellationToken);

            string[] website = ["Position", "EffectivePrice", "CurrencyRate", "AccountFlow", "AccountDay", "AccountStatistic", "Weight", "Symbol", "Institution", "Edge", "Portfolio", "PortfolioDay", "PortfolioStatistic", "Favorite", "Restriction"];
            var sync = person.Sync(new client::AtlasLedger.Model.Ledger(), theirs, website);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (sync.Partition.Positions.Count < 3 && DateTimeOffset.UtcNow < deadline)
            {
                await sync.SyncAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }

            Assert.Equal(3, sync.Partition.Positions.Count);
            Assert.NotEmpty(sync.Partition.CurrencyRates);
            Assert.NotEmpty(sync.Partition.Weights);
            Assert.Equal(Joe10Account.AccountId, sync.Partition.Accounts.Single().AccountId);
            Assert.Empty(sync.Partition.Events);   // only what the website asked for

            // The household's tree, and the Joe 10 portfolio's books: one account, so the same as the account's.
            while (sync.Partition.PortfolioStatistics.Count == 0 && DateTimeOffset.UtcNow < deadline.AddSeconds(15))
            {
                await sync.SyncAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }

            Assert.Equal(2, sync.Partition.Edges.Count);
            Assert.Equal(Joe10Account.ModelId, sync.Partition.Portfolios.Single().ModelId);
            var portfolio = sync.Partition.PortfolioStatistics.Single();
            var account = sync.Partition.AccountStatistics.Single();
            Assert.Equal(Joe10Account.PortfolioId, portfolio.PortfolioId);
            Assert.Equal((account.From, account.Through, account.Value, account.Returns, account.TotalReturn, account.SharpeRatio), (portfolio.From, portfolio.Through, portfolio.Value, portfolio.Returns, portfolio.TotalReturn, portfolio.SharpeRatio));
            Assert.Equal(sync.Partition.AccountDays.Count, sync.Partition.PortfolioDays.Count);

            // The person's own favorites: the ledger tells them their user key (and no one else's); the tenant's users
            // aren't theirs to read.
            Assert.Equal(userId, await person.GetMyUserIdAsync(theirs, cancellationToken));
            Assert.Equal(Joe10Account.PortfolioId, sync.Partition.Favorites.Single(favorite => favorite.UserId == userId).PortfolioId);
            Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<LedgerApiException>(() => tool.GetMyUserIdAsync(theirs, cancellationToken))).StatusCode);   // a tool is no one

            var elsewhere = person.Sync(new client::AtlasLedger.Model.Ledger(), another, website);
            var refused = await Assert.ThrowsAsync<client::AtlasLedger.Model.Replica.LedgerSyncException>(() => elsewhere.SyncAsync(TimeSpan.Zero, cancellationToken));
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
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

            await tool.CommitTransactionsAsync(direct, Joe10Account.History(state, 1_000_000m), cancellationToken);
            await tool.CommitTransactionsAsync(viaOrders, Joe10Account.Trading(state, 1_000_000m), cancellationToken);

            // The rules book the fills as purchases (AAA's in two broker orders, each filled in two parts: four bookings).
            var expected = await WaitForAsync(tool, direct, found => found.Holdings.Count == 3, cancellationToken);
            var holdings = await WaitForAsync(tool, viaOrders, found => found.Holdings.Count == 3 && Quantity(found, Joe10Account.DollarId) == 500_020m, cancellationToken);
            Assert.Equal(
                expected.Holdings.Select(holding => (holding.AssetId, holding.Quantity, holding.CostBasis, holding.MarketValue)),
                holdings.Holdings.Select(holding => (holding.AssetId, holding.Quantity, holding.CostBasis, holding.MarketValue)));
            Assert.Equal(1_000_000m, holdings.Holdings.Sum(holding => holding.MarketValue!.Value));

            var orders = await tool.GetOrdersAsync(viaOrders, cancellationToken);
            Assert.Equal(2, orders.Orders.Count);
            Assert.All(orders.Orders, order => Assert.Equal(("Filled", 0m, 0m), (order.Status, order.Legs.Single().Unplaced, order.Legs.Single().Unallocated)));
            var aaa = orders.Orders.Single(order => order.Legs.Single().AssetId == Joe10Account.AssetIdOf("AAA"));
            Assert.Equal(["Filled", "Filled"], aaa.BrokerOrders.Select(brokerOrder => brokerOrder.Status));
            Assert.Equal((3_000m, 0m), (aaa.Legs.Single().Allocations.Single().Filled, aaa.Legs.Single().Allocations.Single().Remaining));
            using (var printed = new StringWriter())
            {
                await Commands.OrdersAsync(tool, viaOrders, printed, cancellationToken);
                Assert.Contains("Buy 3,000 AAA  filled 3,000, placed 3,000, unplaced 0", printed.ToString(), StringComparison.Ordinal);
            }

            // The fills post to holdings by derivation: nothing is written back.  Loading the trading events again changes
            // nothing: every event is idempotent on its source and source ID.
            var logged = (await tool.ListAsync<EventDto>(viaOrders, "Event", cancellationToken)).Count;
            Assert.DoesNotContain(await tool.ListAsync<EventDto>(viaOrders, "Event", cancellationToken), row => row.Source == EventSource.Rules);
            await tool.CommitTransactionsAsync(viaOrders, Joe10Account.Trading(state, 1_000_000m), cancellationToken);
            await Task.Delay(500, cancellationToken);
            Assert.Equal(logged, (await tool.ListAsync<EventDto>(viaOrders, "Event", cancellationToken)).Count);
            Assert.Equal(3_000m, Quantity(await tool.GetHoldingsAsync(viaOrders, cancellationToken), Joe10Account.AssetIdOf("AAA")));
        }

        [Fact]
        public async Task AnExchangeThatDoesNotBalanceIsLeftOutUntilCorrected()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var tool = new LedgerApiClient(this.server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            await tool.SetServiceAsync("rules", isOn: true, cancellationToken: cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(ReadState()), cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var lopsided = new TradeEvent
            {
                AccountId = Joe10Account.AccountId,
                TradeDate = DateOnly.FromDateTime(now.UtcDateTime),
                Items = [new ActivityItem(Joe10Account.AssetIdOf("AAA"), 10m, 1_000m), new ActivityItem(Joe10Account.DollarId, -900m, -900m)],
            };
            var lopsidedId = Guid.NewGuid();
            await tool.ApplyAsync(tenantId, new LedgerScript().AppendEvent(lopsided, lopsidedId, EventSource.Simulator, "lopsided", now), cancellationToken);

            // It stays in the log, is left out of the holdings, and is reported as a problem until a correction fixes it.
            var holdings = await WaitForAsync(tool, tenantId, found => found.Problems.Count > 0, cancellationToken);
            var problem = holdings.Problems.Single();
            Assert.Equal(lopsidedId, problem.EventId);
            Assert.Contains("doesn't balance", problem.Problem, StringComparison.Ordinal);
            Assert.DoesNotContain(holdings.Holdings, holding => holding.AssetId == Joe10Account.AssetIdOf("AAA"));

            var corrected = new CorrectionEvent
            {
                AccountId = Joe10Account.AccountId,
                TradeDate = lopsided.TradeDate,
                Corrects = lopsidedId,
                Reason = "The cost was $1,000.",
                Items = [new ActivityItem(Joe10Account.AssetIdOf("AAA"), 10m, 1_000m), new ActivityItem(Joe10Account.DollarId, -1_000m, -1_000m)],
            };
            await tool.ApplyAsync(tenantId, new LedgerScript().AppendEvent(corrected, Guid.NewGuid(), EventSource.Operator, "fix-lopsided", now), cancellationToken);
            holdings = await WaitForAsync(tool, tenantId, found => found.Holdings.Any(holding => holding.AssetId == Joe10Account.AssetIdOf("AAA")), cancellationToken);
            Assert.Equal((10m, -1_000m), (Quantity(holdings, Joe10Account.AssetIdOf("AAA")), Quantity(holdings, Joe10Account.DollarId)));
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

        private static string Text(IReadOnlyList<TransactionMessage> transactions) => EventMessages.WriteScript(transactions);

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
