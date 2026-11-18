// <copyright file="TenantClearingTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias client;

namespace AtlasLedger.Tests
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using client::AtlasLedger.Client;
    using client::AtlasLedger.Client.Api;
    using client::AtlasLedger.Client.Scripts;
    using client::AtlasLedger.Client.TestData;
    using client::AtlasLedger.Model;
    using Xunit;

    /// <summary>
    /// Loading is idempotent (the scripts supply every key), and clearing a tenant takes its holdings out with events and
    /// then deletes its accounts, leaving the log and the reference data.
    /// </summary>
    public sealed class TenantClearingTests : IClassFixture<ServerFixture>
    {
        private readonly ServerFixture server;

        public TenantClearingTests(ServerFixture server)
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
        public async Task ClearingTakesTheHoldingsOutThenDeletesTheAccountsAndTheUsers()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var tool = new LedgerApiClient(this.server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            var state = Joe10State.Generic();
            await tool.SetServiceAsync("rules", isOn: true, cancellationToken: cancellationToken);
            await Commands.LoadAsync(tool, tenantId, SampleData.FirmA, TextWriter.Null, cancellationToken);
            await Commands.LoadAsync(tool, tenantId, SampleData.JonesIra, TextWriter.Null, cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
            await tool.CommitTransactionsAsync(tenantId, Joe10Account.History(state, 1_000_000m), cancellationToken);
            await tool.ApplyAsync(tenantId, new LedgerScript().Insert("User", new UserDto { UserId = Guid.NewGuid(), IdentityIssuer = "issuer", IdentitySubject = "subject" }), cancellationToken);
            Assert.Equal(4, (await tool.ListAsync<AccountDto>(tenantId, "Account", cancellationToken)).Count);
            Assert.Equal(11, (await HoldingsAsync(tool, tenantId, found => found.Holdings.Count == 11)).Holdings.Count);   // ten companies and dollars

            var output = new StringWriter();
            await TenantClearing.ClearAsync(tool, tool, tenantId, holdingsOnly: false, removeUsers: true, TimeSpan.Zero, output, cancellationToken);

            Assert.Empty((await HoldingsAsync(tool, tenantId, found => found.Holdings.Count == 0)).Holdings);
            Assert.Empty(await tool.ListAsync<AccountDto>(tenantId, "Account", cancellationToken));
            Assert.Empty(await tool.ListAsync<BrokerageDto>(tenantId, "Brokerage", cancellationToken));
            Assert.Empty(await tool.ListAsync<UserDto>(tenantId, "User", cancellationToken));
            Assert.DoesNotContain(await tool.ListAsync<SymbolDto>(tenantId, "Symbol", cancellationToken), symbol => symbol.Scheme == SymbolScheme.Custodian);
            Assert.DoesNotContain(await tool.ListAsync<NodeDto>(tenantId, "Node", cancellationToken), node => node.NodeType == NodeType.Account);

            // The reference data and the log stay: the institution, the assets, and every event, the transfer out among them.
            Assert.Equal(2, (await tool.ListAsync<InstitutionDto>(tenantId, "Institution", cancellationToken)).Count);
            Assert.Contains(await tool.ListAsync<AssetDto>(tenantId, "Asset", cancellationToken), asset => asset.AssetId == Joe10Account.DollarId);
            var events = await tool.ListAsync<EventDto>(tenantId, "Event", cancellationToken);
            Assert.Contains(events, row => row.EventType == EventType.ReceiveAndDeliver && row.Source == EventSource.Operator);
            Assert.Contains(events, row => row.EventType == EventType.CashMovement && row.Source == EventSource.Operator);

            // Clearing again changes nothing.
            var count = events.Count;
            await TenantClearing.ClearAsync(tool, tool, tenantId, holdingsOnly: false, removeUsers: true, TimeSpan.Zero, TextWriter.Null, cancellationToken);
            Assert.Equal(count, (await tool.ListAsync<EventDto>(tenantId, "Event", cancellationToken)).Count);

            // Loading the same scripts again brings the accounts back, holding what the log adds up to: nothing.
            await Commands.LoadAsync(tool, tenantId, SampleData.FirmA, TextWriter.Null, cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
            await tool.CommitTransactionsAsync(tenantId, Joe10Account.History(state, 1_000_000m), cancellationToken);
            Assert.Equal(3, (await tool.ListAsync<AccountDto>(tenantId, "Account", cancellationToken)).Count);
            await Task.Delay(500, cancellationToken);
            Assert.Empty((await HoldingsAsync(tool, tenantId, _ => true)).Holdings);

            async Task<TenantHoldings> HoldingsAsync(LedgerApiClient api, Guid tenant, Func<TenantHoldings, bool> condition)
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
                while (true)
                {
                    try
                    {
                        var holdings = await api.GetHoldingsAsync(tenant, cancellationToken);
                        if (condition(holdings) || DateTimeOffset.UtcNow >= deadline)
                        {
                            return holdings;
                        }
                    }
                    catch (LedgerApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict && DateTimeOffset.UtcNow < deadline)
                    {
                        // The rules haven't run for the tenant yet.
                    }

                    await Task.Delay(100, cancellationToken);
                }
            }
        }

        [Fact]
        public void TheHoldingsLeaveAsATransferOutInKindAndAWithdrawalOfTheCash()
        {
            var account = Guid.NewGuid();
            var stock = Guid.NewGuid();
            var dollars = Guid.NewGuid();
            var holdings = new TenantHoldings(
                DateTimeOffset.UnixEpoch,
                [
                    new Holding(account, stock, AssetType.Equity, 10m, 900m, false, 100m, "USD", "Market", "USD", 1_000m, null, null),
                    new Holding(account, dollars, AssetType.Cash, 250m, null, false, 1m, "USD", "Market", "USD", 250m, null, null),
                    new Holding(Guid.NewGuid(), stock, AssetType.Equity, 0m, null, false, 100m, "USD", "Market", "USD", 0m, null, null),
                ],
                [],
                [],
                []);

            var (accountId, transaction) = Assert.Single(TenantClearing.HoldingsOut(holdings, DateTimeOffset.UnixEpoch, Guid.NewGuid));

            Assert.Equal(account, accountId);
            Assert.Equal([EventType.ReceiveAndDeliver, EventType.CashMovement], transaction.Events.Select(message => message.EventType));
            var transfer = Assert.IsType<ReceiveAndDeliverEvent>(EventMessages.Read(transaction.Events[0]));
            Assert.Equal(DeliveryReason.TransferOut, transfer.Reason);
            Assert.Equal([(stock, -10m, (decimal?)-1_000m)], transfer.Items.Select(item => (item.AssetId, item.Quantity, item.Amount)));
            var withdrawal = Assert.IsType<CashMovementEvent>(EventMessages.Read(transaction.Events[1]));
            Assert.Equal([(dollars, -250m, (decimal?)-250m)], withdrawal.Items.Select(item => (item.AssetId, item.Quantity, item.Amount)));
        }
    }
}
