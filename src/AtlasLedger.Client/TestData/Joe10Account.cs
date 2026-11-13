// <copyright file="Joe10Account.cs" company="Tangent Atlas">
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
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using AtlasLedger.Client.Scripts;
    using AtlasLedger.Model;

    /// <summary>
    /// One holding of the Joe 10 at a close: its ticker, its weight in the stocks, and its closing price.
    /// </summary>
    /// <param name="Ticker">The ticker, as the price source spells it.</param>
    /// <param name="Weight">The holding's share of the stocks (the ten add to one).</param>
    /// <param name="Price">The closing price.</param>
    public sealed record Joe10Holding(string Ticker, decimal Weight, decimal Price);

    /// <summary>
    /// The Joe 10 at a close: what the forward test's state file (tangent-atlas/forward-test, <c>state/joe10.json</c>) says.
    /// </summary>
    /// <param name="Close">The close: 4 pm New York time on the state's last date.</param>
    /// <param name="StockShare">The share of the account in stocks after the thermostat (the rest is cash).</param>
    /// <param name="Holdings">The ten holdings.</param>
    public sealed record Joe10State(DateTimeOffset Close, decimal StockShare, IReadOnlyList<Joe10Holding> Holdings)
    {
        /// <summary>
        /// Reads the forward test's state file.  Its <c>units</c> are shares per dollar of the track's value; with the
        /// weights and the share in stocks they give each closing price: price = weight × stock share × value ÷ units.
        /// </summary>
        /// <param name="path">The state file.</param>
        /// <returns>The state.</returns>
        public static Joe10State Read(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var lastDate = DateOnly.ParseExact(root.GetProperty("last_date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var value = root.GetProperty("joe10").GetDecimal();
            var portfolio = root.GetProperty("portfolio");
            var stockShare = portfolio.GetProperty("stocks_after").GetDecimal();
            var units = portfolio.GetProperty("units");
            var holdings = portfolio.GetProperty("weights").EnumerateObject()
                .Select(weight =>
                {
                    var unitsPerDollar = units.GetProperty(weight.Name).GetDecimal();
                    var price = Math.Round(weight.Value.GetDecimal() * stockShare * value / unitsPerDollar, 2, MidpointRounding.AwayFromZero);
                    return new Joe10Holding(weight.Name, weight.Value.GetDecimal(), price);
                })
                .OrderByDescending(holding => holding.Weight)
                .ToArray();
            return new Joe10State(CloseOf(lastDate), stockShare, holdings);
        }

        /// <summary>
        /// The US market's close on a date, 4 pm in New York, as a UTC time.
        /// </summary>
        /// <param name="date">The trading date.</param>
        /// <returns>The close.</returns>
        /// <summary>
        /// A generic Joe 10, for a test account that needs no real data: ten large US companies at equal weights, half the
        /// account in stocks, each at $100 (where the simulated market feed starts a ticker it hasn't priced), at the last
        /// weekday close before a time.  No licensed prices, so the scripts it makes can be shared.
        /// </summary>
        /// <param name="now">The time (default: now).</param>
        /// <returns>The state.</returns>
        public static Joe10State Generic(DateTimeOffset? now = null)
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now ?? DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime);
            while (CloseOf(day) > (now ?? DateTimeOffset.UtcNow) || day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                day = day.AddDays(-1);
            }

            var tickers = new[] { "NVDA", "MSFT", "AAPL", "GOOGL", "AMZN", "META", "AVGO", "TSLA", "BRK.B", "JPM" };
            return new Joe10State(CloseOf(day), 0.5m, tickers.Select(ticker => new Joe10Holding(ticker, 0.1m, 100m)).ToArray());
        }

        public static DateTimeOffset CloseOf(DateOnly date)
        {
            var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var local = date.ToDateTime(new TimeOnly(16, 0));
            return new DateTimeOffset(local, newYork.GetUtcOffset(local)).ToUniversalTime();
        }
    }

    /// <summary>
    /// Builds a test account holding the Joe 10: a reference script (the account, its connection, and the assets it holds,
    /// US dollars among them, with their names) and a history script (a deposit of dollars, then one purchase per company at
    /// the close).  Both are ordinary ledger scripts, so <c>script-loader</c> loads them.
    /// </summary>
    /// <remarks>
    /// Cash is an asset like any other.  A purchase exchanges one asset for another, so each is one transaction with two
    /// lines that balance: the shares received (their cost) and the dollars given.  Every key is derived from a name, so
    /// building twice gives the same keys and loading twice changes nothing (transactions are idempotent on their source and
    /// source ID).  Shares are whole, rounded down, so the purchases never spend more than the cash; what is left stays in
    /// dollars.  Fees are zero, as at Schwab.
    /// </remarks>
    public static class Joe10Account
    {
        /// <summary>The default tenant for the Joe 10 test account.</summary>
        public static readonly Guid DefaultTenantId = KeyFor("tenant");

        /// <summary>The account.</summary>
        public static readonly Guid AccountId = KeyFor("account");

        /// <summary>The US dollar, the account's cash.</summary>
        public static readonly Guid DollarId = KeyFor("asset/USD");

        /// <summary>
        /// The Joe 10 model: the weights the account is to hold.
        /// </summary>
        public static readonly Guid ModelId = KeyFor("model");

        /// <summary>
        /// The institution the account is held at: the simulated broker.
        /// </summary>
        public static readonly Guid InstitutionId = KeyFor("institution/simulator");

        /// <summary>
        /// The schedule that generates the tenant's orders: 3:45 pm in New York on market days.
        /// </summary>
        public static readonly Guid GenerateOrdersScheduleId = KeyFor("job/GenerateOrders");

        private const string ExternalAccountNumber = "JOE10-TEST";

        /// <summary>
        /// Builds the reference script: rows that say how things are.
        /// </summary>
        /// <param name="state">The Joe 10 at a close.</param>
        /// <param name="broker">The broker the account is held at (its institution's connection): the simulator unless a test says otherwise.</param>
        /// <returns>The script.</returns>
        public static LedgerScript Reference(Joe10State state, EventSource broker = EventSource.Simulator)
        {
            ArgumentNullException.ThrowIfNull(state);
            var script = new LedgerScript()
                .Insert("Institution", new InstitutionDto { InstitutionId = InstitutionId, Name = broker == EventSource.Simulator ? "Simulated broker" : broker.ToString(), Source = broker })
                .Insert("Model", new ModelDto { ModelId = ModelId, Name = "Joe 10", Band = 0.005m })
                .Insert("Node", new NodeDto { NodeId = AccountId, DisplayName = "Joe 10", FullName = "Joe 10 test account", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account })
                .Insert("Account", new AccountDto { AccountId = AccountId, AccountRole = AccountRole.ManagedAccount, AccountType = AccountType.Brokerage, Currency = CurrencyCode.UnitedStatesDollar, InstitutionId = InstitutionId, ExternalAccountId = ExternalAccountNumber, ModelId = ModelId })
                .Insert("Brokerage", new BrokerageDto { AccountId = AccountId, BrokerageType = BrokerageType.Taxable })
                .Insert("Symbol", new SymbolDto { Scheme = SymbolScheme.Custodian, Code = ExternalAccountNumber, NodeId = AccountId })
                .Insert("Node", new NodeDto { NodeId = DollarId, DisplayName = CurrencyCode.UnitedStatesDollar, FullName = "US dollar", NodeStatus = NodeStatus.Active, NodeType = NodeType.Asset })
                .Insert("Asset", new AssetDto { AssetId = DollarId, AssetType = AssetType.Cash, QuoteCurrency = CurrencyCode.UnitedStatesDollar, QuantityFactor = ValueFactor.One, PriceFactor = ValueFactor.One })
                .Insert("Cash", new CashDto { AssetId = DollarId, CashType = CashType.Currency, CurrencyCode = CurrencyCode.UnitedStatesDollar })
                .Insert("Symbol", new SymbolDto { Scheme = SymbolScheme.Currency, Code = CurrencyCode.UnitedStatesDollar, NodeId = DollarId });
            foreach (var holding in state.Holdings)
            {
                var assetId = AssetIdOf(holding.Ticker);
                script
                    .Insert("Node", new NodeDto { NodeId = assetId, DisplayName = holding.Ticker, FullName = holding.Ticker, NodeStatus = NodeStatus.Active, NodeType = NodeType.Asset })
                    .Insert("Asset", new AssetDto { AssetId = assetId, AssetType = AssetType.Equity, QuoteCurrency = CurrencyCode.UnitedStatesDollar, QuantityFactor = ValueFactor.One, PriceFactor = ValueFactor.One })
                    .Insert("Equity", new EquityDto { AssetId = assetId, EquityType = EquityType.CommonStock })
                    .Insert("Symbol", new SymbolDto { Scheme = SymbolScheme.Ticker, Code = holding.Ticker, NodeId = assetId });
            }

            return script;
        }

        /// <summary>
        /// Builds the history: transactions that say what happened, one event each.  The deposit is effective at the open;
        /// the purchases at the close, in order of weight.
        /// </summary>
        /// <param name="state">The Joe 10 at a close.</param>
        /// <param name="deposit">The dollars paid in.</param>
        /// <returns>The transactions, in order.</returns>
        public static IReadOnlyList<TransactionMessage> History(Joe10State state, decimal deposit)
        {
            ArgumentNullException.ThrowIfNull(state);
            var entered = state.Close;
            var transactions = new List<TransactionMessage> { Deposit(state, deposit) };
            var sequence = 0L;
            var day = DateOnly.FromDateTime(state.Close.Date);
            foreach (var purchase in Purchases(state, deposit))
            {
                sequence++;
                var cost = purchase.Shares * purchase.Price;
                var trade = new TradeEvent
                {
                    AccountId = AccountId,
                    TradeDate = day,
                    SettlementDate = day.AddDays(1),
                    Items =
                    [
                        new ActivityItem(AssetIdOf(purchase.Ticker), purchase.Shares, cost, purchase.Price, PositionEffect.Opening),
                        new ActivityItem(DollarId, -cost, -cost),
                    ],
                };
                transactions.Add(Transaction($"buy/{purchase.Ticker}", EventMessages.From(trade, KeyFor($"buy/{purchase.Ticker}"), EventSource.Simulator, $"joe10-buy-{purchase.Ticker}", state.Close, entered, sequence)));
            }

            return transactions;
        }

        /// <summary>
        /// Builds the trading transactions: the same deposit and the same purchases as <see cref="History"/>, but as the
        /// trading events that lead to them, for the rules to turn into holdings.  Each purchase is an order (origin
        /// Rebalance) with one allocation, to the account, placed in one transaction with its broker orders (as the order
        /// generator writes them); then the broker's acceptance and each fill at the last close, without fees, each a
        /// transaction of its own (as the router records them).  The largest purchase is split into two broker orders, each
        /// filled in two parts, so the script also exercises splitting and partial fills.  The rules book each fill as a
        /// purchase, so the holdings come out the same as from the history.  Load one or the other, not both.
        /// </summary>
        /// <param name="state">The Joe 10 at a close.</param>
        /// <param name="deposit">The dollars paid in.</param>
        /// <returns>The transactions, in order.</returns>
        public static IReadOnlyList<TransactionMessage> Trading(Joe10State state, decimal deposit)
        {
            ArgumentNullException.ThrowIfNull(state);
            var transactions = new List<TransactionMessage> { Deposit(state, deposit) };
            var answers = new List<TransactionMessage>();
            var placed = state.Close.AddMinutes(-30);
            var purchases = Purchases(state, deposit);
            var largest = purchases.OrderByDescending(purchase => purchase.Shares * purchase.Price).First().Ticker;
            foreach (var purchase in purchases)
            {
                var orderId = KeyFor($"order/{purchase.Ticker}");
                var order = new OrderPlacedEvent
                {
                    OrderId = orderId,
                    Origin = OrderOrigin.Rebalance,
                    Legs = [new OrderLeg(1, AssetIdOf(purchase.Ticker), Instruction.Buy, [new AllocationItem(AccountId, purchase.Shares)], PositionEffect.Opening)],
                };
                var placing = new List<EventMessage> { EventMessages.From(order, orderId, EventSource.Simulator, $"joe10-order-{purchase.Ticker}", placed, correlationId: orderId) };

                // The largest purchase in two broker orders, each filled in two parts; the rest in one, filled at once.
                var isSplit = purchase.Ticker == largest && purchase.Shares >= 4m;
                var first = isSplit ? Math.Floor(purchase.Shares / 2m) : purchase.Shares;
                var brokerQuantities = isSplit ? new[] { first, purchase.Shares - first } : new[] { purchase.Shares };
                for (var part = 0; part < brokerQuantities.Length; part++)
                {
                    var quantity = brokerQuantities[part];
                    var brokerOrderId = KeyFor($"broker-order/{purchase.Ticker}/{part}");
                    var sent = new BrokerOrderSentEvent { OrderId = orderId, BrokerOrderId = brokerOrderId, AccountId = AccountId, Legs = [new BrokerLeg(1, quantity)], OrderType = OrderType.Market, TimeInForce = TimeInForce.Day };
                    var accepted = new OrderAcceptedEvent { OrderId = orderId, BrokerOrderId = brokerOrderId, ReportTime = placed.AddMinutes(1) };
                    placing.Add(EventMessages.From(sent, brokerOrderId, EventSource.Simulator, $"joe10-{purchase.Ticker}-{part}-sent", placed, correlationId: orderId, causationId: orderId));
                    answers.Add(Transaction($"report/{purchase.Ticker}/{part}/accepted", EventMessages.From(accepted, KeyFor($"report/{purchase.Ticker}/{part}/accepted"), EventSource.Simulator, $"joe10-{purchase.Ticker}-{part}-accepted", placed.AddMinutes(1), correlationId: orderId, causationId: brokerOrderId)));
                    var fillFirst = isSplit ? Math.Floor(quantity / 2m) : quantity;
                    var fills = isSplit ? new[] { fillFirst, quantity - fillFirst } : new[] { quantity };
                    for (var number = 0; number < fills.Length; number++)
                    {
                        var sourceId = $"joe10-{purchase.Ticker}-{part}-fill-{number}";
                        var fill = new ExecutionReportedEvent { OrderId = orderId, BrokerOrderId = brokerOrderId, ReportTime = state.Close, ExecutionId = sourceId, Quantity = fills[number], Price = purchase.Price };
                        answers.Add(Transaction($"report/{purchase.Ticker}/{part}/fill/{number}", EventMessages.From(fill, KeyFor($"report/{purchase.Ticker}/{part}/fill/{number}"), EventSource.Simulator, sourceId, state.Close, correlationId: orderId, causationId: brokerOrderId)));
                    }
                }

                transactions.Add(Transaction($"order/{purchase.Ticker}", placing.ToArray()));
            }

            // The broker answers after every order is placed: acceptances at a minute past, fills at the close.
            transactions.AddRange(answers.OrderBy(answer => answer.Events[0].EffectiveTime));
            return transactions;
        }

        /// <summary>
        /// Builds the opening transactions for an account the rules rebalance: the deposit only.  The first rebalance buys the
        /// Joe 10 with it (load the weights too).
        /// </summary>
        /// <param name="state">The Joe 10 at a close.</param>
        /// <param name="deposit">The dollars paid in.</param>
        /// <returns>The transactions.</returns>
        public static IReadOnlyList<TransactionMessage> Opening(Joe10State state, decimal deposit) => [Deposit(state, deposit)];

        /// <summary>
        /// Builds the jobs script: the schedule that rebalances the tenant, generating its orders at 3:45 pm in New York on
        /// market days (while the trading switch is on).  Separate from the reference, so it can be added to a tenant that
        /// already has its reference rows.
        /// </summary>
        /// <returns>The script.</returns>
        public static LedgerScript Jobs() =>
            new LedgerScript()
                .Insert("JobSchedule", new JobScheduleDto { JobScheduleId = GenerateOrdersScheduleId, Job = "GenerateOrders", CronSchedule = "0 45 15 ? * MON-FRI", TimeZone = "America/New_York", Status = JobStatus.Scheduled, Description = "Rebalance: turn the proposed trades into orders, fifteen minutes before the close." });

        /// <summary>
        /// Builds a weights script: the Joe 10's weights at a close (each company's weight times the share in stocks) as the
        /// Joe 10 model's weights; the rest stays in cash.  Load it once with script-loader; after that, each day, replace the
        /// model's weights with it (<c>ledger-operator weights &lt;tenant&gt; joe10-weights.json</c>), which changes,
        /// removes and adds rows to match.
        /// </summary>
        /// <param name="state">The Joe 10 at a close.</param>
        /// <returns>The script.</returns>
        public static LedgerScript Weights(Joe10State state)
        {
            ArgumentNullException.ThrowIfNull(state);
            var script = new LedgerScript();
            foreach (var holding in state.Holdings)
            {
                script.Insert("Weight", new WeightDto { ModelId = ModelId, AssetId = AssetIdOf(holding.Ticker), Fraction = Math.Round(holding.Weight * state.StockShare, 6, MidpointRounding.ToZero) });
            }

            return script;
        }

        private static TransactionMessage Deposit(Joe10State state, decimal deposit)
        {
            var cash = new CashMovementEvent
            {
                AccountId = AccountId,
                TradeDate = DateOnly.FromDateTime(state.Close.Date),
                Method = CashMethod.Ach,
                Items = [new ActivityItem(DollarId, deposit, deposit)],
            };
            return Transaction("deposit", EventMessages.From(cash, KeyFor("deposit"), EventSource.Simulator, "joe10-deposit", state.Close.AddHours(-6.5), state.Close));
        }

        private static TransactionMessage Transaction(string name, params EventMessage[] events) =>
            new() { TransactionId = KeyFor($"transaction/{name}"), Events = events };

        /// <summary>
        /// The purchases that put the deposit into the Joe 10: each company's share of the stocks, in whole shares, rounded
        /// down.
        /// </summary>
        /// <param name="state">The Joe 10 at a close.</param>
        /// <param name="deposit">The dollars paid in.</param>
        /// <returns>Each purchase.</returns>
        public static IReadOnlyList<(string Ticker, decimal Shares, decimal Price)> Purchases(Joe10State state, decimal deposit)
        {
            ArgumentNullException.ThrowIfNull(state);
            return state.Holdings
                .Select(holding => (holding.Ticker, Shares: Math.Floor(deposit * state.StockShare * holding.Weight / holding.Price), holding.Price))
                .Where(purchase => purchase.Shares > 0m)
                .ToArray();
        }

        /// <summary>
        /// The asset for a ticker.
        /// </summary>
        /// <param name="ticker">The ticker.</param>
        /// <returns>The asset's node ID.</returns>
        public static Guid AssetIdOf(string ticker) => KeyFor($"asset/{ticker}");

        // A name-based key (RFC 9562, version 8: SHA-256 of a fixed prefix and the name), so the same name always gives the
        // same key.
        private static Guid KeyFor(string name)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"tangent-atlas/test-data/joe10/{name}"));
            hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
            return new Guid(hash.AsSpan(0, 16), bigEndian: true);
        }
    }
}
