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
        private WebApplication? application;

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
                "--Logging:LogLevel:Default=None",
                "--Logging:LogLevel:Microsoft.Hosting.Lifetime=None",
                "--Logging:LogLevel:AtlasLedger=None",
            });
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
