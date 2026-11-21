// <copyright file="ReloadTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias client;

namespace AtlasLedger.Tests
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using client::AtlasLedger.Client;
    using client::AtlasLedger.Client.Api;
    using client::AtlasLedger.Client.Scripts;
    using client::AtlasLedger.Model;
    using Xunit;

    /// <summary>
    /// Loading is idempotent (the scripts supply every key), and a Replica like the website's follows what is loaded and
    /// deleted as it happens.
    /// </summary>
    public sealed class ReloadTests : IClassFixture<ServerFixture>
    {
        private static readonly string[] WebsiteTables = ["Position", "EffectivePrice", "CurrencyRate", "AccountFlow", "AccountDay", "AccountStatistic", "Weight", "Symbol", "Institution", "Edge", "Portfolio", "PortfolioDay", "PortfolioStatistic", "Favorite", "Restriction"];

        private readonly ServerFixture server;

        public ReloadTests(ServerFixture server)
        {
            this.server = server;
        }

        [Fact]
        public async Task LoadingTheSameScriptTwiceChangesNothing()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var tool = new LedgerApiClient(this.server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            await Commands.LoadAsync(tool, tenantId, SampleData.FirmA, TextWriter.Null, cancellationToken);
            var first = await tool.ListAsync<AccountDto>(tenantId, "Account", cancellationToken);

            await Commands.LoadAsync(tool, tenantId, SampleData.FirmA, TextWriter.Null, cancellationToken);

            var second = await tool.ListAsync<AccountDto>(tenantId, "Account", cancellationToken);
            Assert.Equal(2, second.Count);
            Assert.Equal(first.Select(account => (account.AccountId, account.RowVersion)), second.Select(account => (account.AccountId, account.RowVersion)));
        }

        [Fact]
        public async Task AReplicaLikeTheWebsitesSeesTheAccountsArriveAndGo()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var tool = new LedgerApiClient(this.server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            await Commands.LoadAsync(tool, tenantId, SampleData.FirmA, TextWriter.Null, cancellationToken);
            var replica = new Ledger();
            var sync = new client::AtlasLedger.Model.Replica.LedgerSyncClient(
                this.server.HttpClient,
                replica,
                tenantId,
                WebsiteTables,
                (request, _) =>
                {
                    request.Headers.Add(DevelopmentCallers.Header, DevelopmentCallers.Tool);
                    return ValueTask.CompletedTask;
                },
                LedgerApiClient.SerializerOptions);
            await sync.SyncAsync(TimeSpan.Zero, cancellationToken);
            Assert.Equal(2, AccountsIn(replica, tenantId));
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var following = sync.RunAsync(stopping.Token);

            // One more account arrives; then every account goes, each with its custodian number, brokerage row and node.
            await Commands.LoadAsync(tool, tenantId, SampleData.JonesIra, TextWriter.Null, cancellationToken);
            await UntilAsync(() => AccountsIn(replica, tenantId) == 3);
            var symbols = await tool.ListAsync<SymbolDto>(tenantId, "Symbol", cancellationToken);
            var brokerages = await tool.ListAsync<BrokerageDto>(tenantId, "Brokerage", cancellationToken);
            var nodes = await tool.ListAsync<NodeDto>(tenantId, "Node", cancellationToken);
            var deletion = new LedgerScript();
            foreach (var account in await tool.ListAsync<AccountDto>(tenantId, "Account", cancellationToken))
            {
                foreach (var symbol in symbols.Where(symbol => symbol.NodeId == account.AccountId))
                {
                    deletion.Delete("Symbol", new { symbol.Scheme, symbol.Code }, symbol.RowVersion);
                }

                foreach (var brokerage in brokerages.Where(brokerage => brokerage.AccountId == account.AccountId))
                {
                    deletion.Delete("Brokerage", new { brokerage.AccountId }, brokerage.RowVersion);
                }

                deletion.Delete("Account", new { account.AccountId }, account.RowVersion);
                var node = nodes.Single(node => node.NodeId == account.AccountId);
                deletion.Delete("Node", new { node.NodeId }, node.RowVersion);
            }

            await tool.ApplyAsync(tenantId, deletion, cancellationToken);
            await UntilAsync(() => AccountsIn(replica, tenantId) == 0);
            Assert.False(following.IsFaulted, following.Exception?.ToString());
            stopping.Cancel();

            async Task UntilAsync(Func<bool> condition)
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
                while (!condition())
                {
                    Assert.True(DateTimeOffset.UtcNow < deadline, $"The Replica shows {AccountsIn(replica, tenantId)} account(s).");
                    await Task.Delay(100, cancellationToken);
                }
            }
        }

        private static int AccountsIn(Ledger replica, Guid tenantId)
        {
            var partition = replica.FindPartition(tenantId) ?? throw new InvalidOperationException("The Replica has no copy of the tenant.");
            lock (partition.SyncRoot)
            {
                return partition.Accounts.Count();
            }
        }
    }
}
