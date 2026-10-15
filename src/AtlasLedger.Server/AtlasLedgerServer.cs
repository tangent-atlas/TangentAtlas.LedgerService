// <copyright file="AtlasLedgerServer.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server
{
    using System;
    using System.Linq;
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

            // Callers: development sign-in only, until bearer tokens from the identity provider are wired in.
            if (!builder.Environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    $"No authentication is configured for the '{builder.Environment.EnvironmentName}' environment. Production signs callers in with bearer tokens from the identity provider, which is not wired in yet; run in Development.");
            }

            builder.Services.AddAuthentication(DevelopmentAuthentication.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthentication>(DevelopmentAuthentication.SchemeName, null);

            // Tools: the app registrations allowed to act as themselves in any tenant, from configuration (placeholders until
            // the registrations exist).  The development tool is allowed only alongside development sign-in.
            builder.Services.Configure<ToolAccessOptions>(configuration.GetSection(ToolAccessOptions.SectionName));
            builder.Services.PostConfigure<ToolAccessOptions>(tools => tools.ClientIds = tools.ClientIds.Append(DevelopmentAuthentication.ToolClientId).ToArray());

            configureServices?.Invoke(builder.Services);
            var application = builder.Build();
            foreach (var clientId in application.Services.GetRequiredService<IOptions<ToolAccessOptions>>().Value.ClientIds.Where(ToolAccessOptions.IsPlaceholder))
            {
                application.Logger.LogWarning("Tools:ClientIds still holds the placeholder '{Placeholder}'; no application can use it. Replace it with the app registration's client ID.", clientId);
            }

            application.MapHealthChecks("/health");
            application.UseLedgerContract();
            application.UseAuthentication();
            application.UseAuthorization();
            application.MapControllers();
            return application;
        }
    }
}
