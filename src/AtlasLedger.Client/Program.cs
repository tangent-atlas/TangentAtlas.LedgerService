// <copyright file="Program.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Client.Api;

    /// <summary>
    /// The Atlas Ledger client: commands that work with a running server through its API.
    /// </summary>
    internal static class Program
    {
        private const string DefaultServer = "http://127.0.0.1:5180";

        private const string Usage = """
            Usage: AtlasLedger.Client <command> [options]

            Commands:
              walkthrough                   Exercise the server end to end as the tool and an advisor (the default).
              tenants                       List the tenants the caller may open.
              load <tenant> <script.json>   Load a script into a tenant as one batch.
              export <tenant> [file.json]   Export a tenant as a script, to a file or the console.
              alerts <tenant> [--watch]     Show a tenant's compliance alerts, once or as they change.
              mirror <tenant>               Copy a tenant locally and show its nodes, symbols, and alerts.
              services                      Show the server's optional services (market feed, compliance) and which are on.
              services on [name...]         Turn services on (all, if none named) for a while; they turn themselves off.
              services off [name...]        Turn services off (all, if none named).

            Options:
              --server <url>                The server (default: ATLAS_LEDGER_SERVER, else http://127.0.0.1:5180).
              --as <tool|advisor>           Who to sign in as on a development server (default: tool).
              --market-wait <seconds>       How long the walkthrough watches for a bad print (default: 30; 0 skips).
              --for <hours>                 How long 'services on' lasts: hours (4, 0.5) or h:mm (1:30); default 4 hours.
            """;

        /// <summary>
        /// Runs a command.
        /// </summary>
        /// <param name="args">The command and its options.</param>
        /// <returns>Zero on success.</returns>
        private static async Task<int> Main(string[] args)
        {
            var positional = new List<string>();
            var server = Environment.GetEnvironmentVariable("ATLAS_LEDGER_SERVER") ?? DefaultServer;
            var caller = DevelopmentCallers.Tool;
            var watch = false;
            var marketWait = TimeSpan.FromSeconds(30);
            string? durationText = null;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--server" when index + 1 < args.Length: server = args[++index]; break;
                    case "--as" when index + 1 < args.Length: caller = args[++index]; break;
                    case "--market-wait" when index + 1 < args.Length: marketWait = TimeSpan.FromSeconds(double.Parse(args[++index], CultureInfo.InvariantCulture)); break;
                    case "--for" when index + 1 < args.Length: durationText = args[++index]; break;
                    case "--watch": watch = true; break;
                    case "--help" or "-h" or "-?": Console.WriteLine(Usage); return 0;
                    default: positional.Add(args[index]); break;
                }
            }

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            using var httpClient = new HttpClient { BaseAddress = new Uri(server.EndsWith('/') ? server : server + "/") };
            var api = new LedgerApiClient(httpClient, caller);
            var output = Console.Out;
            var command = positional.Count == 0 ? "walkthrough" : positional[0];
            try
            {
                switch (command)
                {
                    case "walkthrough":
                        return await new Walkthrough(httpClient, output, marketWait).RunAsync(cancellation.Token).ConfigureAwait(false) == 0 ? 0 : 1;
                    case "tenants":
                        await Commands.TenantsAsync(api, output, cancellation.Token).ConfigureAwait(false);
                        return 0;
                    case "load" when positional.Count == 3:
                        await Commands.LoadAsync(api, Guid.Parse(positional[1]), positional[2], output, cancellation.Token).ConfigureAwait(false);
                        return 0;
                    case "export" when positional.Count is 2 or 3:
                        await Commands.ExportToAsync(api, Guid.Parse(positional[1]), positional.Count == 3 ? positional[2] : null, output, cancellation.Token).ConfigureAwait(false);
                        return 0;
                    case "alerts" when positional.Count == 2:
                        await Commands.AlertsAsync(api, Guid.Parse(positional[1]), watch, output, cancellation.Token).ConfigureAwait(false);
                        return 0;
                    case "mirror" when positional.Count == 2:
                        await Commands.MirrorAsync(api, Guid.Parse(positional[1]), output, cancellation.Token).ConfigureAwait(false);
                        return 0;
                    case "services" when positional.Count == 1 || positional[1] is "status" or "on" or "off":
                        var action = positional.Count == 1 ? "status" : positional[1];
                        TimeSpan? duration = null;
                        if (durationText is not null)
                        {
                            if (!Commands.TryParseDuration(durationText, out var parsed))
                            {
                                Console.Error.WriteLine($"'{durationText}' is not a duration: give hours (4, 0.5) or hours:minutes (1:30).");
                                return 2;
                            }

                            duration = parsed;
                        }

                        await Commands.ServicesAsync(api, action, positional.Skip(2).ToList(), duration, output, cancellation.Token).ConfigureAwait(false);
                        return 0;
                    default:
                        Console.Error.WriteLine(Usage);
                        return 2;
                }
            }
            catch (FormatException exception)
            {
                Console.Error.WriteLine($"{exception.Message} Tenants are GUIDs, for example {Guid.Empty}.");
                return 2;
            }
            catch (LedgerApiException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
            catch (HttpRequestException exception)
            {
                Console.Error.WriteLine($"Cannot reach the ledger server at {server}: {exception.Message}");
                return 1;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return 0;
            }
        }
    }
}
