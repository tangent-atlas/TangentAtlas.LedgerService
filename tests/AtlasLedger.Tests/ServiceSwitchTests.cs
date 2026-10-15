// <copyright file="ServiceSwitchTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias client;
extern alias server;

namespace AtlasLedger.Tests
{
    using System;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using client::AtlasLedger.Client.Api;
    using client::AtlasLedger.Client.Scripts;
    using client::AtlasLedger.Model;
    using server::AtlasLedger.Server.Switches;
    using Xunit;

    /// <summary>
    /// The optional services start off, turn on for a while and then off by themselves, and only an allowed tool can switch
    /// them.  Each test uses its own server, since the switches are server-wide.
    /// </summary>
    public sealed class ServiceSwitchTests
    {
        /// <summary>
        /// A switch starts off, is on until the time it was given, and is off once that time passes, waking a waiter.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task ASwitchTurnsItselfOffWhenItsTimeRunsOut()
        {
            var serviceSwitch = new ServiceSwitch("test", "A test service.", TimeProvider.System);
            Assert.False(serviceSwitch.IsOn);
            Assert.Null(serviceSwitch.OnUntil);

            serviceSwitch.TurnOn(TimeSpan.FromMilliseconds(300));
            Assert.True(serviceSwitch.IsOn);
            var waited = serviceSwitch.WaitForChangeAsync(TestContext.Current.CancellationToken);
            await waited.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(serviceSwitch.IsOn);

            // Off (expired) waits for the next turn on or off, rather than returning at once.
            var next = serviceSwitch.WaitForChangeAsync(TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(next.IsCompleted);
            serviceSwitch.TurnOn(TimeSpan.FromHours(1));
            await next.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            serviceSwitch.TurnOff();
            Assert.False(serviceSwitch.IsOn);
        }

        /// <summary>
        /// Only an allowed tool may see or change the services: anyone else signed in gets 404, as for a tenant they do not
        /// belong to, and anyone not signed in gets 401.  A duration over the maximum is refused.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task OnlyAnAllowedToolMaySwitchServices()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var fixture = new ServerFixture();
            await fixture.InitializeAsync();
            var tool = new LedgerApiClient(fixture.HttpClient, DevelopmentCallers.Tool);
            var advisor = new LedgerApiClient(fixture.HttpClient, DevelopmentCallers.Advisor);

            var services = await tool.GetServicesAsync(cancellationToken);
            Assert.Equal(new[] { ServiceSwitchboard.Compliance, ServiceSwitchboard.MarketFeed }, services.Select(service => service.Name));
            Assert.All(services, service => Assert.False(service.IsOn));

            Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<LedgerApiException>(() => advisor.GetServicesAsync(cancellationToken))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<LedgerApiException>(() => advisor.SetServiceAsync(ServiceSwitchboard.MarketFeed, true, null, cancellationToken))).StatusCode);
            using (var anonymous = await fixture.HttpClient.GetAsync("v1/system/services", cancellationToken))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            }

            Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<LedgerApiException>(() => tool.SetServiceAsync("no-such-service", true, null, cancellationToken))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Assert.ThrowsAsync<LedgerApiException>(() => tool.SetServiceAsync(ServiceSwitchboard.MarketFeed, true, TimeSpan.FromDays(2), cancellationToken))).StatusCode);

            var before = DateTimeOffset.UtcNow;
            var on = await tool.SetServiceAsync(ServiceSwitchboard.MarketFeed, true, null, cancellationToken);
            Assert.True(on.IsOn);
            Assert.InRange(on.OnUntil!.Value, before + TimeSpan.FromHours(4) - TimeSpan.FromMinutes(1), before + TimeSpan.FromHours(4) + TimeSpan.FromMinutes(1));
            var off = await tool.SetServiceAsync(ServiceSwitchboard.MarketFeed, false, null, cancellationToken);
            Assert.False(off.IsOn);
            Assert.Null(off.OnUntil);
        }

        /// <summary>
        /// With compliance off, the rules do nothing; turned on, they raise the alert the data calls for; turned off again,
        /// they stop, and the alert stays until they run again.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task ComplianceRunsOnlyWhileOn()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var fixture = new ServerFixture();
            await fixture.InitializeAsync();
            var tool = new LedgerApiClient(fixture.HttpClient, DevelopmentCallers.Tool);
            var tenant = Guid.NewGuid();
            var nodeId = Guid.NewGuid();
            var accountWithoutCustodian = new LedgerScript()
                .Insert("Node", new NodeDto { NodeId = nodeId, DisplayName = "Jones IRA", FullName = "Jones IRA", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account })
                .Insert("Account", new AccountDto { AccountId = nodeId, AccountType = AccountType.ManagedAccount });
            await tool.ApplyAsync(tenant, accountWithoutCustodian, cancellationToken);

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            Assert.Empty(await tool.ListAsync<AlertDto>(tenant, "Alert", cancellationToken));

            await tool.SetServiceAsync(ServiceSwitchboard.Compliance, true, TimeSpan.FromMinutes(5), cancellationToken);
            await WaitUntilAsync(async () => (await tool.ListAsync<AlertDto>(tenant, "Alert", cancellationToken)).Any(alert => alert.Rule == "MissingCustodian"));

            await tool.SetServiceAsync(ServiceSwitchboard.Compliance, false, null, cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            await tool.InsertAsync(tenant, "Symbol", new SymbolDto { Scheme = "Custodian", Code = "9999-0001", NodeId = nodeId }, cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            Assert.Contains(await tool.ListAsync<AlertDto>(tenant, "Alert", cancellationToken), alert => alert.Rule == "MissingCustodian");

            await tool.SetServiceAsync(ServiceSwitchboard.Compliance, true, TimeSpan.FromMinutes(5), cancellationToken);
            await WaitUntilAsync(async () => !(await tool.ListAsync<AlertDto>(tenant, "Alert", cancellationToken)).Any());
        }

        private static async Task WaitUntilAsync(Func<Task<bool>> condition)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            while (!await condition())
            {
                Assert.True(DateTimeOffset.UtcNow < deadline, "The server did not get there within 10 seconds.");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }
    }
}
