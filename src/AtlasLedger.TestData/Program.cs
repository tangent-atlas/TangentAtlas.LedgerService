// <copyright file="Program.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.TestData
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Client.TestData;

    /// <summary>
    /// Writes test accounts as ledger scripts.
    /// </summary>
    internal static class Program
    {
        private const string Usage = """
            Usage: test-data joe10 --state <joe10.json> [--deposit <dollars>] [--out <folder>]

            Writes a test account holding the Joe 10, as two scripts for script-loader:
              joe10-reference.json   the account, its connection, and its assets (US dollars and the ten
                                     companies) with their names (rows)
              joe10-history.json     a deposit of dollars, then one purchase per company at the last close,
                                     each an exchange of dollars for shares in two balanced lines (transactions)
              joe10-trading.json     the same deposit and purchases as the trading events behind them: per
                                     company an order, its allocation, a broker order, the broker's acceptance
                                     and a fill at the last close (the largest split into two broker orders,
                                     each filled in two parts); the rules book the fills as purchases.  Load
                                     the history or the trading script, not both.

              --state <joe10.json>   The forward test's state file (tangent-atlas/forward-test, state/joe10.json):
                                     the weights, the share in stocks and the last close's prices.
              --deposit <dollars>    The opening deposit (default 1000000).
              --out <folder>         Where to write (default: artifacts/test-data, which git ignores).

            The prices come from EODHD through the forward test and are licensed, not ours to publish: keep the
            files out of public repositories.

            Then, against a local server (development sign-in):
              (the rules engine starts on; otherwise: ledger-operator services on rules)
              script-loader <tenant> joe10-reference.json
              script-loader <tenant> joe10-history.json      (or joe10-trading.json)
              ledger-operator holdings <tenant>
              ledger-operator orders <tenant>                (after the trading script)

            Exit codes: 0 written; 2 the command line or the state file is wrong.
            """;

        private static async Task<int> Main(string[] args)
        {
            string? statePath = null;
            var deposit = 1_000_000m;
            var folder = Path.Combine("artifacts", "test-data");
            var positional = new List<string>();
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--state" when index + 1 < args.Length: statePath = args[++index]; break;
                    case "--deposit" when index + 1 < args.Length: deposit = decimal.Parse(args[++index], CultureInfo.InvariantCulture); break;
                    case "--out" when index + 1 < args.Length: folder = args[++index]; break;
                    case "--help" or "-h" or "-?": Console.WriteLine(Usage); return 0;
                    default: positional.Add(args[index]); break;
                }
            }

            if (positional is not ["joe10"] || statePath is null)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            Joe10State state;
            try
            {
                state = Joe10State.Read(statePath);
            }
            catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                Console.Error.WriteLine($"Cannot read the state file {statePath}: {exception.Message}");
                return 2;
            }

            Directory.CreateDirectory(folder);
            var referencePath = Path.Combine(folder, "joe10-reference.json");
            var historyPath = Path.Combine(folder, "joe10-history.json");
            var tradingPath = Path.Combine(folder, "joe10-trading.json");
            await WriteAsync(Joe10Account.Reference(state), referencePath).ConfigureAwait(false);
            await WriteAsync(Joe10Account.History(state, deposit), historyPath).ConfigureAwait(false);
            await WriteAsync(Joe10Account.Trading(state, deposit), tradingPath).ConfigureAwait(false);

            var english = CultureInfo.GetCultureInfo("en-US");
            var purchases = Joe10Account.Purchases(state, deposit);
            var spent = purchases.Sum(purchase => purchase.Shares * purchase.Price);
            Console.WriteLine($"The Joe 10 at the close of {state.Close.ToString("u", CultureInfo.InvariantCulture)}: {state.StockShare.ToString("P1", english)} in stocks.");
            foreach (var purchase in purchases)
            {
                Console.WriteLine(string.Format(english, "  Buy {0,6:#,0} {1,-6} at {2,10:C2} = {3,14:C2}", purchase.Shares, purchase.Ticker, purchase.Price, purchase.Shares * purchase.Price));
            }

            Console.WriteLine(string.Format(english, "  Deposit {0:C2}; stocks {1:C2}; dollars left {2:C2}.", deposit, spent, deposit - spent));
            Console.WriteLine($"Wrote {referencePath}, {historyPath} and {tradingPath}. Tenant (suggested): {Joe10Account.DefaultTenantId}");
            return 0;
        }

        private static async Task WriteAsync(AtlasLedger.Client.Scripts.LedgerScript script, string path)
        {
            await using var stream = File.Create(path);
            await script.WriteAsync(stream, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
