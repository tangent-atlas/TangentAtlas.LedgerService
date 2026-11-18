namespace AtlasLedger.LoadTest;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// A synthetic market: every ticker's daily close is a deterministic random walk from $100 on January 1, 2015, so the
/// vendor's closes and the tenants' trades agree without any licensed data.
/// </summary>
public static class Market
{
    public static readonly string[] Tickers = ["NVDA", "MSFT", "AAPL", "GOOGL", "AMZN", "META", "AVGO", "TSLA", "BRK.B", "JPM"];

    private static readonly DateOnly Start = new(2015, 1, 1);

    private static readonly Dictionary<string, decimal[]> Walks = new();

    private static readonly Lock Gate = new();

    public static bool IsMarketDay(DateOnly day) => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    public static decimal Close(string ticker, DateOnly day)
    {
        var walk = WalkOf(ticker);
        var index = Math.Clamp(day.DayNumber - Start.DayNumber, 0, walk.Length - 1);
        return walk[index];
    }

    public static IEnumerable<DateOnly> MarketDays(DateOnly from, DateOnly through)
    {
        for (var day = from; day <= through; day = day.AddDays(1))
        {
            if (IsMarketDay(day))
            {
                yield return day;
            }
        }
    }

    public static string TreasuryCsv(int year)
    {
        var text = new StringBuilder("Date,\"4 WEEKS BANK DISCOUNT\",\"13 WEEKS BANK DISCOUNT\",\"13 WEEKS COUPON EQUIVALENT\"\n");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var day in MarketDays(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31)).Where(day => day <= today).Reverse())
        {
            text.Append(CultureInfo.InvariantCulture, $"{day:MM/dd/yyyy},4.10,4.15,4.25\n");
        }

        return text.ToString();
    }

    private static decimal[] WalkOf(string ticker)
    {
        lock (Gate)
        {
            if (Walks.TryGetValue(ticker, out var walk))
            {
                return walk;
            }

            var days = DateOnly.FromDateTime(DateTime.UtcNow).DayNumber - Start.DayNumber + 2;
            walk = new decimal[days];
            var seed = BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(ticker)), 0);
            var random = new Random(seed);
            var price = 100.0;
            for (var i = 0; i < days; i++)
            {
                price *= 1 + ((random.NextDouble() - 0.49) * 0.03);
                walk[i] = Math.Round((decimal)Math.Max(price, 1.0), 2);
            }

            Walks[ticker] = walk;
            return walk;
        }
    }
}
