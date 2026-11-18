// ledger-load: vendor | load | measure.  See the project file.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AtlasLedger.Client.Api;
using AtlasLedger.LoadTest;
using AtlasLedger.Model;

var command = args.FirstOrDefault() ?? "help";
string Option(string name, string fallback)
{
    var at = Array.IndexOf(args, "--" + name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback;
}

switch (command)
{
    case "vendor":
        await RunVendorAsync(int.Parse(Option("port", "5199"), CultureInfo.InvariantCulture));
        return 0;
    case "load":
        return await LoadAsync();
    case "measure":
        return await MeasureAsync();
    default:
        Console.Error.WriteLine("ledger-load vendor [--port 5199] | load --server URL --from I --to J [--years 3] [--trades-per-week 3] | measure --server URL --tenants N [--clients 1] [--seconds 60] [--pid P] [--label L] [--out FILE]");
        return 2;
}

// The market data vendor, synthetic: EODHD's closes (no dividends or splits) and the Treasury's bill rates.
static async Task RunVendorAsync(int port)
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
    builder.Logging.ClearProviders();
    var app = builder.Build();
    app.MapGet("/api/eod/{symbol}", (string symbol, string? from, string? to) =>
    {
        var ticker = symbol.EndsWith(".US", StringComparison.Ordinal) ? symbol[..^3].Replace('-', '.') : symbol;
        var first = DateOnly.Parse(from ?? "2015-01-01", CultureInfo.InvariantCulture);
        var last = DateOnly.Parse(to ?? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return Results.Json(Market.MarketDays(first, last).Select(day => new { date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), close = Market.Close(ticker, day) }));
    });
    app.MapGet("/api/div/{symbol}", () => Results.Json(Array.Empty<object>()));
    app.MapGet("/api/splits/{symbol}", () => Results.Json(Array.Empty<object>()));
    app.MapGet("/resource-center/data-chart-center/interest-rates/daily-treasury-rates.csv/{year}/all", (int year) => Results.Text(Market.TreasuryCsv(year), "text/csv"));
    Console.WriteLine($"Vendor on http://127.0.0.1:{port}");
    await app.RunAsync();
}

async Task<int> LoadAsync()
{
    using var http = Http(Option("server", "http://127.0.0.1:5100"));
    var api = new LedgerApiClient(http, DevelopmentCallers.Tool);
    var (from, to) = (int.Parse(Option("from", "0"), CultureInfo.InvariantCulture), int.Parse(Option("to", "1"), CultureInfo.InvariantCulture));
    var years = int.Parse(Option("years", "3"), CultureInfo.InvariantCulture);
    var tradesPerWeek = double.Parse(Option("trades-per-week", "3"), CultureInfo.InvariantCulture);
    var parallel = int.Parse(Option("parallel", "4"), CultureInfo.InvariantCulture);
    var timings = new ConcurrentBag<(int Index, int Transactions, double Seconds)>();
    var watch = Stopwatch.StartNew();
    await Parallel.ForEachAsync(Enumerable.Range(from, to - from), new ParallelOptions { MaxDegreeOfParallelism = parallel }, async (index, token) =>
    {
        var (reference, weights, history) = Tenants.Build(index, years, tradesPerWeek);
        var tenantId = Tenants.IdOf(index);
        var each = Stopwatch.StartNew();
        await api.ApplyAsync(tenantId, reference, token);
        await api.ApplyAsync(tenantId, weights, token);
        foreach (var chunk in history.Chunk(200))
        {
            await api.CommitTransactionsAsync(tenantId, chunk, token);
        }

        timings.Add((index, history.Count, each.Elapsed.TotalSeconds));
    });
    var ordered = timings.OrderBy(timing => timing.Index).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        tenants = ordered.Length,
        transactionsPerTenant = ordered.Average(timing => timing.Transactions),
        secondsPerTenant = ordered.Average(timing => timing.Seconds),
        totalSeconds = watch.Elapsed.TotalSeconds,
    }));
    return 0;
}

