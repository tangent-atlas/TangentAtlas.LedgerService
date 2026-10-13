// <copyright file="EndToEndTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias client;
extern alias server;

namespace AtlasLedger.Tests
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading.Tasks;
    using client::AtlasLedger.Client;
    using client::AtlasLedger.Client.Api;
    using client::AtlasLedger.Client.Scripts;
    using server::AtlasLedger.Server;
    using Xunit;

    /// <summary>
    /// Runs the real client against the real server: the division of labor the products will have, tested in one process.
    /// </summary>
    public sealed class EndToEndTests : IClassFixture<ServerFixture>
    {
        private readonly ServerFixture server;

        /// <summary>
        /// Initializes a new instance of the <see cref="EndToEndTests"/> class.
        /// </summary>
        /// <param name="server">The running server.</param>
        public EndToEndTests(ServerFixture server)
        {
            this.server = server;
        }

        /// <summary>
        /// Every step of the client's walkthrough gets the status and result it expects, including the compliance alerts
        /// raised and cleared by the server's rules and market feed.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task TheWalkthroughSucceeds()
        {
            using var output = new StringWriter();
            var failures = await new Walkthrough(this.server.HttpClient, output, TimeSpan.FromSeconds(10)).RunAsync(TestContext.Current.CancellationToken);
            Assert.True(failures == 0, output.ToString());
        }

        /// <summary>
        /// A script exported from one tenant loads into another unchanged, and exporting that tenant gives the same rows.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task ScriptsRoundTrip()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var tool = new LedgerApiClient(this.server.HttpClient, DevelopmentCallers.Tool);
            var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
            await tool.ApplyAsync(first, await LedgerScript.ReadAsync(Walkthrough.FirmAScript, cancellationToken), cancellationToken);
            var exported = await Commands.ExportAsync(tool, first, cancellationToken);
            await tool.ApplyAsync(second, exported, cancellationToken);
            var again = await Commands.ExportAsync(tool, second, cancellationToken);
            Assert.Equal(Rows(exported), Rows(again));
            Assert.Equal(7, Rows(exported).Length);
        }

        /// <summary>
        /// A batch larger than the server's limit is refused with 413, naming the limit, and changes nothing; the client loads a
        /// large script in several batches of at most that size, in order.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task LargeBatchesAreRefusedAndScriptsLoadInParts()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var script = await LedgerScript.ReadAsync(Walkthrough.FirmAScript, cancellationToken);

            // The reference: the same script loaded in one batch by the fixture's server, which has the default limit.
            var fixtureTool = new LedgerApiClient(this.server.HttpClient, DevelopmentCallers.Tool);
            var reference = Guid.NewGuid();
            await fixtureTool.ApplyAsync(reference, script, cancellationToken);
            var expected = Rows(await Commands.ExportAsync(fixtureTool, reference, cancellationToken));

            await using var limited = AtlasLedgerServer.Build(new[]
            {
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                "--Batch:MaximumOperations=3",
                "--MarketFeed:Enabled=false",
                "--Logging:LogLevel:Default=None",
            });
            await limited.StartAsync(cancellationToken);
            using var http = new HttpClient { BaseAddress = new Uri(limited.Urls.First() + "/") };
            var tool = new LedgerApiClient(http, DevelopmentCallers.Tool);
            Assert.True(script.Operations.Count > 3);

            var tenant = Guid.NewGuid();
            var refused = await Assert.ThrowsAsync<LedgerApiException>(() => tool.ApplyAsync(tenant, script, cancellationToken));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
            using (var problem = JsonDocument.Parse(refused.ResponseBody!))
            {
                Assert.Equal(3, problem.RootElement.GetProperty("maximumOperations").GetInt32());
            }

            Assert.Empty(Rows(await Commands.ExportAsync(tool, tenant, cancellationToken)));

            var results = await tool.ApplyInBatchesAsync(tenant, script, maximumOperations: 3, cancellationToken);
            Assert.Equal(script.Operations.Count, results.Count);
            Assert.Equal(expected, Rows(await Commands.ExportAsync(tool, tenant, cancellationToken)));
            await limited.StopAsync(cancellationToken);
        }

        /// <summary>
        /// A client built from a different model is refused with 426 and told both models.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task AClientFromAnotherModelIsRefused()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "v1/me/tenants");
            request.Headers.Add(client::AtlasLedger.Model.Ledger.VersionHeader, client::AtlasLedger.Model.Ledger.Version);
            request.Headers.Add(client::AtlasLedger.Model.Ledger.FingerprintHeader, "00000000000000000000000000000000");
            request.Headers.Add(DevelopmentCallers.Header, DevelopmentCallers.Tool);
            using var response = await this.server.HttpClient.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
            Assert.Equal(server::AtlasLedger.Model.Ledger.Fingerprint, response.Headers.GetValues(server::AtlasLedger.Model.Ledger.FingerprintHeader).Single());
        }

        /// <summary>
        /// The client and server are generated from one model file, so they agree on its fingerprint.
        /// </summary>
        [Fact]
        public void ClientAndServerShareTheModel()
        {
            Assert.Equal(server::AtlasLedger.Model.Ledger.Fingerprint, client::AtlasLedger.Model.Ledger.Fingerprint);
            Assert.Equal("Primary", server::AtlasLedger.Model.Ledger.Role);
            Assert.Equal("Replica", client::AtlasLedger.Model.Ledger.Role);
        }

        /// <summary>
        /// Outside Development the server refuses to start, rather than fall back to development sign-in.
        /// </summary>
        [Fact]
        public void TheServerRefusesToStartWithoutRealAuthentication()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => AtlasLedgerServer.Build(new[] { "--environment", "Production", "--urls", "http://127.0.0.1:0" }));
            Assert.Contains("Production", exception.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A script's rows as comparable text, without their versions, which belong to the tenant they came from, and
        /// without alerts, which each tenant's rules raise and clear on their own schedule (the market feed is running).
        /// </summary>
        private static string[] Rows(LedgerScript script) => script.Operations
            .Where(operation => operation.Table != "Alert")
            .Select(operation => $"{operation.Table} {string.Join(",", operation.Row!.Value.EnumerateObject().Where(property => property.Name != "rowVersion").Select(property => $"{property.Name}={property.Value}"))}")
            .OrderBy(row => row, StringComparer.Ordinal)
            .ToArray();
    }
}
