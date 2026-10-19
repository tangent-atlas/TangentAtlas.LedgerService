// <copyright file="ClientConnection.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Api
{
    using System;
    using System.Net.Http;

    /// <summary>
    /// Which program is connecting: each has its own registration and signs in its own way.
    /// </summary>
    public enum ClientProgram
    {
        /// <summary>
        /// The script loader: a tool, write-only, signing in as itself with the certificate.
        /// </summary>
        ScriptLoader,

        /// <summary>
        /// The operator console: a tool that reads, exports and switches services, signing in as itself with the
        /// certificate.
        /// </summary>
        Operator,

        /// <summary>
        /// access-test: a person, signing in through the browser.
        /// </summary>
        AccessTest,
    }

    /// <summary>
    /// A program's connection to a server: the address, and an API client signed in the program's way.  For the settings
    /// file's server, real sign-in; for any other server, such as one on this computer, development sign-in.
    /// </summary>
    public sealed class ClientConnection : IDisposable
    {
        private const string DefaultServer = "http://127.0.0.1:5180";

        private ClientConnection(string server, HttpClient httpClient, LedgerApiClient api, PersonSignIn? person)
        {
            this.Server = server;
            this.HttpClient = httpClient;
            this.Api = api;
            this.Person = person;
        }

        /// <summary>
        /// Gets the server's address.
        /// </summary>
        public string Server { get; }

        /// <summary>
        /// Gets the HTTP client, addressed to the server.
        /// </summary>
        public HttpClient HttpClient { get; }

        /// <summary>
        /// Gets the API, signed in as the program.
        /// </summary>
        public LedgerApiClient Api { get; }

        /// <summary>
        /// Gets the API with no sign-in at all.
        /// </summary>
        public LedgerApiClient Anonymous => new LedgerApiClient(this.HttpClient, null);

        /// <summary>
        /// Gets the person's sign-in, for access-test with real sign-in; otherwise null.
        /// </summary>
        public PersonSignIn? Person { get; }

        /// <summary>
        /// Gets a value indicating whether the connection uses real sign-in.
        /// </summary>
        public bool UsesRealSignIn => this.Api.Caller is null;

        /// <summary>
        /// Opens a program's connection.
        /// </summary>
        /// <param name="program">The program.</param>
        /// <param name="serverOption">The server named on the command line, if any.</param>
        /// <returns>The connection.</returns>
        public static ClientConnection Open(ClientProgram program, string? serverOption)
        {
            var settings = ClientSettings.Read(ClientSettings.DefaultPath);
            var server = serverOption
                ?? (Environment.GetEnvironmentVariable("ATLAS_LEDGER_SERVER") is { Length: > 0 } fromEnvironment ? fromEnvironment : null)
                ?? settings.Server
                ?? DefaultServer;
            var httpClient = new HttpClient { BaseAddress = new Uri(server.EndsWith('/') ? server : server + "/") };
            if (!SameServer(server, settings.Server) || settings.Authority is null || settings.ApiUri is null)
            {
                var caller = program == ClientProgram.AccessTest ? DevelopmentCallers.Advisor : DevelopmentCallers.Tool;
                return new ClientConnection(server, httpClient, new LedgerApiClient(httpClient, caller), null);
            }

            switch (program)
            {
                case ClientProgram.AccessTest:
                    var person = new PersonSignIn(
                        settings.Authority,
                        Required(settings.AccessTestClientId, "accessTestClientId"),
                        new[] { $"{settings.ApiUri}/Ledger.Read", $"{settings.ApiUri}/Ledger.Write" },
                        "access-test.cache");
                    return new ClientConnection(server, httpClient, new LedgerApiClient(httpClient, null, person.GetAccessTokenAsync), person);
                default:
                    var clientId = program == ClientProgram.ScriptLoader
                        ? Required(settings.ScriptLoaderClientId, "scriptLoaderClientId")
                        : Required(settings.OperatorClientId, "operatorClientId");
                    var tool = new ToolSignIn(settings.Authority, clientId, settings.ApiUri, Required(settings.CertificateThumbprint, "certificateThumbprint"));
                    return new ClientConnection(server, httpClient, new LedgerApiClient(httpClient, null, tool.GetAccessTokenAsync), null);
            }
        }

        /// <inheritdoc/>
        public void Dispose() => this.HttpClient.Dispose();

        /// <summary>
        /// Gets a value indicating whether two server addresses name the same server (scheme, host, and port).
        /// </summary>
        private static bool SameServer(string server, string? configured) =>
            configured is not null
            && Uri.TryCreate(server, UriKind.Absolute, out var a)
            && Uri.TryCreate(configured, UriKind.Absolute, out var b)
            && Uri.Compare(a, b, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

        private static string Required(string? value, string name) =>
            value is { Length: > 0 } ? value : throw new InvalidOperationException($"{ClientSettings.DefaultPath} has no {name}. Run TangentAtlas.Platform's Deploy-EntraApplications.ps1 on this computer.");
    }
}
