namespace AtlasLedger.LoadTest;

using System.Security.Cryptography;
using System.Text;
using AtlasLedger.Client.Scripts;
using AtlasLedger.Client.TestData;
using AtlasLedger.Model;

/// <summary>
/// Synthetic tenants: the Joe 10 at the simulated broker, bought years ago and traded since (a few trades a week, a
/// deposit a month), each trade a sale and a purchase at the day's synthetic close.
/// </summary>
public static class Tenants
{
    public static Guid IdOf(int index) => KeyFor($"tenant/{index}");

    public static (LedgerScript Reference, LedgerScript Weights, IReadOnlyList<TransactionMessage> History) Build(int index, int years, double tradesPerWeek)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var first = today.AddYears(-years);
        while (!Market.IsMarketDay(first))
        {
            first = first.AddDays(1);
        }

        var state = new Joe10State(Joe10State.CloseOf(first), 0.5m, Market.Tickers.Select(ticker => new Joe10Holding(ticker, 0.1m, Market.Close(ticker, first))).ToArray());
        var reference = Joe10Account.Reference(state);
        var weights = Joe10Account.Weights(state);
        var history = new List<TransactionMessage>(Joe10Account.History(state, 1_000_000m));
        var shares = Joe10Account.Purchases(state, 1_000_000m).ToDictionary(purchase => purchase.Ticker, purchase => purchase.Shares);
        var cash = 1_000_000m - Joe10Account.Purchases(state, 1_000_000m).Sum(purchase => purchase.Shares * purchase.Price);
        var random = new Random(index);
        var number = 0;
        foreach (var day in Market.MarketDays(first.AddDays(1), today.AddDays(-1)))
        {
            var close = Joe10State.CloseOf(day);
            if (day.Day == 1 || (day.Day <= 3 && day.DayOfWeek == DayOfWeek.Monday))
            {
                cash += 1_000m;
                history.Add(Transaction($"deposit/{day}", new CashMovementEvent { AccountId = Joe10Account.AccountId, TradeDate = day, Method = CashMethod.Ach, Items = [new ActivityItem(Joe10Account.DollarId, 1_000m, 1_000m)] }, close.AddHours(-6), ref number));
            }

            if (random.NextDouble() >= tradesPerWeek / 5.0)
            {
                continue;
            }

            var sell = Market.Tickers[random.Next(Market.Tickers.Length)];
            var buy = Market.Tickers[random.Next(Market.Tickers.Length)];
            if (sell == buy || shares[sell] < 2m)
            {
                continue;
            }

            var sold = Math.Max(1m, Math.Floor(shares[sell] * 0.05m));
            var sellPrice = Market.Close(sell, day);
            var proceeds = sold * sellPrice;
            shares[sell] -= sold;
            cash += proceeds;
            history.Add(Transaction($"sell/{day}/{number}", new TradeEvent
            {
                AccountId = Joe10Account.AccountId,
                TradeDate = day,
                SettlementDate = day.AddDays(1),
                Items = [new ActivityItem(Joe10Account.AssetIdOf(sell), -sold, -proceeds, sellPrice, PositionEffect.Closing), new ActivityItem(Joe10Account.DollarId, proceeds, proceeds)],
            }, close, ref number));

            var buyPrice = Market.Close(buy, day);
            var bought = Math.Floor(proceeds / buyPrice);
            if (bought < 1m)
            {
                continue;
            }

            var cost = bought * buyPrice;
            shares[buy] += bought;
            cash -= cost;
            history.Add(Transaction($"buy/{day}/{number}", new TradeEvent
            {
                AccountId = Joe10Account.AccountId,
                TradeDate = day,
                SettlementDate = day.AddDays(1),
                Items = [new ActivityItem(Joe10Account.AssetIdOf(buy), bought, cost, buyPrice, PositionEffect.Opening), new ActivityItem(Joe10Account.DollarId, -cost, -cost)],
            }, close, ref number));
        }

        return (reference, weights, history);
    }

    private static TransactionMessage Transaction(string name, LedgerEvent value, DateTimeOffset effective, ref int number)
    {
        number++;
        return new TransactionMessage
        {
            TransactionId = KeyFor($"transaction/{name}"),
            Events = [EventMessages.From(value, KeyFor($"event/{name}"), EventSource.Simulator, $"load-{name}", effective, effective, number)],
        };
    }

    private static Guid KeyFor(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"tangent-atlas/load-test/{name}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
