// <copyright file="Program.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Operator
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Client;
    using AtlasLedger.Client.Api;
    using AtlasLedger.Model;

    /// <summary>
    /// ledger-operator: the operator's console.
    /// </summary>
    internal static class Program
    {
        private const string Usage = """
            Usage: ledger-operator <command> [options]

            Commands:
              tenants                         List the tenants.
              export <tenant> [file.json]     Export a tenant as a script (what script-loader loads), to a file or the
                                              console.
              alerts <tenant>                 Show a tenant's compliance alerts.
              alerts <tenant> --watch         ...and keep showing them as they change (Ctrl+C stops).
              alerts <tenant> --wait-for <rule> [--timeout <seconds>]
                                              Wait until an alert from the rule (or a rule starting with that name) is
                                              raised; exit 1 if it is not within the timeout (default 10).
              alerts <tenant> --wait-cleared <rule> [--timeout <seconds>]
                                              Wait until there is no such alert.
              mirror <tenant>                 Copy a tenant locally and show its accounts, symbols, and alerts.
              events <tenant> [--after <seq>] [--last <n>] [--order <id>] [--follow] [--json]
                                              Show the tenant's event log in commit order, transaction by
                                              transaction, each event's payload under it: all (or after a
                                              sequence), the latest n transactions, or one order's (its replace
                                              chain).  --follow keeps showing new transactions (Ctrl+C stops);
                                              --json writes JSON Lines, one transaction per line: the same
                                              format the events endpoint takes, so the output can be loaded.
              holdings <tenant>               Show what the tenant's events add up to: each account's holdings,
                                              valued at their prices, and its cash.  The rules derive them, so
                                              the rules engine must be on.
              orders <tenant>                 Show what each account's model proposes, and each order's state: its
                                              allocations, broker orders and fills.
              trade <tenant>                  Turn the proposals into orders now, as the scheduled run would.
              jobs <tenant>                   Show the tenant's jobs (running or not, and what each last did) and
                                              its schedules (off, on or scheduled, and when each next starts and
                                              stops).
              jobs run <tenant> <job>         Run a task now, whatever its schedules say (unless it is off); the
                                              run is recorded in the tenant's JobRun log.
              jobs set <tenant> <job> off|on|scheduled
                                              Set the status of every schedule of the job: off (it doesn't run on
                                              its own), on (a service runs until turned off) or scheduled.
              weights <tenant> <script.json>  Replace the weights of each model the script names with the script's
                                              Weight rows: changes, removes and adds rows to match, in one batch.
              services                        Show the server's optional services (market feed, rules engine).
              services on [name...] [--for <hours>]
                                              Turn services on (all, if none named) for a while (default 4 hours;
                                              hours like 4 or 0.5, or h:mm); they turn themselves off.
              services off [name...]          Turn services off (all, if none named).

            Options:
              --server <url>                  The server (default: ATLAS_LEDGER_SERVER, else the settings file's, else
                                              http://127.0.0.1:5180).

            Sign-in: to the settings file's server (%APPDATA%\TangentAtlas\ledger-client.json) as the Operator, with
            the tools' certificate; to any other server, development sign-in.

            Exit codes: 0 done; 1 the server refused, could not be reached, or a wait timed out; 2 the command line is
            wrong.
            """;

        private static async Task<int> Main(string[] args)
        {
            string? server = null;
            string? durationText = null;
            string? waitFor = null;
            string? waitCleared = null;
            var timeout = TimeSpan.FromSeconds(10);
            var watch = false;
            var follow = false;
            var json = false;
            var after = 0L;
            int? last = null;
            Guid? order = null;
            var positional = new List<string>();
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--server" when index + 1 < args.Length: server = args[++index]; break;
                    case "--for" when index + 1 < args.Length: durationText = args[++index]; break;
                    case "--wait-for" when index + 1 < args.Length: waitFor = args[++index]; break;
                    case "--wait-cleared" when index + 1 < args.Length: waitCleared = args[++index]; break;
                    case "--timeout" when index + 1 < args.Length: timeout = TimeSpan.FromSeconds(double.Parse(args[++index], CultureInfo.InvariantCulture)); break;
                    case "--watch": watch = true; break;
                    case "--follow": follow = true; break;
                    case "--json": json = true; break;
                    case "--after" when index + 1 < args.Length: after = long.Parse(args[++index], CultureInfo.InvariantCulture); break;
                    case "--last" when index + 1 < args.Length: last = int.Parse(args[++index], CultureInfo.InvariantCulture); break;
                    case "--order" when index + 1 < args.Length: order = Guid.Parse(args[++index]); break;
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

            try
            {
                using var connection = ClientConnection.Open(ClientProgram.Operator, server);
                var api = connection.Api;
                var output = Console.Out;
                var token = cancellation.Token;
                switch (positional.FirstOrDefault())
                {
                    case "tenants" when positional.Count == 1:
                        await Commands.TenantsAsync(api, output, token).ConfigureAwait(false);
                        return 0;
                    case "export" when positional.Count is 2 or 3:
                        await Commands.ExportToAsync(api, Guid.Parse(positional[1]), positional.Count == 3 ? positional[2] : null, output, token).ConfigureAwait(false);
                        return 0;
                    case "alerts" when positional.Count == 2 && (waitFor ?? waitCleared) is { } rule:
                        return await Commands.WaitForAlertAsync(api, Guid.Parse(positional[1]), rule, waitFor is not null, timeout, output, token).ConfigureAwait(false) ? 0 : 1;
                    case "alerts" when positional.Count == 2:
                        await Commands.AlertsAsync(api, Guid.Parse(positional[1]), watch, output, token).ConfigureAwait(false);
                        return 0;
                    case "trade" when positional.Count == 2:
                        await Commands.TradeAsync(api, Guid.Parse(positional[1]), output, token).ConfigureAwait(false);
                        return 0;
                    case "events" when positional.Count == 2:
                        await Commands.EventsAsync(
                            api,
                            Guid.Parse(positional[1]),
                            after,
                            last,
                            order,
                            follow,
                            json,
                            output,
                            token).ConfigureAwait(false);
                        return 0;
                    case "jobs" when positional.Count == 2:
                        await Commands.JobsAsync(api, Guid.Parse(positional[1]), output, token).ConfigureAwait(false);
                        return 0;
                    case "jobs" when positional.Count == 4 && positional[1] == "run":
                        await Commands.RunJobAsync(api, Guid.Parse(positional[2]), positional[3], output, token).ConfigureAwait(false);
                        return 0;
                    case "jobs" when positional.Count == 5 && positional[1] == "set" && Enum.TryParse<JobStatus>(positional[4], ignoreCase: true, out var jobStatus) && Enum.IsDefined(jobStatus):
                        await Commands.SetJobStatusAsync(api, Guid.Parse(positional[2]), positional[3], jobStatus, output, token).ConfigureAwait(false);
                        return 0;
                    case "orders" when positional.Count == 2:
                        await Commands.OrdersAsync(api, Guid.Parse(positional[1]), output, token).ConfigureAwait(false);
                        return 0;
                    case "weights" when positional.Count == 3:
                        await Commands.ReplaceWeightsAsync(api, Guid.Parse(positional[1]), positional[2], output, token).ConfigureAwait(false);
                        return 0;
                    case "holdings" when positional.Count == 2:
                        await Commands.HoldingsAsync(api, Guid.Parse(positional[1]), output, token).ConfigureAwait(false);
                        return 0;
                    case "mirror" when positional.Count == 2:
                        await Commands.MirrorAsync(api, Guid.Parse(positional[1]), output, token).ConfigureAwait(false);
                        return 0;
                    case "services" when positional.Count == 1 || positional[1] is "status" or "on" or "off":
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

                        await Commands.ServicesAsync(api, positional.Count == 1 ? "status" : positional[1], positional.Skip(2).ToList(), duration, output, token).ConfigureAwait(false);
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
                Console.Error.WriteLine($"Cannot reach the ledger server: {exception.Message}");
                return 1;
            }
            catch (InvalidOperationException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return 0;
            }
        }
    }
}
