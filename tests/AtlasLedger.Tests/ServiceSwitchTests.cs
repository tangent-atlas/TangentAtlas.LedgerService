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
    using Microsoft.Extensions.Options;
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
        /// The settings choose which services start on: by default the rules engine, on until turned off; empty starts them
        /// all off, for diagnostics; a name that isn't a service stops the server starting.
        /// </summary>
        [Fact]
        public void TheSettingsChooseWhichServicesStartOn()
        {
            var byDefault = new ServiceSwitchboard(TimeProvider.System, Options.Create(new ServiceSwitchOptions()));
            Assert.True(byDefault[ServiceSwitchboard.Rules].IsOnUntilTurnedOff);
            Assert.False(byDefault[ServiceSwitchboard.MarketFeed].IsOn);
            Assert.False(byDefault[ServiceSwitchboard.Broker].IsOn);   // nothing is sent to a broker unless asked for
            Assert.True(server::AtlasLedger.Server.Switches.ServiceState.From(byDefault[ServiceSwitchboard.Rules]) is { IsOn: true, OnUntil: null });

            var both = new ServiceSwitchboard(TimeProvider.System, Options.Create(new ServiceSwitchOptions { StartOn = " rules , market-feed,broker " }));
            Assert.All(both.All, service => Assert.True(service.IsOnUntilTurnedOff));

            var none = new ServiceSwitchboard(TimeProvider.System, Options.Create(new ServiceSwitchOptions { StartOn = string.Empty }));
            Assert.All(none.All, service => Assert.False(service.IsOn));

            Assert.Throws<InvalidOperationException>(() => new ServiceSwitchboard(TimeProvider.System, Options.Create(new ServiceSwitchOptions { StartOn = "compliance" })));

            // On until turned off waits for a turn on or off, not for a time.
            var rules = byDefault[ServiceSwitchboard.Rules];
            var waited = rules.WaitForChangeAsync(TestContext.Current.CancellationToken);
            Assert.False(waited.IsCompleted);
            rules.TurnOff();
            Assert.False(rules.IsOn);
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
            Assert.Equal(new[] { ServiceSwitchboard.Broker, ServiceSwitchboard.MarketFeed, ServiceSwitchboard.Rules }, services.Select(service => service.Name));
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
        /// With the rules engine off there are no rules: no alerts and no holdings (409); turned on, the rules raise the alert
        /// the data calls for and derive the holdings; turned off again, they stop, the holdings go, and the alert stays until
        /// they run again.
        /// </summary>
        /// <returns>A task that completes when the test is done.</returns>
        [Fact]
        public async Task TheRulesRunOnlyWhileTheEngineIsOn()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var fixture = new ServerFixture();
            await fixture.InitializeAsync();
            var tool = new LedgerApiClient(fixture.HttpClient, DevelopmentCallers.Tool);
            var tenant = Guid.NewGuid();
            var nodeId = Guid.NewGuid();
            var accountWithoutCustodian = new LedgerScript()
                .Insert("Node", new NodeDto { NodeId = nodeId, DisplayName = "Jones IRA", FullName = "Jones IRA", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account })
                .Insert("Account", new AccountDto { AccountId = nodeId, AccountType = AccountType.ManagedAccount, Currency = CurrencyCode.UnitedStatesDollar });
            await tool.ApplyAsync(tenant, accountWithoutCustodian, cancellationToken);

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            Assert.Empty(await tool.ListAsync<AlertDto>(tenant, "Alert", cancellationToken));
            Assert.Equal(HttpStatusCode.Conflict, (await Assert.ThrowsAsync<LedgerApiException>(() => tool.GetHoldingsAsync(tenant, cancellationToken))).StatusCode);

            await tool.SetServiceAsync(ServiceSwitchboard.Rules, true, TimeSpan.FromMinutes(5), cancellationToken);
            await WaitUntilAsync(async () => (await tool.ListAsync<AlertDto>(tenant, "Alert", cancellationToken)).Any(alert => alert.Rule == "MissingCustodian"));
            Assert.Empty((await tool.GetHoldingsAsync(tenant, cancellationToken)).Holdings);   // derived: the account holds nothing yet

            await tool.SetServiceAsync(ServiceSwitchboard.Rules, false, null, cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, (await Assert.ThrowsAsync<LedgerApiException>(() => tool.GetHoldingsAsync(tenant, cancellationToken))).StatusCode);
            await tool.InsertAsync(tenant, "Symbol", new SymbolDto { Scheme = "Custodian", Code = "9999-0001", NodeId = nodeId }, cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            Assert.Contains(await tool.ListAsync<AlertDto>(tenant, "Alert", cancellationToken), alert => alert.Rule == "MissingCustodian");

            await tool.SetServiceAsync(ServiceSwitchboard.Rules, true, TimeSpan.FromMinutes(5), cancellationToken);
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
