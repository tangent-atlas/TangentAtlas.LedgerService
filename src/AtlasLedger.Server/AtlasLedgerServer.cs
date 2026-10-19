// <copyright file="AtlasLedgerServer.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server
{
    using System;
    using System.Linq;
    using System.Reflection;
    using System.Text.Json.Serialization;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using AtlasLedger.Server.Compliance;
    using AtlasLedger.Server.Market;
    using AtlasLedger.Server.Persistence;
    using AtlasLedger.Server.Security;
    using AtlasLedger.Server.Switches;
    using Bellerophon.Rules;
    using Bellerophon.Runtime;
    using Bellerophon.Rules.NRules;
    using Microsoft.AspNetCore.Authentication;
    using Microsoft.AspNetCore.Authentication.JwtBearer;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.DependencyInjection.Extensions;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Builds the Atlas Ledger server: the generated API over the in-memory ledger, write-behind persistence, the compliance
    /// engine, and the market data feed.
    /// </summary>
    /// <remarks>
    /// Kept apart from <c>Main</c> so tests can host the same server in their own process, with their own settings.
    /// </remarks>
    public static class AtlasLedgerServer
    {
        /// <summary>
        /// The environment variable that, when set, overrides the commit the server reports (a container image built by the
        /// Dockerfile sets it).
        /// </summary>
        public const string CommitVariable = "LEDGER_COMMIT";

        /// <summary>
        /// Gets the commit the running server was built from: <c>LEDGER_COMMIT</c> if set, otherwise the commit the .NET SDK
        /// stamps into the assembly's informational version (<c>1.0.0+&lt;commit&gt;</c>) when it builds from a Git
        /// checkout, otherwise "development".
        /// </summary>
        public static string BuildCommit
        {
            get
            {
                if (Environment.GetEnvironmentVariable(CommitVariable) is { Length: > 0 } fromEnvironment)
                {
                    return fromEnvironment;
                }

                var informational = typeof(AtlasLedgerServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                var plus = informational?.IndexOf('+', StringComparison.Ordinal) ?? -1;
                return plus >= 0 && plus < informational!.Length - 1 ? informational[(plus + 1)..] : "development";
            }
        }

        /// <summary>
        /// Builds the server, ready to run.
        /// </summary>
        /// <param name="args">Command-line arguments, which override the settings files: for example <c>--urls http://127.0.0.1:0</c>.</param>
        /// <param name="configureServices">Changes to the services after the defaults, for tests: for example, a database shared by two servers in turn.</param>
        /// <returns>The application.</returns>
        public static WebApplication Build(string[] args, Action<IServiceCollection>? configureServices = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory,

                // The server's controllers are found in the application's assembly, which is this one even when a test hosts it.
                ApplicationName = typeof(AtlasLedgerServer).Assembly.GetName().Name,
            });
            var configuration = builder.Configuration;

            // The ledger: every write answers only once the flusher has made it durable.
            builder.Services.AddSingleton(new Ledger { UsesWriteBehind = true });
            builder.Services.AddLedger();
            builder.Services.Configure<BatchOptions>(configuration.GetSection(BatchOptions.SectionName));
            builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            builder.Services.AddHealthChecks();

            // Persistence: SQL Server when a connection string is configured, otherwise a database in memory. Recovery is the
            // first hosted service, so every tenant is loaded before the flusher, the compliance engine, or the API starts.
            builder.Services.Configure<PersistenceOptions>(configuration.GetSection(PersistenceOptions.SectionName));
            if (configuration.GetConnectionString(PersistenceOptions.ConnectionName) is { Length: > 0 } connectionString)
            {
                builder.Services.AddSingleton<ILedgerDatabase>(new SqlServerLedgerDatabase(connectionString));
            }
            else
            {
                builder.Services.AddSingleton<ILedgerDatabase, InMemoryLedgerDatabase>();
            }

            builder.Services.AddHostedService<LedgerRecovery>();
            builder.Services.AddHostedService<LedgerFlusher>();

            // The services on top of the base system (the market feed and compliance) run only while switched on, through
            // v1/system/services.  Every switch starts off, so a restart lands in the cheap state: the server idles and the
            // database can pause.
            builder.Services.TryAddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<ServiceSwitchboard>();
            builder.Services.Configure<ServiceSwitchOptions>(configuration.GetSection(ServiceSwitchOptions.SectionName));

            // Compliance: the rules are compiled once; each tenant gets its own engine and NRules session.
            builder.Services.Configure<ComplianceOptions>(configuration.GetSection(ComplianceOptions.SectionName));
            builder.Services.AddSingleton(services =>
            {
                var compliance = services.GetRequiredService<IOptions<ComplianceOptions>>().Value;
                return new RulesEngineHost(
                    new NRulesSessionFactory(ComplianceRules.Compile()),
                    new RulesEngineOptions { MinimumInterval = compliance.MinimumInterval },
                    compliance.MaximumConcurrentCycles);
            });
            builder.Services.AddHostedService<ComplianceService>();

            // Market data: simulated until a real feed is connected.  Its switch decides whether it runs.
            builder.Services.Configure<MarketFeedOptions>(configuration.GetSection(MarketFeedOptions.SectionName));
            builder.Services.AddHostedService<SimulatedMarketFeed>();

            // Callers: bearer tokens from the identity provider (Microsoft Entra External ID) when one is configured;
            // otherwise development sign-in, in the Development environment only.  With neither, the server does not start:
            // it never runs open.
            builder.Services.Configure<ToolAccessOptions>(configuration.GetSection(ToolAccessOptions.SectionName));
            var identityProvider = configuration.GetSection(IdentityProviderOptions.SectionName).Get<IdentityProviderOptions>() ?? new IdentityProviderOptions();
            if (identityProvider.IsConfigured)
            {
                if (identityProvider.Audiences.Length == 0)
                {
                    throw new InvalidOperationException($"{IdentityProviderOptions.SectionName}:Authority is set but {IdentityProviderOptions.SectionName}:Audiences is empty: name the Ledger API registration's client ID.");
                }

                builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                    .AddJwtBearer(options =>
                    {
                        options.Authority = identityProvider.Authority;

                        // Keep the claims as Entra writes them (oid, scp, idtyp, azp, roles): the access policies and tenant
                        // membership read those names, exactly as development sign-in supplies them.
                        options.MapInboundClaims = false;
                        options.TokenValidationParameters.ValidIssuer = identityProvider.Authority;
                        options.TokenValidationParameters.ValidAudiences = identityProvider.Audiences;
                        options.TokenValidationParameters.NameClaimType = IdentityClaims.ObjectIdentifierClaim;
                    });
            }
            else if (builder.Environment.IsDevelopment())
            {
                builder.Services.AddAuthentication(DevelopmentAuthentication.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthentication>(DevelopmentAuthentication.SchemeName, null);

                // The development tool is allowed only alongside development sign-in, never with real tokens.
                builder.Services.PostConfigure<ToolAccessOptions>(tools => tools.ClientIds = tools.ClientIds.Append(DevelopmentAuthentication.ToolClientId).ToArray());
            }
            else
            {
                throw new InvalidOperationException(
                    $"No identity provider is configured for the '{builder.Environment.EnvironmentName}' environment: set {IdentityProviderOptions.SectionName}:Authority and {IdentityProviderOptions.SectionName}:Audiences (TangentAtlas.Platform's Deploy-EntraApplications.ps1 does), or run in Development.");
            }

            configureServices?.Invoke(builder.Services);
            var application = builder.Build();
            foreach (var clientId in application.Services.GetRequiredService<IOptions<ToolAccessOptions>>().Value.ClientIds.Where(ToolAccessOptions.IsPlaceholder))
            {
                application.Logger.LogWarning("Tools:ClientIds still holds the placeholder '{Placeholder}'; no application can use it. Replace it with the app registration's client ID.", clientId);
            }

            application.MapHealthChecks("/health");

            // The commit the running server was built from, so a deployment can confirm the new build is the one answering.
            // Anonymous like /health: a commit ID reveals nothing about any tenant.
            var commit = BuildCommit;
            application.MapGet("/version", () => commit).AllowAnonymous();
            application.UseLedgerContract();
            application.UseAuthentication();
            application.UseAuthorization();
            application.MapControllers();
            return application;
        }
    }
}
