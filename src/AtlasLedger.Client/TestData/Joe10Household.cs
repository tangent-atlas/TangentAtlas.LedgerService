// <copyright file="Joe10Household.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.TestData
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using AtlasLedger.Client.Scripts;
    using AtlasLedger.Model;

    /// <summary>
    /// SPY at a close, held under the Joe 10's thermostat: its closing price and the share of the account in it.
    /// </summary>
    /// <param name="Price">SPY's closing price.</param>
    /// <param name="StockShare">The share in SPY after the thermostat: the 15% target over SPY's volatility, at most all of
    /// it.</param>
    /// <param name="Volatility">SPY's volatility over the last 21 trading days (annualized), which set the share; null for the
    /// generic SPY.</param>
    public sealed record SpyState(decimal Price, decimal StockShare, decimal? Volatility)
    {
        /// <summary>The Joe 10's volatility target (pre-registration §20): the benchmark is held to the same.</summary>
        public const decimal Target = 0.15m;

        /// <summary>The trading days of returns in the volatility estimate, as the Joe 10's.</summary>
        public const int Window = 21;

        /// <summary>
        /// A generic SPY, for scripts that need no real data: $100 a share (where the simulated feed starts a ticker), half
        /// the account in it.
        /// </summary>
        /// <returns>The state.</returns>
        public static SpyState Generic() => new(100m, 0.5m, null);

        /// <summary>
        /// Reads SPY's daily closes, as EODHD's end-of-day API returns them (a JSON array of rows with <c>date</c>,
        /// <c>close</c> and <c>adjusted_close</c>), and sets the share in SPY at a close by the Joe 10's thermostat: the
        /// annualized sample standard deviation of the last 21 daily returns (from adjusted closes, so dividends don't count
        /// as drops), and the 15% target over it, at most one.
        /// </summary>
        /// <param name="path">The closes.</param>
        /// <param name="close">The trading date of the close (the Joe 10's).</param>
        /// <returns>The state.</returns>
        public static SpyState Read(string path, DateOnly close)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var rows = document.RootElement.EnumerateArray()
                .Select(row => (
                    Date: DateOnly.ParseExact(row.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Close: row.GetProperty("close").GetDecimal(),
                    Adjusted: row.GetProperty("adjusted_close").GetDecimal()))
                .Where(row => row.Date <= close)
                .OrderBy(row => row.Date)
                .ToArray();
            if (rows.Length == 0 || rows[^1].Date != close)
            {
                throw new InvalidOperationException($"The closes have no row for {close:yyyy-MM-dd}.");
            }

            if (rows.Length < Window + 1)
            {
                throw new InvalidOperationException($"The closes have {rows.Length} rows up to {close:yyyy-MM-dd}; the thermostat needs {Window + 1}.");
            }

            var last = rows[^(Window + 1)..];
            var returns = last.Zip(last.Skip(1), (before, after) => (double)(after.Adjusted / before.Adjusted) - 1).ToArray();
            var volatility = (decimal)VolatilityOf(returns);
            return new SpyState(rows[^1].Close, Math.Min(1m, Target / volatility), volatility);
        }

        /// <summary>
        /// The annualized sample standard deviation of daily returns (252 trading days a year), as the forward test's.
        /// </summary>
        /// <param name="returns">The daily returns.</param>
        /// <returns>The volatility.</returns>
        public static double VolatilityOf(IReadOnlyList<double> returns)
        {
            ArgumentNullException.ThrowIfNull(returns);
            var mean = returns.Average();
            return Math.Sqrt(returns.Sum(value => (value - mean) * (value - mean)) / (returns.Count - 1)) * Math.Sqrt(252);
        }
    }

    /// <summary>
    /// The rest of the Joe 10's test household, beside <see cref="Joe10Account"/>: in the Joe 10 portfolio, a checking
    /// account and a credit card (entered by hand, with placeholder balances; read-only: they count toward the portfolio
    /// but no model trades them), and beside it the SPY Benchmark portfolio, a Play Money account of SPY held under the same thermostat
    /// as the Joe 10 (15% volatility), with the same deposit.  Load its reference after the Joe 10's (it uses the household,
    /// the Joe 10 portfolio, the paper broker and the dollar), its weights with the Joe 10's, and its history after the
    /// Joe 10's.
    /// </summary>
    public static class Joe10Household
    {
        /// <summary>The bank: entered by hand until accounts are linked.</summary>
        public static readonly Guid BankId = Joe10Account.KeyFor("institution/bank-of-america");

        /// <summary>The checking account, in the Joe 10 portfolio.</summary>
        public static readonly Guid CheckingId = Joe10Account.KeyFor("account/checking");

        /// <summary>The credit card, in the Joe 10 portfolio.</summary>
        public static readonly Guid CardId = Joe10Account.KeyFor("account/mastercard");

        /// <summary>The SPY Benchmark portfolio, beside the Joe 10 in the household.</summary>
        public static readonly Guid BenchmarkPortfolioId = Joe10Account.KeyFor("portfolio/spy-benchmark");

        /// <summary>The SPY Benchmark's model: SPY, with the thermostat's cash taken out of its weight.</summary>
        public static readonly Guid BenchmarkModelId = Joe10Account.KeyFor("model/spy-benchmark");

        /// <summary>The SPY Benchmark's Play Money account.</summary>
        public static readonly Guid BenchmarkAccountId = Joe10Account.KeyFor("account/spy-benchmark");

        /// <summary>SPY.</summary>
        public static readonly Guid SpyId = Joe10Account.AssetIdOf("SPY");

        /// <summary>The checking account's balance: a placeholder.</summary>
        public const decimal CheckingBalance = 25_000m;

        /// <summary>The card's balance owed: a placeholder.</summary>
        public const decimal CardBalance = 18_500m;

        private const string CheckingNumber = "BOFA-CHK-4821";

        private const string CardNumber = "BOFA-MC-7310";

        private const string BenchmarkAccountNumber = "PAPER-100020";

        /// <summary>
        /// Builds the reference script: the bank, the checking account and the card in the Joe 10 portfolio, SPY, and the
        /// SPY Benchmark portfolio with its model and its Play Money account.
        /// </summary>
        /// <param name="state">The Joe 10 at a close (the day everything is in its place from).</param>
        /// <returns>The script.</returns>
        public static LedgerScript Reference(Joe10State state)
        {
            ArgumentNullException.ThrowIfNull(state);
            var since = DateOnly.FromDateTime(state.Close.UtcDateTime);
            return new LedgerScript()
                .Insert("Institution", new InstitutionDto { InstitutionId = BankId, Name = "Bank of America", Source = EventSource.Operator })
                .Insert("Node", new NodeDto { NodeId = CheckingId, DisplayName = "Checking", FullName = "Bank of America checking", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account })
                .Insert("Account", new AccountDto { AccountId = CheckingId, AccountType = AccountType.Deposit, Currency = CurrencyCode.UnitedStatesDollar, InstitutionId = BankId, ExternalAccountId = CheckingNumber, IsReadOnly = true })
                .Insert("Edge", new EdgeDto { ChildNodeId = CheckingId, ParentNodeId = Joe10Account.PortfolioId, Position = 1, Since = since })
                .Insert("Node", new NodeDto { NodeId = CardId, DisplayName = "Mastercard", FullName = "Bank of America Premium Mastercard", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account })
                .Insert("Account", new AccountDto { AccountId = CardId, AccountType = AccountType.CreditCard, Currency = CurrencyCode.UnitedStatesDollar, InstitutionId = BankId, ExternalAccountId = CardNumber, IsReadOnly = true })
                .Insert("Edge", new EdgeDto { ChildNodeId = CardId, ParentNodeId = Joe10Account.PortfolioId, Position = 2, Since = since })
                .Insert("Node", new NodeDto { NodeId = SpyId, DisplayName = "SPY", FullName = "SPDR S&P 500 ETF Trust", NodeStatus = NodeStatus.Active, NodeType = NodeType.Asset })
                .Insert("Asset", new AssetDto { AssetId = SpyId, AssetType = AssetType.Fund, QuoteCurrency = CurrencyCode.UnitedStatesDollar, QuantityFactor = ValueFactor.One, PriceFactor = ValueFactor.One })
                .Insert("Fund", new FundDto { AssetId = SpyId, FundType = FundType.ExchangeTradedFund })
                .Insert("Symbol", new SymbolDto { Scheme = SymbolScheme.Ticker, Code = "SPY", NodeId = SpyId })
                .Insert("Model", new ModelDto { ModelId = BenchmarkModelId, Name = "SPY at 15%", ModelKind = ModelKind.FixedWeights, CashRule = CashRule.None, Band = 0.005m })
                .Insert("Node", new NodeDto { NodeId = BenchmarkPortfolioId, DisplayName = "SPY Benchmark", FullName = "SPY Benchmark", NodeStatus = NodeStatus.Active, NodeType = NodeType.Portfolio })
                .Insert("Portfolio", new PortfolioDto { PortfolioId = BenchmarkPortfolioId, ModelId = BenchmarkModelId })
                .Insert("Edge", new EdgeDto { ChildNodeId = BenchmarkPortfolioId, ParentNodeId = Joe10Account.HouseholdId, Position = 1, Since = since })
                .Insert("Node", new NodeDto { NodeId = BenchmarkAccountId, DisplayName = Joe10Account.PaperAccountName, FullName = "SPY Benchmark's Play Money account", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account })
                .Insert("Account", new AccountDto { AccountId = BenchmarkAccountId, AccountType = AccountType.Brokerage, Currency = CurrencyCode.UnitedStatesDollar, InstitutionId = Joe10Account.InstitutionId, ExternalAccountId = BenchmarkAccountNumber })
                .Insert("Edge", new EdgeDto { ChildNodeId = BenchmarkAccountId, ParentNodeId = BenchmarkPortfolioId, Position = 0, Since = since })
                .Insert("Brokerage", new BrokerageDto { AccountId = BenchmarkAccountId, BrokerageType = BrokerageType.Taxable })
                .Insert("Symbol", new SymbolDto { Scheme = SymbolScheme.Custodian, Code = BenchmarkAccountNumber, NodeId = BenchmarkAccountId });
        }

        /// <summary>
        /// Builds the weights script: SPY at the thermostat's share as the benchmark model's weight; the rest stays in cash.
        /// </summary>
        /// <param name="spy">SPY at the close.</param>
        /// <returns>The script.</returns>
        public static LedgerScript Weights(SpyState spy)
        {
            ArgumentNullException.ThrowIfNull(spy);
            return new LedgerScript()
                .Insert("Weight", new WeightDto { ModelId = BenchmarkModelId, AssetId = SpyId, Fraction = Math.Round(spy.StockShare, 6, MidpointRounding.ToZero) });
        }

        /// <summary>
        /// The shares of SPY the deposit buys: the thermostat's share, in whole shares, rounded down.
        /// </summary>
        /// <param name="spy">SPY at the close.</param>
        /// <param name="deposit">The dollars paid in.</param>
        /// <returns>The shares.</returns>
        public static decimal Shares(SpyState spy, decimal deposit)
        {
            ArgumentNullException.ThrowIfNull(spy);
            return Math.Floor(deposit * spy.StockShare / spy.Price);
        }

        /// <summary>
        /// Builds the history: the checking account's and the card's balances (entered by hand, at the open), then the SPY
        /// Benchmark's deposit and its purchase of SPY at the close.
        /// </summary>
        /// <param name="state">The Joe 10 at a close.</param>
        /// <param name="spy">SPY at that close.</param>
        /// <param name="deposit">The dollars paid into the benchmark: the Joe 10's deposit, so the two start level.</param>
        /// <returns>The transactions, in order.</returns>
        public static IReadOnlyList<TransactionMessage> History(Joe10State state, SpyState spy, decimal deposit)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(spy);
            var day = DateOnly.FromDateTime(state.Close.Date);
            var open = state.Close.AddHours(-6.5);
            var transactions = new List<TransactionMessage>
            {
                Cash("balance/checking", CheckingId, day, CashMethod.Other, CheckingBalance, open, state.Close),
                Cash("balance/mastercard", CardId, day, CashMethod.Other, -CardBalance, open, state.Close),
                Cash("deposit/spy-benchmark", BenchmarkAccountId, day, CashMethod.Ach, deposit, open, state.Close),
            };
            var shares = Shares(spy, deposit);
            if (shares > 0m)
            {
                var cost = shares * spy.Price;
                var trade = new TradeEvent
                {
                    AccountId = BenchmarkAccountId,
                    TradeDate = day,
                    SettlementDate = day.AddDays(1),
                    Items =
                    [
                        new ActivityItem(SpyId, shares, cost, spy.Price, PositionEffect.Opening),
                        new ActivityItem(Joe10Account.DollarId, -cost, -cost),
                    ],
                };
                transactions.Add(Transaction("buy/spy-benchmark/SPY", EventMessages.From(trade, Joe10Account.KeyFor("event/buy/spy-benchmark/SPY"), EventSource.Simulator, "spy-benchmark-buy-SPY", state.Close, state.Close, 1)));
            }

            return transactions;
        }

        private static TransactionMessage Cash(string name, Guid accountId, DateOnly day, CashMethod method, decimal amount, DateTimeOffset effective, DateTimeOffset entered)
        {
            var cash = new CashMovementEvent
            {
                AccountId = accountId,
                TradeDate = day,
                Method = method,
                Items = [new ActivityItem(Joe10Account.DollarId, amount, amount)],
            };
            var source = method == CashMethod.Other ? EventSource.Operator : EventSource.Simulator;
            return Transaction(name, EventMessages.From(cash, Joe10Account.KeyFor($"event/{name}"), source, $"household-{name.Replace('/', '-')}", effective, entered));
        }

        private static TransactionMessage Transaction(string name, params EventMessage[] events) =>
            new() { TransactionId = Joe10Account.KeyFor($"transaction/{name}"), Events = events };
    }
}
