// <copyright file="OrderRoutingTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias client;
extern alias server;

namespace AtlasLedger.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using client::AtlasLedger.Client.Api;
    using client::AtlasLedger.Client.Scripts;
    using client::AtlasLedger.Client.TestData;
    using client::AtlasLedger.Model;
    using Microsoft.Extensions.DependencyInjection;
    using server::AtlasLedger.Server.Brokers;
    using server::AtlasLedger.Server.Orders;
    using Xunit;

    /// <summary>
    /// Trading end to end: the rules rebalance an account, the router sends the orders to a broker and records its reports,
    /// and the rules book the fills.  The simulated broker trades against the simulated feed's prices; a fake broker stands
    /// in for a real one that already has an order.
    /// </summary>
    public sealed class OrderRoutingTests
    {
        private const string StateJson = """
            {
              "joe10": 1.0,
              "last_date": "2026-11-06",
              "portfolio": {
                "stocks_after": 0.5,
                "units": { "AAA": 0.003, "BBB": 0.0060006000600060006 },
                "weights": { "AAA": 0.6, "BBB": 0.4 }
              }
            }
            """;

        private static readonly string[] Trading =
        [
            "--Services:StartOn=rules,market-feed,broker",
            "--MarketFeed:TickInterval=00:00:00.100",
            "--MarketFeed:BadPrintInterval=1.00:00:00",
            "--Broker:PollInterval=00:00:00.100",
            "--Broker:SimulatedFees=1",
        ];

        [Fact]
        public async Task TheAccountIsRebalancedOnceADayThroughTheBroker()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var server = ServerFixture.With(Trading);
            await server.InitializeAsync();
            var tool = new LedgerApiClient(server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            var state = ReadState();
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Opening(state, 1_000_000m), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Weights(state), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Plan(EventSource.Simulator, timeOfDay: 0, weekdaysOnly: false), cancellationToken);

            // The rebalance buys both companies with half the account; the simulated broker fills each at once, $1 in fees.
            var orders = await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Orders.Count == 2 && found.Orders.All(order => order.Status == "Filled"));
            Assert.All(orders.Orders, order => Assert.Equal((OrderSide.Buy, OrderOrigin.Rebalance, 1m), (order.Side, order.Origin, order.Fees)));
            var holdings = await WaitForAsync(() => tool.GetHoldingsAsync(tenantId, cancellationToken), found => found.Holdings.Count == 3);
            var account = holdings.Accounts.Single();
            Assert.Equal((1_000_000m, Rebalancing.TradingDay(DateTimeOffset.UtcNow), 2), (account.NetContributions, account.LastRebalance!.Value, account.LastRebalanceOrders!.Value));
            Assert.Equal((0.5m, "Joe 10"), (account.TargetCash!.Value, account.ModelName));
            foreach (var holding in holdings.Holdings.Where(holding => holding.AssetType == AssetType.Equity))
            {
                // Bought at the price of the moment, in whole shares: each within a point of its target.
                Assert.InRange(holding.Weight!.Value, holding.TargetWeight!.Value - 0.01m, holding.TargetWeight!.Value + 0.01m);
            }

            // The fees are in the cost, and on the bookings as one amount.
            var bookings = (await tool.ListAsync<TransactionDto>(tenantId, "Transaction", cancellationToken)).Where(transaction => transaction.Source == EventSource.Rules).ToArray();
            Assert.Equal(2, bookings.Length);
            Assert.All(bookings, booking => Assert.Equal(1m, booking.Fees));

            using (var printed = new StringWriter())
            {
                await client::AtlasLedger.Client.Commands.HoldingsAsync(tool, tenantId, printed, cancellationToken);
                Assert.Contains("Paid in 1,000,000.00", printed.ToString(), StringComparison.Ordinal);
            }

            // Once a day: the rules keep running as prices move, and nothing more is ordered.
            await Task.Delay(1_000, cancellationToken);
            Assert.Single(await tool.ListAsync<RebalanceDto>(tenantId, "Rebalance", cancellationToken));
            Assert.Equal(2, (await tool.ListAsync<OrderDto>(tenantId, "Order", cancellationToken)).Count);
        }

        [Fact]
        public async Task AnAccountWithoutAModelIsNotRebalanced()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var server = ServerFixture.With(Trading);
            await server.InitializeAsync();
            var tool = new LedgerApiClient(server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            var state = ReadState();
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
            var account = (await tool.ListAsync<AccountDto>(tenantId, "Account", cancellationToken)).Single();
            await tool.ApplyAsync(tenantId, new LedgerScript().Update("Account", new { account.AccountId }, new AccountDto { AccountId = account.AccountId, AccountType = account.AccountType, Currency = account.Currency, ModelId = null, RowVersion = account.RowVersion }), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Opening(state, 1_000_000m), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Weights(state), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Plan(EventSource.Simulator, timeOfDay: 0, weekdaysOnly: false), cancellationToken);

            // The plan, the weights and the prices are all there; without a model the account is left alone.
            var holdings = await WaitForAsync(() => tool.GetHoldingsAsync(tenantId, cancellationToken), found => found.Holdings.Count == 1);
            await Task.Delay(1_500, cancellationToken);
            Assert.Empty(await tool.ListAsync<RebalanceDto>(tenantId, "Rebalance", cancellationToken));
            Assert.Equal((null, null), (holdings.Accounts.Single().ModelId, holdings.Accounts.Single().TargetCash));
        }

        [Fact]
        public async Task TwoTenantsWithTheSameKeysAreSeparateOrdersAtTheBroker()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var server = ServerFixture.With(Trading);
            await server.InitializeAsync();
            var tool = new LedgerApiClient(server.HttpClient, DevelopmentCallers.Tool);
            var state = ReadState();
            var (orderId, brokerOrderId) = (Guid.NewGuid(), Guid.NewGuid());
            var tenants = new[] { (TenantId: Guid.NewGuid(), Quantity: 10m), (TenantId: Guid.NewGuid(), Quantity: 20m) };
            foreach (var (tenantId, _) in tenants)
            {
                await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
                await tool.ApplyAsync(tenantId, Joe10Account.Opening(state, 1_000_000m), cancellationToken);
            }

            await Task.Delay(1_000, cancellationToken);   // the simulated feed prices AAA

            // The same order, allocation and broker order keys in both tenants (tenants share nothing), for different quantities.
            var now = DateTimeOffset.UtcNow;
            foreach (var (tenantId, quantity) in tenants)
            {
                await tool.ApplyAsync(tenantId, new LedgerScript()
                    .Insert("Order", new OrderDto { OrderId = orderId, AssetId = Joe10Account.AssetIdOf("AAA"), Side = OrderSide.Buy, Origin = OrderOrigin.Manual, EnteredTime = now })
                    .Insert("Allocation", new AllocationDto { OrderId = orderId, AccountId = Joe10Account.AccountId, Quantity = quantity })
                    .Insert("BrokerOrder", new BrokerOrderDto { BrokerOrderId = brokerOrderId, OrderId = orderId, Broker = EventSource.Simulator, Quantity = quantity, OrderType = OrderType.Market, TimeInForce = TimeInForce.Day, EnteredTime = now }), cancellationToken);
            }

            // Each fills for its own quantity: the broker saw two orders, not one sent twice.
            foreach (var (tenantId, quantity) in tenants)
            {
                var orders = await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Orders.SingleOrDefault()?.Status == "Filled");
                Assert.Equal(quantity, orders.Orders.Single().Filled);
            }

            Assert.NotEqual(BrokerOrderRequest.ClientOrderIdFor(tenants[0].TenantId, brokerOrderId), BrokerOrderRequest.ClientOrderIdFor(tenants[1].TenantId, brokerOrderId));
        }

        [Fact]
        public async Task AnOpenOrderCanBeCancelled()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var server = ServerFixture.With(Trading);
            await server.InitializeAsync();
            var tool = new LedgerApiClient(server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            var state = ReadState();
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Opening(state, 1_000_000m), cancellationToken);

            await Task.Delay(1_000, cancellationToken);   // the simulated feed prices AAA, so the simulated broker accepts orders for it

            // A limit far below the market: accepted, never filled.
            var (orderId, brokerOrderId) = (Guid.NewGuid(), Guid.NewGuid());
            var now = DateTimeOffset.UtcNow;
            await tool.ApplyAsync(tenantId, new LedgerScript()
                .Insert("Order", new OrderDto { OrderId = orderId, AssetId = Joe10Account.AssetIdOf("AAA"), Side = OrderSide.Buy, Origin = OrderOrigin.Manual, EnteredTime = now })
                .Insert("Allocation", new AllocationDto { OrderId = orderId, AccountId = Joe10Account.AccountId, Quantity = 10m })
                .Insert("BrokerOrder", new BrokerOrderDto { BrokerOrderId = brokerOrderId, OrderId = orderId, Broker = EventSource.Simulator, Quantity = 10m, OrderType = OrderType.Limit, LimitPrice = 0.01m, TimeInForce = TimeInForce.GoodTillCancelled, EnteredTime = now }), cancellationToken);
            await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Orders.SingleOrDefault()?.BrokerOrders.Single().Status == "Working");

            await tool.InsertAsync(tenantId, "CancelRequest", new CancelRequestDto { CancelRequestId = Guid.NewGuid(), BrokerOrderId = brokerOrderId, EnteredTime = DateTimeOffset.UtcNow, Reason = "Test." }, cancellationToken);
            var orders = await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Orders.Single().BrokerOrders.Single().Status == "Cancelled");
            var order = orders.Orders.Single();
            Assert.Equal(("New", 0m, 10m), (order.Status, order.Placed, order.Unplaced));   // back to unplaced: nothing filled, nothing working
        }

        [Fact]
        public async Task AnOrderTheBrokerAlreadyHasIsRecordedNotSentAgain()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var broker = new RememberingBroker();
            await using var server = ServerFixture.With(Trading, services => services.AddSingleton<IBroker>(broker));
            await server.InitializeAsync();
            var tool = new LedgerApiClient(server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            var state = ReadState();
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Opening(state, 1_000_000m), cancellationToken);

            // As after a restart: the broker filled the order, and our ledger never heard back.
            var (orderId, known, fresh) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            broker.Already[known] = [new BrokerReport("schwab-1", ExecutionReportType.Accepted, null, null, null, DateTimeOffset.UtcNow, null), new BrokerReport("schwab-2", ExecutionReportType.Fill, 10m, 120m, 0.5m, DateTimeOffset.UtcNow, null)];
            var now = DateTimeOffset.UtcNow;
            await tool.ApplyAsync(tenantId, new LedgerScript()
                .Insert("Order", new OrderDto { OrderId = orderId, AssetId = Joe10Account.AssetIdOf("AAA"), Side = OrderSide.Buy, Origin = OrderOrigin.Manual, EnteredTime = now })
                .Insert("Allocation", new AllocationDto { OrderId = orderId, AccountId = Joe10Account.AccountId, Quantity = 20m })
                .Insert("BrokerOrder", new BrokerOrderDto { BrokerOrderId = known, OrderId = orderId, Broker = EventSource.Schwab, Quantity = 10m, OrderType = OrderType.Market, TimeInForce = TimeInForce.Day, EnteredTime = now })
                .Insert("BrokerOrder", new BrokerOrderDto { BrokerOrderId = fresh, OrderId = orderId, Broker = EventSource.Schwab, Quantity = 10m, OrderType = OrderType.Market, TimeInForce = TimeInForce.Day, EnteredTime = now }), cancellationToken);

            var orders = await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Orders.SingleOrDefault()?.Status == "Filled");
            Assert.Equal([fresh], broker.Placed);   // the order the broker had was recorded, not sent again
            Assert.Equal((20m, 1m), (orders.Orders.Single().Filled, orders.Orders.Single().Fees));
            var holdings = await WaitForAsync(() => tool.GetHoldingsAsync(tenantId, cancellationToken), found => found.Holdings.Any(holding => holding.AssetId == Joe10Account.AssetIdOf("AAA") && holding.Quantity == 20m));
            Assert.Equal(2_401m, holdings.Holdings.Single(holding => holding.AssetId == Joe10Account.AssetIdOf("AAA")).CostBasis);   // 10 at $120 and 10 at $120, plus $1 in fees
        }

        private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> condition)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (true)
            {
                try
                {
                    var found = await read();
                    if (condition(found))
                    {
                        return found;
                    }
                }
                catch (LedgerApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    // The rules haven't run for the tenant yet.
                }

                Assert.True(DateTimeOffset.UtcNow < deadline, "The server did not get there within 20 seconds.");
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }

        private static Joe10State ReadState()
        {
            var path = Path.Combine(Path.GetTempPath(), $"joe10-state-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, StateJson);
            try
            {
                return Joe10State.Read(path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// A broker that already has some orders (as after a restart) and fills anything new at $120, $0.50 in fees.
        /// </summary>
        private sealed class RememberingBroker : IBroker
        {
            public ConcurrentDictionary<Guid, IReadOnlyList<BrokerReport>> Already { get; } = new ConcurrentDictionary<Guid, IReadOnlyList<BrokerReport>>();

            public List<Guid> Placed { get; } = new List<Guid>();

            public EventSource Source => EventSource.Schwab;

            public Task<IReadOnlyList<BrokerReport>> PlaceAsync(BrokerOrderRequest order, CancellationToken cancellationToken)
            {
                lock (this.Placed)
                {
                    this.Placed.Add(order.BrokerOrderId);
                }

                var reports = new[] { new BrokerReport($"new-{order.BrokerOrderId:N}-0", ExecutionReportType.Accepted, null, null, null, DateTimeOffset.UtcNow, null), new BrokerReport($"new-{order.BrokerOrderId:N}-1", ExecutionReportType.Fill, order.Quantity, 120m, 0.5m, DateTimeOffset.UtcNow, null) };
                this.Already[order.BrokerOrderId] = reports;
                return Task.FromResult<IReadOnlyList<BrokerReport>>(reports);
            }

            public Task<IReadOnlyList<BrokerReport>> CancelAsync(BrokerOrderRequest order, CancellationToken cancellationToken) => this.GetReportsAsync(order, cancellationToken);

            public Task<IReadOnlyList<BrokerReport>> GetReportsAsync(BrokerOrderRequest order, CancellationToken cancellationToken) =>
                Task.FromResult(this.Already.TryGetValue(order.BrokerOrderId, out var reports) ? reports : (IReadOnlyList<BrokerReport>)Array.Empty<BrokerReport>());
        }
    }
}
