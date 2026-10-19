// <copyright file="BearerTokenTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias client;
extern alias server;

namespace AtlasLedger.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Security.Cryptography;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using client::AtlasLedger.Client.Api;
    using client::AtlasLedger.Client.Scripts;
    using client::AtlasLedger.Model;
    using Microsoft.AspNetCore.Authentication.JwtBearer;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.IdentityModel.JsonWebTokens;
    using Microsoft.IdentityModel.Protocols;
    using Microsoft.IdentityModel.Protocols.OpenIdConnect;
    using Microsoft.IdentityModel.Tokens;
    using server::AtlasLedger.Server;
    using Xunit;

    /// <summary>
    /// The server in production mode, signing callers in with bearer tokens from an identity provider: here a test one,
    /// whose signing key the test holds, so it can issue the tokens Entra External ID would.  Every request goes through
    /// the same token validation and access rules as in Azure; only the key and the issuer are the test's.
    /// </summary>
    public sealed class BearerTokenTests : IClassFixture<BearerTokenTests.Server>
    {
        private const string Authority = "https://00000000-0000-0000-0000-00000000c1a0.ciamlogin.com/00000000-0000-0000-0000-00000000c1a0/v2.0";

        private const string ApiClientId = "00000000-0000-0000-0000-0000000000a1";

        private const string ToolClientId = "00000000-0000-0000-0000-0000000000b2";

        private const string LoaderClientId = "00000000-0000-0000-0000-0000000000b3";

        private const string OperatorClientId = "00000000-0000-0000-0000-0000000000b4";

        private static readonly string[] LoaderPermissions = { "Ledger.Write", "Users.Write", "Dossier.Write" };

        private static readonly string[] OperatorPermissions = { "Ledger.Read", "Users.Read", "Services.Manage" };

        private const string DevelopmentCallerToolClientId = "22222222-2222-2222-2222-222222222222";

        private const string MemberObjectId = "00000000-0000-0000-0000-0000000000c3";

        private readonly Server server;

        /// <summary>
        /// Initializes a new instance of the <see cref="BearerTokenTests"/> class.
        /// </summary>
        /// <param name="server">The server, shared by the class's tests.</param>
        public BearerTokenTests(Server server)
        {
            this.server = server;
        }

        /// <summary>
        /// A caller without a token gets 401, not data, and not a hint about which tenants exist.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task ACallerWithoutATokenIsUnauthorized()
        {
            var status = await StatusOf(() => this.Client(null).ListAsync<AccountDto>(this.server.FirmA, "Account", TestContext.Current.CancellationToken));
            Assert.Equal(HttpStatusCode.Unauthorized, status);
        }

        /// <summary>
        /// The allowed tool, acting as itself with an application's token, loads a script into any tenant and switches the
        /// services: the walkthrough's path in Azure.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task TheAllowedToolLoadsAnyTenantAndSwitchesServices()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var tool = this.Client(this.server.ToolToken(ToolClientId));
            var tenant = Guid.NewGuid();
            await tool.ApplyAsync(tenant, Firm(), cancellationToken);
            Assert.Equal(2, (await tool.ListAsync<AccountDto>(tenant, "Account", cancellationToken)).Count);
            Assert.NotEmpty(await tool.GetServicesAsync(cancellationToken));
        }

        /// <summary>
        /// An application that is not on the tools list is checked like a person, and a person with no identity belongs to
        /// no tenant: 404, as for a tenant that does not exist.  It cannot reach the services switch either.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task AnApplicationNotOnTheToolsListGetsNothing()
        {
            var cancellationToken = TestContext.Current.CancellationToken;

            // Any other application, including one claiming the development tool's client ID, which is allowed only
            // alongside development sign-in.
            foreach (var clientId in new[] { "00000000-0000-0000-0000-0000000000ff", DevelopmentCallerToolClientId })
            {
                var stranger = this.Client(this.server.ToolToken(clientId));
                Assert.Equal(HttpStatusCode.NotFound, await StatusOf(() => stranger.ListAsync<AccountDto>(this.server.FirmA, "Account", cancellationToken)));
                Assert.Equal(HttpStatusCode.NotFound, await StatusOf(() => stranger.ApplyAsync(Guid.NewGuid(), Firm(), cancellationToken)));
                Assert.Equal(HttpStatusCode.NotFound, await StatusOf(() => stranger.GetServicesAsync(cancellationToken)));
            }
        }

        /// <summary>
        /// A person, signed in through an application with delegated scopes, works in the tenant whose users include their
        /// identity (issuer and oid), gets 404 for a tenant they do not belong to, and 403 for a table their scopes do not
        /// cover inside their own tenant.  They cannot reach the services switch.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task APersonWorksOnlyInTheirOwnTenantWithinTheirScopes()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var member = this.Client(this.server.PersonToken(MemberObjectId, "Ledger.Read Ledger.Write"));
            Assert.Equal(2, (await member.ListAsync<AccountDto>(this.server.FirmA, "Account", cancellationToken)).Count);
            Assert.Equal(HttpStatusCode.NotFound, await StatusOf(() => member.ListAsync<AccountDto>(this.server.FirmB, "Account", cancellationToken)));
            Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(() => member.ListRowsAsync(this.server.FirmA, "Dossier", cancellationToken)));
            Assert.Equal(HttpStatusCode.NotFound, await StatusOf(() => member.GetServicesAsync(cancellationToken)));

            var outsider = this.Client(this.server.PersonToken("00000000-0000-0000-0000-0000000000dd", "Ledger.Read Ledger.Write"));
            Assert.Equal(HttpStatusCode.NotFound, await StatusOf(() => outsider.ListAsync<AccountDto>(this.server.FirmA, "Account", cancellationToken)));
        }

        /// <summary>
        /// The walkthrough, with real tokens: the script loader and the operator console each with only their own
        /// permissions, and a person signed in for access-test's checks.  Every step gets what it expects.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task TheWalkthroughPassesWithEachProgramsOwnToken()
        {
            const string Person = "00000000-0000-0000-0000-0000000000c9";
            var (passed, report) = await WalkthroughSteps.RunAsync(
                this.Client(this.server.ToolToken(LoaderClientId, roles: LoaderPermissions)),
                this.Client(this.server.ToolToken(OperatorClientId, roles: OperatorPermissions)),
                this.Client(this.server.PersonToken(Person, "Ledger.Read Ledger.Write")),
                this.Client(null),
                (Authority, Person),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            Assert.True(passed, report);
        }

        /// <summary>
        /// Each program can do its job and nothing else: the script loader writes but cannot read or switch services; the
        /// operator console reads and switches services but cannot write.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task EachProgramHasOnlyItsOwnPermissions()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var loader = this.Client(this.server.ToolToken(LoaderClientId, roles: LoaderPermissions));
            var operatorConsole = this.Client(this.server.ToolToken(OperatorClientId, roles: OperatorPermissions));
            var tenant = Guid.NewGuid();

            await loader.ApplyAsync(tenant, Firm(), cancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(() => loader.ListAsync<AccountDto>(tenant, "Account", cancellationToken)));
            Assert.Equal(HttpStatusCode.NotFound, await StatusOf(() => loader.GetServicesAsync(cancellationToken)));

            Assert.Equal(2, (await operatorConsole.ListAsync<AccountDto>(tenant, "Account", cancellationToken)).Count);
            Assert.NotEmpty(await operatorConsole.GetServicesAsync(cancellationToken));
            Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(() => operatorConsole.ApplyAsync(Guid.NewGuid(), Firm(), cancellationToken)));
        }

        /// <summary>
        /// A token is refused (401) unless it comes from the configured issuer, for this API, unexpired, and signed with the
        /// issuer's key: whatever its claims say.
        /// </summary>
        /// <param name="flaw">What is wrong with the token.</param>
        /// <returns>A task that completes when the test is done.</returns>
        [Theory]
        [InlineData("issuer")]
        [InlineData("audience")]
        [InlineData("expired")]
        [InlineData("key")]
        public async Task AFlawedTokenIsUnauthorized(string flaw)
        {
            var token = flaw switch
            {
                "issuer" => this.server.ToolToken(ToolClientId, issuer: "https://elsewhere.ciamlogin.com/elsewhere/v2.0"),
                "audience" => this.server.ToolToken(ToolClientId, audience: "00000000-0000-0000-0000-0000000000ee"),
                "expired" => this.server.ToolToken(ToolClientId, expires: DateTime.UtcNow.AddMinutes(-10)),
                _ => this.server.ToolToken(ToolClientId, key: new RsaSecurityKey(RSA.Create(2048))),
            };

            var status = await StatusOf(() => this.Client(token).ListAsync<AccountDto>(this.server.FirmA, "Account", TestContext.Current.CancellationToken));
            Assert.Equal(HttpStatusCode.Unauthorized, status);
        }

        /// <summary>
        /// Outside Development, a server with no identity provider configured does not start: it never runs open.  Nor does
        /// one whose provider names no audience.
        /// </summary>
        [Fact]
        public void WithoutAnIdentityProviderTheServerDoesNotStartOutsideDevelopment()
        {
            Assert.Throws<InvalidOperationException>(() => AtlasLedgerServer.Build(new[] { "--environment", "Production", "--urls", "http://127.0.0.1:0" }));
            Assert.Throws<InvalidOperationException>(() => AtlasLedgerServer.Build(new[] { "--environment", "Production", "--urls", "http://127.0.0.1:0", $"--IdentityProvider:Authority={Authority}" }));
        }

        private static LedgerScript Firm() => new LedgerScript()
            .Insert("Node", new NodeDto { NodeId = Guid.Parse("5a000000-0000-0000-0000-000000000001"), DisplayName = "Smith Trust", FullName = "Smith Family Trust", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account })
            .Insert("Node", new NodeDto { NodeId = Guid.Parse("5a000000-0000-0000-0000-000000000002"), DisplayName = "Lee 401k", FullName = "Lee Family 401(k) Plan", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account })
            .Insert("Account", new AccountDto { AccountId = Guid.Parse("5a000000-0000-0000-0000-000000000001"), AccountType = AccountType.ManagedAccount })
            .Insert("Account", new AccountDto { AccountId = Guid.Parse("5a000000-0000-0000-0000-000000000002"), AccountType = AccountType.GroupAccount })
            .Insert("User", new UserDto { UserId = Guid.Parse("5a000000-0000-0000-0000-0000000000a1"), IdentityIssuer = Authority, IdentitySubject = MemberObjectId });

        private static async Task<HttpStatusCode> StatusOf(Func<Task> call)
        {
            try
            {
                await call();
                return HttpStatusCode.OK;
            }
            catch (LedgerApiException exception)
            {
                return exception.StatusCode;
            }
        }

        private LedgerApiClient Client(string? token)
        {
            var httpClient = new HttpClient { BaseAddress = this.server.BaseAddress };
            if (token != null)
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return new LedgerApiClient(httpClient, caller: null);
        }

        /// <summary>
        /// One server in production mode, trusting a test identity provider, with firm A (whose users include the member)
        /// and firm B (which has none) loaded by the tool.
        /// </summary>
        public sealed class Server : IAsyncLifetime
        {
            private readonly RsaSecurityKey signingKey = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test-key" };

            private WebApplication? application;

            /// <summary>
            /// Gets the server's address.
            /// </summary>
            public Uri BaseAddress { get; private set; } = new Uri("http://127.0.0.1/");

            /// <summary>
            /// Gets firm A, whose users include the member.
            /// </summary>
            public Guid FirmA { get; } = Guid.NewGuid();

            /// <summary>
            /// Gets firm B, which has no users.
            /// </summary>
            public Guid FirmB { get; } = Guid.NewGuid();

            /// <summary>
            /// Issues an application's token, as Entra does for client credentials: idtyp app, the client in azp, and
            /// application permissions in roles.
            /// </summary>
            /// <param name="clientId">The application's client ID.</param>
            /// <param name="issuer">The issuer, if not the configured one.</param>
            /// <param name="audience">The audience, if not the API.</param>
            /// <param name="expires">When it expires, if not in an hour.</param>
            /// <param name="key">The signing key, if not the issuer's.</param>
            /// <returns>The token.</returns>
            /// <param name="roles">The application permissions, if not every ledger permission and the services switch.</param>
            public string ToolToken(string clientId, string? issuer = null, string? audience = null, DateTime? expires = null, SecurityKey? key = null, string[]? roles = null) =>
                this.Issue(
                    new Dictionary<string, object> { ["idtyp"] = "app", ["azp"] = clientId, ["oid"] = Guid.NewGuid().ToString(), ["roles"] = roles ?? new[] { "Ledger.Read", "Ledger.Write", "Users.Read", "Users.Write", "Services.Manage" } },
                    issuer,
                    audience,
                    expires,
                    key);

            /// <summary>
            /// Issues a person's token, as Entra does for a signed-in user: their oid and the delegated scopes in scp.
            /// </summary>
            /// <param name="objectId">The person's object ID.</param>
            /// <param name="scopes">The delegated scopes, separated by spaces.</param>
            /// <returns>The token.</returns>
            public string PersonToken(string objectId, string scopes) =>
                this.Issue(new Dictionary<string, object> { ["idtyp"] = "user", ["azp"] = "00000000-0000-0000-0000-0000000000c1", ["oid"] = objectId, ["scp"] = scopes }, null, null, null, null);

            /// <inheritdoc/>
            public async ValueTask InitializeAsync()
            {
                var arguments = new[]
                {
                    "--environment", "Production",
                    "--urls", "http://127.0.0.1:0",
                    "--Logging:LogLevel:Default=None",
                    $"--IdentityProvider:Authority={Authority}",
                    $"--IdentityProvider:Audiences:0={ApiClientId}",
                    $"--Tools:ClientIds:0={ToolClientId}",
                    $"--Tools:ClientIds:1={LoaderClientId}",
                    $"--Tools:ClientIds:2={OperatorClientId}",
                    "--MarketFeed:TickInterval=00:00:00.300",
                    "--MarketFeed:BadPrintInterval=00:00:01",
                };

                // The issuer's metadata and keys, as the server would fetch them from Entra.
                var metadata = new OpenIdConnectConfiguration { Issuer = Authority };
                metadata.SigningKeys.Add(this.signingKey);
                this.application = AtlasLedgerServer.Build(arguments, services => services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options => options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata)));
                await this.application.StartAsync().ConfigureAwait(false);
                this.BaseAddress = new Uri(this.application.Urls.First() + "/");

                using var httpClient = new HttpClient { BaseAddress = this.BaseAddress };
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", this.ToolToken(ToolClientId));
                var tool = new LedgerApiClient(httpClient, caller: null);
                await tool.ApplyAsync(this.FirmA, Firm()).ConfigureAwait(false);
                await tool.ApplyAsync(this.FirmB, new LedgerScript().Insert("Node", new NodeDto { NodeId = Guid.NewGuid(), DisplayName = "Other", FullName = "Other Firm's Account", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account })).ConfigureAwait(false);
            }

            /// <inheritdoc/>
            public async ValueTask DisposeAsync()
            {
                if (this.application != null)
                {
                    await this.application.StopAsync().ConfigureAwait(false);
                    await this.application.DisposeAsync().ConfigureAwait(false);
                }
            }

            private string Issue(Dictionary<string, object> claims, string? issuer, string? audience, DateTime? expires, SecurityKey? key)
            {
                var now = DateTime.UtcNow;
                return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                {
                    Issuer = issuer ?? Authority,
                    Audience = audience ?? ApiClientId,
                    IssuedAt = now.AddMinutes(-30),
                    NotBefore = now.AddMinutes(-30),
                    Expires = expires ?? now.AddHours(1),
                    Claims = claims,
                    SigningCredentials = new SigningCredentials(key ?? this.signingKey, SecurityAlgorithms.RsaSha256),
                });
            }
        }
    }
}
