// <copyright file="AccountHistoryTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias client;
extern alias server;

namespace AtlasLedger.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using client::AtlasLedger.Client.Api;
    using client::AtlasLedger.Client.TestData;
    using Microsoft.Extensions.DependencyInjection;
    using server::AtlasLedger.Model.Primary;
    using server::AtlasLedger.Server.Brokers;
    using server::AtlasLedger.Server.Holdings;
    using server::AtlasLedger.Server.Jobs;
    using server::AtlasLedger.Server.Market;
    using Xunit;
    using AccountDto = server::AtlasLedger.Model.AccountDto;
    using AssetDto = server::AtlasLedger.Model.AssetDto;
    using SymbolDto = server::AtlasLedger.Model.SymbolDto;

    /// <summary>
    /// An account's history at the closes, its statistics, the paper broker's corporate actions and fills at the close, and
    /// the market history service that feeds them.
    /// </summary>
    public sealed class AccountHistoryTests
    {
        private static readonly Guid Account = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

        private static readonly Guid Stock = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

        private static readonly Guid Dollar = Guid.Parse("00000000-0000-0000-0000-0000000000c1");

        private static readonly DateOnly Monday = new DateOnly(2026, 11, 2);

        /// <summary>
        /// The worked example, checked by hand (a separate calculation in Python, recorded here): $1,000 deposited on Monday,
        /// 10 shares of X bought at Monday's close at $50, $1,000 more deposited on Wednesday; X closes at 50, 55, 52, 56, 54;
        /// the bill rate is 5.04% Monday and Tuesday, 2.52% after.
        /// </summary>
        [Fact]
        public void TheHistoryAndStatisticsMatchTheHandCalculation()
        {
            var history = Build(Lines(), isPaper: false);

            Assert.Equal([1000m, 1050m, 2020m, 2060m, 2040m], history.Days.Select(day => day.Value!.Value));
            Assert.Equal([1000m, 0m, 1000m, 0m, 0m], history.Days.Select(day => day.Flow));
            Assert.Equal([null, 0.05m, -0.0285714286m, 0.0198019802m, -0.0097087379m], history.Days.Select(day => day.Return));
            Assert.Equal([null, 0.0002m, 0.0002m, 0.0001m, 0.0001m], history.Days.Select(day => day.RiskFreeReturn));   // the day before's rate ÷ 252

            var statistics = history.Statistics;
            Assert.Equal((Monday, Monday.AddDays(4), 4), (statistics.From!.Value, statistics.Through!.Value, statistics.Returns));
            Assert.Equal(2040m, statistics.Value);
            Assert.Equal(2000m, statistics.NetFlows);
            Assert.Equal(0.030099m, statistics.TotalReturn);
            Assert.Equal(5.477018m, statistics.AnnualizedReturn);
            Assert.Equal(0.546407m, statistics.Volatility);
            Assert.Equal(3.565805m, statistics.SharpeRatio);
            Assert.Equal(0.028571m, statistics.MaximumDrawdown);
            Assert.False(statistics.IsRanked);   // 4 returns; 20 to rank
            Assert.Empty(statistics.Gaps);
        }

        [Fact]
        public void APaperAccountIsOwedTheCorporateActionsOfWhatItHeldTheCloseBefore()
        {
            var history = Build(Lines(), isPaper: true);

            Assert.Collection(
                history.Due,
                dividend => Assert.Equal((new DateOnly(2026, 11, 5), false, 10m, 0.25m, (Guid?)Dollar), (dividend.Date, dividend.IsSplit, dividend.SharesHeld, dividend.Value, dividend.CashAssetId)),
                split => Assert.Equal((new DateOnly(2026, 11, 6), true, 10m, 2m), (split.Date, split.IsSplit, split.SharesHeld, split.Value)));
            var income = Assert.IsType<IncomeEvent>(AccountHistoryFold.Event(history.Due[0]));
            Assert.Equal((Dollar, 2.50m, 2.50m, IncomeType.Dividend, (Guid?)Stock), (income.Items[0].AssetId, income.Items[0].Quantity, income.Items[0].Amount!.Value, income.IncomeType, income.PaidByAssetId));
            var split = Assert.IsType<ReceiveAndDeliverEvent>(AccountHistoryFold.Event(history.Due[1]));
            Assert.Equal((Stock, 10m, 0m, DeliveryReason.Split), (split.Items[0].AssetId, split.Items[0].Quantity, split.Items[0].Amount!.Value, split.Reason));

            // Once the dividend's event is posted, it is no longer owed; an account held elsewhere is owed nothing (its broker reports them).
            var credited = Lines().Append(Line(AccountHistoryFold.EventId(history.Due[0].SourceId), Dollar, 2.50m, 2.50m, AccountHistoryFold.OpenTime(new DateOnly(2026, 11, 5)), EventType.Income));
            Assert.Equal([true], Build(credited, isPaper: true).Due.Select(item => item.IsSplit));
            Assert.Empty(Build(Lines(), isPaper: false).Due);
        }

        [Fact]
        public void TheMarketDaysAreThoseOfTheExchange()
        {
            var thanksgiving = new DateOnly(2026, 11, 26);
            Assert.False(MarketCalendar.IsMarketDay(thanksgiving));
            Assert.Equal(new DateOnly(2026, 11, 25), MarketCalendar.PreviousMarketDay(new DateOnly(2026, 11, 27)));
            Assert.Equal(new DateOnly(2026, 11, 27), MarketCalendar.NextCloseDay(MarketCalendar.CloseTime(new DateOnly(2026, 11, 25))));   // at the close: the next day's
            Assert.Equal(new DateOnly(2026, 11, 25), MarketCalendar.NextCloseDay(MarketCalendar.CloseTime(new DateOnly(2026, 11, 25)).AddMinutes(-15)));
            var publishedAfter = new TimeOnly(16, 30);
            Assert.Equal(new DateOnly(2026, 11, 24), MarketCalendar.LatestClosedDay(MarketCalendar.CloseTime(new DateOnly(2026, 11, 25)), publishedAfter));
            Assert.Equal(new DateOnly(2026, 11, 25), MarketCalendar.LatestClosedDay(MarketCalendar.CloseTime(new DateOnly(2026, 11, 25)).AddHours(1), publishedAfter));
            Assert.Equal(new DateOnly(2026, 11, 13), MarketCalendar.LatestClosedDay(MarketCalendar.CloseTime(new DateOnly(2026, 11, 15)), publishedAfter));   // a Sunday
            Assert.Equal("BRK-B.US", EodhdTreasurySource.VendorSymbol("BRK.B"));
            Assert.Equal(4m, EodhdTreasurySource.Ratio("4.000000/1.000000"));
            Assert.Equal(0.1m, EodhdTreasurySource.Ratio("1/10"));
            var treasury = "Date,\"4 WEEKS BANK DISCOUNT\",\"4 WEEKS COUPON EQUIVALENT\",\"13 WEEKS BANK DISCOUNT\",\"13 WEEKS COUPON EQUIVALENT\"\n11/13/2026,3.95,4.02,4.13,4.23\n11/12/2026,3.97,4.04,N/A,N/A\n";
            Assert.Equal([new DailyRate(new DateOnly(2026, 11, 13), 0.0423m)], EodhdTreasurySource.ReadTreasuryBillRates(treasury));
        }

        [Fact]
        public async Task ThePaperBrokerFillsAtTheNextCloseOnceItIsLoaded()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var ledger = new server::AtlasLedger.Model.Ledger();
            var tenantId = Guid.NewGuid();
            var partition = ledger.GetPartition(tenantId);
            var day = new DateOnly(2026, 11, 12);
            var clock = new SettableClock(MarketCalendar.CloseTime(day).AddMinutes(-15));
            partition.PublishPrice(new Price { Scheme = SymbolScheme.Ticker, Code = "X", Currency = "USD", Last = 49m, AsOf = clock.GetUtcNow() });
            var broker = new SimulatedBroker(ledger, clock, Microsoft.Extensions.Options.Options.Create(new BrokerOptions { SimulatedFills = SimulatedFills.AtNextClose }));
            var order = new BrokerOrderRequest(tenantId, Guid.NewGuid(), Guid.NewGuid(), [new BrokerLegRequest(1, Instruction.Buy, "X", "USD", 10m)], OrderType.Market, null, TimeInForce.Day, "paper");

            var placed = await broker.PlaceAsync(order, cancellationToken);
            clock.Now = MarketCalendar.CloseTime(day).AddHours(1);
            var before = await broker.GetReportsAsync(order, cancellationToken);
            partition.PublishClosingPrice(new ClosingPrice { Scheme = SymbolScheme.Ticker, Code = "X", Currency = "USD", Date = day, Close = 50m, AsOf = MarketCalendar.CloseTime(day) });
            var after = await broker.GetReportsAsync(order, cancellationToken);

            Assert.Equal([ExecutionReportType.Accepted], placed.Select(report => report.ReportType));
            Assert.Equal([ExecutionReportType.Accepted], before.Select(report => report.ReportType));   // not at the live price
            var fill = after[^1];
            Assert.Equal((ExecutionReportType.Fill, (decimal?)10m, (decimal?)50m, MarketCalendar.CloseTime(day)), (fill.ReportType, fill.Quantity, fill.Price, fill.ReportTime));
        }

        /// <summary>
        /// End to end: the Joe 10 bought at a close eight market days back; the market history service loads the closes, a
        /// dividend, a split and the bill rate from a stand-in vendor; the rules credit the dividend and split to the paper
        /// account and measure it at every close.
        /// </summary>
        [Fact]
        public async Task APaperAccountIsValuedAtEveryCloseAndCreditedItsDividendsAndSplits()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var latest = MarketCalendar.LatestClosedDay(DateTimeOffset.UtcNow, new TimeOnly(16, 30));
            var marketDays = new List<DateOnly> { latest };
            while (marketDays.Count < 9)
            {
                marketDays.Insert(0, MarketCalendar.PreviousMarketDay(marketDays[0]));
            }

            var start = marketDays[0];
            var vendor = new StandInVendor(marketDays, dividendDay: marketDays[3], splitDay: marketDays[5]);
            await using var server = ServerFixture.With(
                ["--Services:StartOn=rules", "--MarketHistory:RefreshInterval=00:00:00.500"],
                services => services.AddSingleton<IMarketHistorySource>(vendor));
            await server.InitializeAsync();
            var tool = new LedgerApiClient(server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            var state = Joe10State.Generic(MarketCalendar.CloseTime(start).AddMinutes(1));
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
            await tool.CommitTransactionsAsync(tenantId, Joe10Account.History(state, 1_000_000m), cancellationToken);
            await tool.SetServiceAsync("market-history", isOn: true, cancellationToken: cancellationToken);

            // What the account should be worth at each close, worked out directly: the cash left, each holding at the day's
            // price (MSFT's shares doubled and its price halved from the split), and the dividend from its ex-date on.
            var purchases = Joe10Account.Purchases(state, 1_000_000m);
            var cashLeft = 1_000_000m - purchases.Sum(purchase => purchase.Shares * purchase.Price);
            var nvdaShares = purchases.Single(purchase => purchase.Ticker == "NVDA").Shares;
            var expected = marketDays.Select((day, k) =>
                cashLeft
                + purchases.Sum(purchase => purchase.Shares * (purchase.Ticker == "MSFT" && k >= 5 ? 2m : 1m) * vendor.Close(purchase.Ticker, day))
                + (k >= 3 ? nvdaShares * StandInVendor.Dividend : 0m)).ToArray();

            client::AtlasLedger.Client.Api.AccountStatistics? account = null;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                try
                {
                    var statistics = await tool.GetStatisticsAsync(tenantId, days: true, cancellationToken);
                    account = statistics.Accounts.SingleOrDefault(item => item.Statistics.AccountId == Joe10Account.AccountId);
                    if (account?.Days is { Count: 9 } days && days[^1].Value == expected[^1])
                    {
                        break;
                    }
                }
                catch (LedgerApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                }

                await Task.Delay(100, cancellationToken);
            }

            Assert.NotNull(account);
            Assert.Equal(marketDays, account.Days!.Select(day => day.Date));
            Assert.Equal(expected, account.Days!.Select(day => day.Value!.Value));
            Assert.Equal(8, account.Statistics.Returns);
            Assert.Equal(1_000_000m, account.Statistics.NetFlows);
            Assert.All(account.Days!.Skip(1), day => Assert.Equal(0.0002m, day.RiskFreeReturn));
            var returns = expected.Zip(expected.Skip(1), (before, after) => (double)(after / before) - 1d).ToArray();
            var excess = returns.Select(item => item - 0.0002d).ToArray();
            var mean = excess.Average();
            var sharpe = mean / Math.Sqrt(excess.Sum(item => (item - mean) * (item - mean)) / (excess.Length - 1)) * Math.Sqrt(252);
            Assert.Equal(sharpe, (double)account.Statistics.SharpeRatio!.Value, sharpe * 1e-6);   // the server's daily returns are kept to 10 places

            // The credits are events in the log, from the paper broker, recorded once.
            var events = await tool.ListAsync<client::AtlasLedger.Model.EventDto>(tenantId, "Event", cancellationToken);
            Assert.Single(events, row => row.EventType == EventType.Income && row.Source == EventSource.Simulator);
            Assert.Single(events, row => row.EventType == EventType.ReceiveAndDeliver && row.Source == EventSource.Simulator);

            // The vendor was asked for each ticker's closes, not once per pass.
            Assert.All(vendor.CloseRequests, pair => Assert.True(pair.Value <= 2, $"{pair.Key}: {pair.Value} requests"));
        }

        private static AccountHistory Build(IEnumerable<PostedLine> lines, bool isPaper)
        {
            var institution = Guid.NewGuid();
            var account = new AccountFact(new AccountDto { AccountId = Account, AccountRole = AccountRole.ManagedAccount, AccountType = AccountType.Brokerage, Currency = "USD", InstitutionId = institution });
            var assets = new[]
            {
                new AssetFact(new AssetDto { AssetId = Stock, AssetType = AssetType.Equity, QuoteCurrency = "USD", QuantityFactor = 1m, PriceFactor = 1m }),
                new AssetFact(new AssetDto { AssetId = Dollar, AssetType = AssetType.Cash, QuoteCurrency = "USD", QuantityFactor = 1m, PriceFactor = 1m }),
            };
            var symbols = new[]
            {
                new SymbolFact(new SymbolDto { Scheme = SymbolScheme.Ticker, Code = "X", NodeId = Stock }),
                new SymbolFact(new SymbolDto { Scheme = SymbolScheme.Currency, Code = "USD", NodeId = Dollar }),
            };
            var prices = new[] { 50m, 55m, 52m, 56m, 54m };
            var closes = prices.Select((price, index) => new ClosingPriceFact(new ClosingPrice { Scheme = SymbolScheme.Ticker, Code = "X", Currency = "USD", Date = Monday.AddDays(index), Close = price, AsOf = MarketCalendar.CloseTime(Monday.AddDays(index)) }));
            var rates = new[] { 0.0504m, 0.0504m, 0.0252m, 0.0252m, 0.0252m }.Select((rate, index) => new InterestRateFact(new InterestRate { Code = AccountHistoryFold.RiskFreeRate, Date = Monday.AddDays(index), Rate = rate, AsOf = DateTimeOffset.UtcNow }));
            var dividends = new[] { new DividendFact(new Dividend { Scheme = SymbolScheme.Ticker, Code = "X", Currency = "USD", ExDate = Monday.AddDays(3), PaymentDate = Monday.AddDays(10), Amount = 0.25m, AsOf = DateTimeOffset.UtcNow }) };
            var splits = new[] { new SplitFact(new Split { Scheme = SymbolScheme.Ticker, Code = "X", Date = Monday.AddDays(4), Ratio = 2m, AsOf = DateTimeOffset.UtcNow }) };
            return AccountHistoryFold.Build(account, isPaper, lines, assets, symbols, closes, rates, dividends, splits);
        }

        private static IEnumerable<PostedLine> Lines()
        {
            var deposit = Guid.NewGuid();
            var trade = Guid.NewGuid();
            var second = Guid.NewGuid();
            return
            [
                Line(deposit, Dollar, 1000m, 1000m, MarketCalendar.CloseTime(Monday).AddHours(-6), EventType.CashMovement, isExternal: true),
                Line(trade, Stock, 10m, 500m, MarketCalendar.CloseTime(Monday), EventType.Trade),
                Line(trade, Dollar, -500m, -500m, MarketCalendar.CloseTime(Monday), EventType.Trade, item: 2),
                Line(second, Dollar, 1000m, 1000m, MarketCalendar.CloseTime(Monday.AddDays(2)).AddHours(-4), EventType.CashMovement, isExternal: true),
            ];
        }

        private static PostedLine Line(Guid eventId, Guid assetId, decimal quantity, decimal amount, DateTimeOffset time, EventType kind, bool isExternal = false, int item = 1) =>
            new PostedLine(eventId, item, Account, assetId, LineEffect.Change, quantity, amount, kind, time, null, 1, isExternal);

        private sealed class SettableClock : TimeProvider
        {
            public SettableClock(DateTimeOffset now)
            {
                this.Now = now;
            }

            public DateTimeOffset Now { get; set; }

            public override DateTimeOffset GetUtcNow() => this.Now;
        }

        /// <summary>
        /// A vendor for the test: every ticker at $100 through the first day, then $1 more each market day (MSFT halved from
        /// its 2-for-1 split); NVDA pays $1.00 a share; the bill rate is 5.04%.
        /// </summary>
        private sealed class StandInVendor : IMarketHistorySource
        {
            public const decimal Dividend = 1.00m;

            private readonly IReadOnlyList<DateOnly> days;

            private readonly DateOnly dividendDay;

            private readonly DateOnly splitDay;

            public StandInVendor(IReadOnlyList<DateOnly> days, DateOnly dividendDay, DateOnly splitDay)
            {
                this.days = days;
                this.dividendDay = dividendDay;
                this.splitDay = splitDay;
            }

            public ConcurrentDictionary<string, int> CloseRequests { get; } = new ConcurrentDictionary<string, int>();

            public decimal Close(string ticker, DateOnly day)
            {
                var k = Math.Max(0, this.days.ToList().FindIndex(item => item == day));
                var price = 100m + k;
                return ticker == "MSFT" && day >= this.splitDay ? price / 2m : price;
            }

            public Task<IReadOnlyList<DailyClose>> GetClosesAsync(string ticker, DateOnly from, DateOnly through, CancellationToken cancellationToken)
            {
                this.CloseRequests.AddOrUpdate(ticker, 1, (_, count) => count + 1);
                return Task.FromResult<IReadOnlyList<DailyClose>>(this.days.Where(day => day >= from && day <= through).Select(day => new DailyClose(day, this.Close(ticker, day))).ToArray());
            }

            public Task<IReadOnlyList<CashDividend>> GetDividendsAsync(string ticker, DateOnly from, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<CashDividend>>(ticker == "NVDA" ? [new CashDividend(this.dividendDay, this.dividendDay.AddDays(7), Dividend, "USD")] : []);

            public Task<IReadOnlyList<StockSplit>> GetSplitsAsync(string ticker, DateOnly from, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<StockSplit>>(ticker == "MSFT" ? [new StockSplit(this.splitDay, 2m)] : []);

            public Task<IReadOnlyList<DailyRate>> GetRatesAsync(string code, DateOnly from, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<DailyRate>>(this.days.Prepend(MarketCalendar.PreviousMarketDay(this.days[0])).Select(day => new DailyRate(day, 0.0504m)).ToArray());
        }
    }
}
