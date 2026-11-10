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

        private const string ExternalAccountNumber = "JOE10-TEST";

        /// <summary>
        /// Builds the reference script: rows that say how things are.
        /// </summary>
        /// <param name="state">The Joe 10 at a close.</param>
        /// <returns>The script.</returns>
        public static LedgerScript Reference(Joe10State state)
        {
            ArgumentNullException.ThrowIfNull(state);
            var script = new LedgerScript()
                .Insert("Node", new NodeDto { NodeId = AccountId, DisplayName = "Joe 10", FullName = "Joe 10 test account", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account })
                .Insert("Account", new AccountDto { AccountId = AccountId, AccountType = AccountType.ManagedAccount, Currency = CurrencyCode.UnitedStatesDollar })
                .Insert("Symbol", new SymbolDto { Scheme = SymbolScheme.Custodian, Code = ExternalAccountNumber, NodeId = AccountId })
                .Insert("AccountConnection", new AccountConnectionDto { AccountConnectionId = KeyFor("connection"), AccountId = AccountId, Source = EventSource.Simulator, ExternalAccountId = ExternalAccountNumber })
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
        /// Builds the history script: transactions that say what happened, each inserted with its lines.  The deposit is
        /// effective at the open; the purchases at the close, in order of weight.
        /// </summary>
        /// <param name="state">The Joe 10 at a close.</param>
        /// <param name="deposit">The dollars paid in.</param>
        /// <returns>The script.</returns>
        public static LedgerScript History(Joe10State state, decimal deposit)
        {
            ArgumentNullException.ThrowIfNull(state);
            var entered = state.Close;
            var depositId = KeyFor("deposit");
            var script = new LedgerScript()
                .Insert("Transaction", new TransactionDto
                {
                    TransactionId = depositId,
                    Kind = TransactionKind.Deposit,
                    Source = EventSource.Simulator,
                    SourceId = "joe10-deposit",
                    EffectiveTime = state.Close.AddHours(-6.5),
                    EnteredTime = entered,
                    Reason = "The Joe 10 test account's opening deposit.",
                })
                .Insert("TransactionLine", new TransactionLineDto { TransactionId = depositId, LineNumber = 1, AccountId = AccountId, AssetId = DollarId, Effect = LineEffect.Change, Quantity = deposit, Amount = deposit });
            var sequence = 0L;
            foreach (var purchase in Purchases(state, deposit))
            {
                sequence++;
                var transactionId = KeyFor($"buy/{purchase.Ticker}");
                var cost = purchase.Shares * purchase.Price;
                script
                    .Insert("Transaction", new TransactionDto
                    {
                        TransactionId = transactionId,
                        Kind = TransactionKind.Buy,
                        Source = EventSource.Simulator,
                        SourceId = $"joe10-buy-{purchase.Ticker}",
                        SourceSequence = sequence,
                        EffectiveTime = state.Close,
                        EnteredTime = entered,
                    })
                    .Insert("TransactionLine", new TransactionLineDto { TransactionId = transactionId, LineNumber = 1, AccountId = AccountId, AssetId = AssetIdOf(purchase.Ticker), Effect = LineEffect.Change, Quantity = purchase.Shares, Amount = cost })
                    .Insert("TransactionLine", new TransactionLineDto { TransactionId = transactionId, LineNumber = 2, AccountId = AccountId, AssetId = DollarId, Effect = LineEffect.Change, Quantity = -cost, Amount = -cost });
            }

            return script;
        }

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