async Task<int> MeasureAsync()
{
    var server = Option("server", "http://127.0.0.1:5100");
    var tenants = int.Parse(Option("tenants", "1"), CultureInfo.InvariantCulture);
    var clientsPerTenant = int.Parse(Option("clients", "1"), CultureInfo.InvariantCulture);
    var seconds = int.Parse(Option("seconds", "60"), CultureInfo.InvariantCulture);
    var pid = int.Parse(Option("pid", "0"), CultureInfo.InvariantCulture);
    var label = Option("label", $"{tenants} tenants");
    var output = Option("out", string.Empty);
    string[] tables = ["Position", "EffectivePrice", "CurrencyRate", "AccountFlow", "AccountDay", "AccountStatistic", "Weight", "Symbol", "Institution"];

    using var http = Http(server);
    var api = new LedgerApiClient(http, DevelopmentCallers.Tool);
    using var stop = new CancellationTokenSource();
    var latencies = new ConcurrentBag<double>();
    var snapshotSeconds = new ConcurrentBag<double>();
    var syncs = 0L;
    var measuring = false;
    var runs = new List<Task>();
    var accountDays = new ConcurrentBag<int>();
    var probes = new List<Func<int>>();
    var started = Stopwatch.StartNew();
    for (var index = 0; index < tenants; index++)
    {
        for (var client = 0; client < clientsPerTenant; client++)
        {
            var ledger = new Ledger();
            var sync = api.Sync(ledger, Tenants.IdOf(index), tables);
            probes.Add(() => sync.Partition.AccountDays.Count());
            var seen = new Dictionary<Guid, DateTimeOffset>();
            var first = Stopwatch.StartNew();
            var snapshotTaken = false;
            sync.Synced += (_, synced) =>
            {
                var now = DateTimeOffset.UtcNow;
                if (!snapshotTaken)
                {
                    snapshotTaken = true;
                    snapshotSeconds.Add(first.Elapsed.TotalSeconds);
                }

                Interlocked.Increment(ref syncs);
                foreach (var price in sync.Partition.EffectivePrices)
                {
                    if (price.Source != PriceSource.Market)
                    {
                        continue;
                    }

                    if (seen.TryGetValue(price.AssetId, out var before) && price.AsOf > before && Volatile.Read(ref measuring) && !synced.IsReset)
                    {
                        latencies.Add((now - price.AsOf).TotalMilliseconds);
                    }

                    seen[price.AssetId] = price.AsOf;
                }
            };
            runs.Add(Task.Run(async () =>
            {
                try
                {
                    await sync.RunAsync(stop.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"Sync for tenant {sync.TenantId} stopped: {exception.Message}");
                }
            }));
        }
    }

    // Every client has its snapshot before measuring starts.
    var expected = tenants * clientsPerTenant;
    while (snapshotSeconds.Count < expected && started.Elapsed < TimeSpan.FromMinutes(10))
    {
        await Task.Delay(200);
    }

    var allSnapshots = started.Elapsed.TotalSeconds;
    var (cpuBefore, wallBefore) = (CpuSeconds(pid), Stopwatch.GetTimestamp());
    var syncsBefore = Interlocked.Read(ref syncs);
    Volatile.Write(ref measuring, true);
    var rss = new List<double>();
    var cpuSamples = new List<double>();
    for (var second = 0; second < seconds; second++)
    {
        var (cpu0, wall0) = (CpuSeconds(pid), Stopwatch.GetTimestamp());
        await Task.Delay(1000);
        cpuSamples.Add((CpuSeconds(pid) - cpu0) / Stopwatch.GetElapsedTime(wall0).TotalSeconds * 100);
        rss.Add(RssMegabytes(pid));
    }

    Volatile.Write(ref measuring, false);
    foreach (var probe in probes)
    {
        accountDays.Add(probe());
    }

    var elapsed = Stopwatch.GetElapsedTime(wallBefore).TotalSeconds;
    var cpuPercent = (CpuSeconds(pid) - cpuBefore) / elapsed * 100;
    var syncRate = (Interlocked.Read(ref syncs) - syncsBefore) / elapsed;
    await stop.CancelAsync();
    await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(30)).ContinueWith(_ => { });
    var sorted = latencies.OrderBy(value => value).ToArray();
    double Percentile(double p) => sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
    var result = new
    {
        label,
        tenants,
        clients = expected,
        seconds,
        serverCpuPercent = Math.Round(cpuPercent, 1),
        serverCpuPercentMax = cpuSamples.Count == 0 ? 0 : Math.Round(cpuSamples.Max(), 1),
        serverRssMegabytes = rss.Count == 0 ? 0 : Math.Round(rss.Average(), 0),
        serverRssMegabytesMax = rss.Count == 0 ? 0 : Math.Round(rss.Max(), 0),
        priceMoves = sorted.Length,
        priceMovesPerSecond = Math.Round(sorted.Length / elapsed, 1),
        latencyP50Milliseconds = Math.Round(Percentile(0.50), 0),
        latencyP95Milliseconds = Math.Round(Percentile(0.95), 0),
        latencyP99Milliseconds = Math.Round(Percentile(0.99), 0),
        latencyMaxMilliseconds = sorted.Length == 0 ? double.NaN : Math.Round(sorted[^1], 0),
        syncsPerSecond = Math.Round(syncRate, 1),
        snapshotSecondsMax = snapshotSeconds.IsEmpty ? 0 : Math.Round(snapshotSeconds.Max(), 2),
        allSnapshotsSeconds = Math.Round(allSnapshots, 2),
        accountDaysPerClient = accountDays.IsEmpty ? 0 : Math.Round(accountDays.Average(), 0),
        clientProcessRssMegabytes = Math.Round(Environment.WorkingSet / 1048576.0, 0),
        clientProcessManagedMegabytes = Math.Round(GC.GetTotalMemory(forceFullCollection: true) / 1048576.0, 0),
    };
    var line = JsonSerializer.Serialize(result);
    Console.WriteLine(line);
    if (output.Length > 0)
    {
        await File.AppendAllTextAsync(output, line + Environment.NewLine);
    }

    return 0;
}

static HttpClient Http(string server) =>
    new(new SocketsHttpHandler { MaxConnectionsPerServer = 10_000, PooledConnectionLifetime = TimeSpan.FromMinutes(10) }) { BaseAddress = new Uri(server.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };

static double CpuSeconds(int pid)
{
    if (pid <= 0)
    {
        return 0;
    }

    var fields = File.ReadAllText($"/proc/{pid}/stat").Split(')')[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var ticks = long.Parse(fields[11], CultureInfo.InvariantCulture) + long.Parse(fields[12], CultureInfo.InvariantCulture);
    return ticks / 100.0;
}

static double RssMegabytes(int pid)
{
    if (pid <= 0)
    {
        return 0;
    }

    var line = File.ReadLines($"/proc/{pid}/status").First(each => each.StartsWith("VmRSS:", StringComparison.Ordinal));
    return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) / 1024.0;
}
