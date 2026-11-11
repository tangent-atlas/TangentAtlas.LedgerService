// <copyright file="ServerFixture.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias server;

namespace AtlasLedger.Tests
{
    using System;
    using System.Linq;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Builder;
    using server::AtlasLedger.Server;
    using Xunit;

    /// <summary>
    /// Runs one real server for a test class, on a free local port, with a fast market feed.
    /// </summary>
    public sealed class ServerFixture : IAsyncLifetime
    {
        private readonly string[] extraArgs;

        private readonly Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configureServices;

        private WebApplication? application;

        /// <summary>
        /// Initializes a new instance of the <see cref="ServerFixture"/> class, with the test settings.
        /// </summary>
        public ServerFixture()
            : this(Array.Empty<string>(), null)
        {
        }

        private ServerFixture(string[] extraArgs, Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configureServices)
        {
            this.extraArgs = extraArgs;
            this.configureServices = configureServices;
        }

        /// <summary>
        /// Makes a server with more settings and services than the test settings (a fixture for a class has one constructor).
        /// </summary>
        /// <param name="extraArgs">Settings that override the test settings (for example <c>--Services:StartOn=rules,broker</c>).</param>
        /// <param name="configureServices">Changes to the services, after the server's own.</param>
        /// <returns>The server, not started yet (call <see cref="InitializeAsync"/>).</returns>
        public static ServerFixture With(string[] extraArgs, Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configureServices = null) =>
            new ServerFixture(extraArgs, configureServices);

        /// <summary>
        /// Gets an HTTP client addressed to the server.
        /// </summary>
        public HttpClient HttpClient { get; private set; } = new HttpClient();

        /// <inheritdoc/>
        public async ValueTask InitializeAsync()
        {
            this.application = AtlasLedgerServer.Build(new[]
            {
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                "--MarketFeed:TickInterval=00:00:00.300",
                "--MarketFeed:BadPrintInterval=00:00:01",
                "--Services:StartOn=",   // every test starts with every service off, and turns on what it needs
                "--Logging:LogLevel:Default=None",
                "--Logging:LogLevel:Microsoft.Hosting.Lifetime=None",
                "--Logging:LogLevel:AtlasLedger=None",
            }.Concat(this.extraArgs).ToArray(), this.configureServices);
            await this.application.StartAsync().ConfigureAwait(false);
            this.HttpClient = new HttpClient { BaseAddress = new Uri(this.application.Urls.First() + "/") };
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            this.HttpClient.Dispose();
            if (this.application != null)
            {
                await this.application.StopAsync().ConfigureAwait(false);
                await this.application.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
