// <copyright file="Program.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.AccessTest
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Client.Api;
    using Microsoft.Identity.Client;

    /// <summary>
    /// access-test: checks what a signed-in person may and may not do.
    /// </summary>
    internal static class Program
    {
        private const string Usage = """
            Usage: access-test <command> [--server <url>]

            Commands:
              whoami                          Sign in (a browser opens the first time) and print who you are as the
                                              server sees you, as JSON: { "issuer": ..., "subject": ... }. A tenant's
                                              user row with that issuer and subject makes you a user of the tenant.
              run --member <tenant> --other <tenant>
                                              Check what you may and may not do: your firm (--member, loaded from
                                              firm-a.json, with you as a user), a firm you do not belong to (--other),
                                              401 without signing in, 403 outside your permissions, the services hidden
                                              from you, and the data rules you meet.
              sign-out                        Forget the remembered sign-in: the accounts, their tokens, and the
                                              cache file (%LOCALAPPDATA%\TangentAtlas\access-test.cache). The next
                                              run opens the browser. The browser itself may still remember you;
                                              the next sign-in shows the account picker, so you can choose another.

            Options:
              --server <url>                  The server (default: ATLAS_LEDGER_SERVER, else the settings file's, else
                                              http://127.0.0.1:5180).

            Sign-in: to the settings file's server (%APPDATA%\TangentAtlas\ledger-client.json) as a person, through the
            browser, remembered between runs; to any other server, development sign-in as the development advisor.

            Exit codes: 0 every check passed; 1 a check failed, or sign-in or the server failed; 2 the command line is
            wrong.
            """;

        private static async Task<int> Main(string[] args)
        {
            string? server = null;
            Guid? member = null;
            Guid? other = null;
            var positional = new List<string>();
            try
            {
                for (var index = 0; index < args.Length; index++)
                {
                    switch (args[index])
                    {
                        case "--server" when index + 1 < args.Length: server = args[++index]; break;
                        case "--member" when index + 1 < args.Length: member = Guid.Parse(args[++index]); break;
                        case "--other" when index + 1 < args.Length: other = Guid.Parse(args[++index]); break;
                        case "--help" or "-h" or "-?": Console.WriteLine(Usage); return 0;
                        default: positional.Add(args[index]); break;
                    }
                }
            }
            catch (FormatException)
            {
                Console.Error.WriteLine($"Tenants are GUIDs, for example {Guid.Empty}.");
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
                using var connection = ClientConnection.Open(ClientProgram.AccessTest, server);
                switch (positional.Count == 1 ? positional[0] : null)
                {
                    case "whoami":
                        var (issuer, subject) = connection.Person is { } person
                            ? await person.GetIdentityAsync(cancellation.Token).ConfigureAwait(false)
                            : (DevelopmentCallers.Issuer, DevelopmentCallers.AdvisorSubject);
                        Console.WriteLine(JsonSerializer.Serialize(new { issuer, subject }));
                        return 0;
                    case "run" when member is { } memberFirm && other is { } otherFirm:
                        var checks = new AccessChecks(connection.Api, connection.Anonymous, Console.Out);
                        return await checks.RunAsync(memberFirm, otherFirm, cancellation.Token).ConfigureAwait(false) == 0 ? 0 : 1;
                    case "sign-out":
                        if (connection.Person is null)
                        {
                            Console.WriteLine("Nothing to forget: development sign-in remembers nothing.");
                            return 0;
                        }

                        var forgotten = await connection.Person.SignOutAsync().ConfigureAwait(false);
                        Console.WriteLine($"Signed out: forgot {forgotten} account(s) and deleted {connection.Person.CachePath}. The next run opens the browser.");
                        return 0;
                    default:
                        Console.Error.WriteLine(Usage);
                        return 2;
                }
            }
            catch (MsalException exception)
            {
                Console.Error.WriteLine($"Sign-in failed: {exception.Message}");
                return 1;
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
                return 1;
            }
        }
    }
}
