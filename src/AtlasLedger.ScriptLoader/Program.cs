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
            Usage: script-loader <tenant> <script.json> [--server <url>]

            Reads the script and sends it to the server, into the tenant, as one batch (several, if it is larger than
            the server's batch limit). Every operation commits, or none does.

              <tenant>          The tenant's ID (a GUID). A new ID makes a new tenant.
              <script.json>     The script: { "operations": [ { "action": "Insert", "table": "Node", "row": { ... } } ] }
              --server <url>    The server (default: ATLAS_LEDGER_SERVER, else the settings file's, else
                                http://127.0.0.1:5180).

            Sign-in: to the settings file's server (%APPDATA%\TangentAtlas\ledger-client.json, written by
            TangentAtlas.Platform's Deploy-EntraApplications.ps1) as the Script Loader, with the tools' certificate;
            to any other server, development sign-in.

            Exit codes: 0 loaded; 1 the server refused the script or could not be reached; 2 the command line or the
            script file is wrong.
            """;

        private static async Task<int> Main(string[] args)
        {
            string? server = null;
            var positional = new List<string>();
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--server" when index + 1 < args.Length: server = args[++index]; break;
                    case "--help" or "-h" or "-?": Console.WriteLine(Usage); return 0;
                    default: positional.Add(args[index]); break;
                }
            }

            if (positional.Count != 2 || !Guid.TryParse(positional[0], out var tenantId))
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
                await Commands.LoadAsync(connection.Api, tenantId, positional[1], Console.Out, cancellation.Token).ConfigureAwait(false);
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
