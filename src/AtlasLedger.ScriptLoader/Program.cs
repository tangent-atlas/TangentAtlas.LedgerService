// <copyright file="Program.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.ScriptLoader
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Client;
    using AtlasLedger.Client.Api;

    /// <summary>
    /// script-loader: loads a JSON script into a tenant.
    /// </summary>
    internal static class Program
    {
        private const string Usage = """
            Usage: script-loader <tenant> <script.json> [--pause <seconds>] [--server <url>]
                   script-loader clear <tenant> [--holdings-only] [--remove-users] [--pause <seconds>] [--server <url>]

            Reads the script and sends it to the server, into the tenant. A table script goes in as one batch
            (several, if it is larger than the server's batch limit): every operation commits, or none does. A
            transaction script goes to the event log: each transaction commits or is refused by itself, and loading
            it again changes nothing.

              <tenant>          The tenant's ID (a GUID). A new ID makes a new tenant.
              <script.json>     A table script: { "operations": [ { "action": "Insert", "table": "Node", "row": { ... } } ] }
                                or a transaction script: [ { "transactionId": ..., "events": [ { "eventId": ...,
                                "eventType": "Trade", ..., "payload": { ... } } ] } ], or the same as JSON Lines (.jsonl:
                                one transaction per line).
            clear: empties the tenant, in two stages a page open on it shows as they happen. First the holdings
            leave each account (its securities sold at their current prices, all of its cash withdrawn: events, since
            the log is never changed), then each account is deleted with its node, custodian number and links. Institutions,
            models and assets stay. Clearing a clear tenant changes nothing. The rules service must be on. It reads
            as the operator and writes as the script loader (each tool has only its own half).
              --holdings-only   Keep the accounts; only take the holdings out.
              --remove-users    Also remove the tenant's users, so no person sees it any more.
              --pause <seconds> Wait between accounts (for a load, between transactions), to watch it in the
                                website (default 0).

              --server <url>    The server (default: ATLAS_LEDGER_SERVER, else the settings file's, else
                                http://127.0.0.1:5180).

            Sign-in: to the settings file's server (%APPDATA%\TangentAtlas\ledger-client.json, written by
            TangentAtlas.Platform's Deploy-EntraApplications.ps1) as the Script Loader, with the tools' certificate;
            to any other server, development sign-in.

            Exit codes: 0 loaded (or cleared); 1 the server refused the script or could not be reached; 2 the command line or the
            script file is wrong.
            """;

        private static async Task<int> Main(string[] args)
        {
            string? server = null;
            var holdingsOnly = false;
            var removeUsers = false;
            var pause = TimeSpan.Zero;
            var positional = new List<string>();
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--server" when index + 1 < args.Length: server = args[++index]; break;
                    case "--holdings-only": holdingsOnly = true; break;
                    case "--remove-users": removeUsers = true; break;
                    case "--pause" when index + 1 < args.Length && double.TryParse(args[index + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 && seconds <= 600:
                        pause = TimeSpan.FromSeconds(seconds);
                        index++;
                        break;
                    case "--help" or "-h" or "-?": Console.WriteLine(Usage); return 0;
                    default: positional.Add(args[index]); break;
                }
            }

            var isClear = positional.Count == 2 && positional[0] == "clear";
            if (positional.Count != 2 || !Guid.TryParse(positional[isClear ? 1 : 0], out var tenantId) || (!isClear && (holdingsOnly || removeUsers)))
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            try
            {
                using var connection = ClientConnection.Open(ClientProgram.ScriptLoader, server);
                if (isClear)
                {
                    // The script loader only writes; it reads as the operator, which only reads.
                    using var reading = ClientConnection.Open(ClientProgram.Operator, server);
                    await TenantClearing.ClearAsync(reading.Api, connection.Api, tenantId, holdingsOnly, removeUsers, pause, Console.Out, cancellation.Token).ConfigureAwait(false);
                }
                else
                {
                    await Commands.LoadAsync(connection.Api, tenantId, positional[1], Console.Out, cancellation.Token, pause).ConfigureAwait(false);
                }

                return 0;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"Cannot read the script {positional[1]}: {exception.Message}");
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
        }
    }
}
