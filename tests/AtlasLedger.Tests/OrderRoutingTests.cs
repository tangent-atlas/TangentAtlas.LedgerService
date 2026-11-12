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
        public async Task TheGeneratorOrdersWhatTheModelProposesAndNothingTwice()
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

            // The rules propose buying both companies with half the account, continuously; nothing is ordered yet.
            var proposed = await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Proposals.SingleOrDefault() is { IsWaiting: false, Trades.Count: 2 });
            Assert.All(proposed.Proposals.Single().Trades, trade => Assert.Equal(OrderSide.Buy, trade.Side));
            Assert.Empty(proposed.Orders);

            // The generator writes them as orders, one broker order per account; the simulated broker fills each, $1 in fees.
            var generated = await tool.GenerateOrdersAsync(tenantId, cancellationToken);
            Assert.Equal((2, 2), (generated.Orders, generated.BrokerOrders));
            var orders = await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Orders.Count == 2 && found.Orders.All(order => order.Status == "Filled"));
            Assert.All(orders.Orders, order => Assert.Equal((OrderSide.Buy, OrderOrigin.Rebalance, 1m), (order.Legs.Single().Side, order.Origin, order.Fees)));
            var holdings = await WaitForAsync(() => tool.GetHoldingsAsync(tenantId, cancellationToken), found => found.Holdings.Count == 3);
            var account = holdings.Accounts.Single();
            Assert.Equal((1_000_000m, "Joe 10", 0.5m), (account.NetContributions, account.ModelName, account.TargetCash!.Value));
            foreach (var holding in holdings.Holdings.Where(holding => holding.AssetType == AssetType.Equity))
            {
                // Bought at the price of the moment, in whole shares: each within a point of its target.
                Assert.InRange(holding.Weight!.Value, holding.TargetWeight!.Value - 0.01m, holding.TargetWeight!.Value + 0.01m);
            }

            // Filled, the account is where its model wants it (any drift since is inside the band): running again orders nothing.
            await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Proposals.Single().Trades.Count == 0);
            Assert.Equal(0, (await tool.GenerateOrdersAsync(tenantId, cancellationToken)).Orders);
            Assert.Equal(2, await OrderCountAsync(tool, tenantId, cancellationToken));
            using var printed = new StringWriter();
            await client::AtlasLedger.Client.Commands.HoldingsAsync(tool, tenantId, printed, cancellationToken);
            Assert.Contains("Paid in 1,000,000.00", printed.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task OrdersWrittenBeforeTheRulesSeeThemAreNotWrittenAgain()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            // Prices every five seconds: still while it checks, so a move can't call for a top-up that looks like a repeat.
            await using var server = ServerFixture.With(["--Services:StartOn=rules,market-feed", "--MarketFeed:TickInterval=00:00:05", "--MarketFeed:BadPrintInterval=1.00:00:00"]);
            await server.InitializeAsync();
            var tool = new LedgerApiClient(server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            var state = ReadState();
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Opening(state, 1_000_000m), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Weights(state), cancellationToken);
            await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Proposals.SingleOrDefault() is { IsWaiting: false, Trades.Count: 2 });

            // The broker is off, so the orders stay open; asked again at once, the generator either waits for the rules to
            // see its orders, or sees them and finds nothing more to order.  Either way, two orders in all.
            Assert.Equal(2, (await tool.GenerateOrdersAsync(tenantId, cancellationToken)).Orders);
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    Assert.Equal(0, (await tool.GenerateOrdersAsync(tenantId, cancellationToken)).Orders);
                }
                catch (LedgerApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    // Not seen yet: try again.
                }

                await Task.Delay(50, cancellationToken);
            }

            Assert.Equal(2, await OrderCountAsync(tool, tenantId, cancellationToken));
            Assert.All((await tool.GetOrdersAsync(tenantId, cancellationToken)).Orders, order => Assert.Equal("Working", order.Status));
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
            await tool.ApplyAsync(tenantId, new LedgerScript().Update("Account", new { account.AccountId }, new AccountDto { AccountId = account.AccountId, AccountRole = account.AccountRole, AccountType = account.AccountType, Currency = account.Currency, InstitutionId = account.InstitutionId, ExternalAccountId = account.ExternalAccountId, ModelId = null, RowVersion = account.RowVersion }), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Opening(state, 1_000_000m), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Weights(state), cancellationToken);

            // The weights and the prices are all there; without a model nothing is proposed, so nothing is ordered.
            var holdings = await WaitForAsync(() => tool.GetHoldingsAsync(tenantId, cancellationToken), found => found.Holdings.Count == 1);
            await Task.Delay(1_000, cancellationToken);
            Assert.Empty((await tool.GetOrdersAsync(tenantId, cancellationToken)).Proposals);
            Assert.Equal(0, (await tool.GenerateOrdersAsync(tenantId, cancellationToken)).Orders);
            Assert.Equal((null, null, null), (holdings.Accounts.Single().ModelId, holdings.Accounts.Single().TargetCash, holdings.Accounts.Single().ProposedTrades));
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
                await tool.ApplyAsync(tenantId, ManualOrder(orderId, quantity, brokerOrders: [(brokerOrderId, quantity, OrderType.Market, null)]), cancellationToken);
            }

            // Each fills for its own quantity: the broker saw two orders, not one sent twice.
            foreach (var (tenantId, quantity) in tenants)
            {
                var orders = await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Orders.SingleOrDefault()?.Status == "Filled");
                Assert.Equal(quantity, orders.Orders.Single().Legs.Single().Filled);
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
            await tool.ApplyAsync(tenantId, ManualOrder(orderId, 10m, brokerOrders: [(brokerOrderId, 10m, OrderType.Limit, 0.01m)]), cancellationToken);
            await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Orders.SingleOrDefault()?.BrokerOrders.Single().Status == "Working");

            await tool.ApplyAsync(tenantId, new LedgerScript().AppendEvent(new CancelRequestedEvent { OrderId = orderId, BrokerOrderId = brokerOrderId }, Guid.NewGuid(), EventSource.Operator, $"cancel-{brokerOrderId:N}", DateTimeOffset.UtcNow, correlationId: orderId), cancellationToken);
            var orders = await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Orders.Single().BrokerOrders.Single().Status == "Cancelled");
            var order = orders.Orders.Single();
            Assert.Equal(("New", 0m, 10m), (order.Status, order.Legs.Single().Placed, order.Legs.Single().Unplaced));   // back to unplaced: nothing filled, nothing working
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
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(state, EventSource.Schwab), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Opening(state, 1_000_000m), cancellationToken);

            // As after a restart: the broker filled the order, and our ledger never heard back.
            var (orderId, known, fresh) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            broker.Already[known] = [new BrokerReport("schwab-1", ExecutionReportType.Accepted, null, null, null, DateTimeOffset.UtcNow, null), new BrokerReport("schwab-2", ExecutionReportType.Fill, 10m, 120m, 0.5m, DateTimeOffset.UtcNow, null)];
            await tool.ApplyAsync(tenantId, ManualOrder(orderId, 20m, brokerOrders: [(known, 10m, OrderType.Market, null), (fresh, 10m, OrderType.Market, null)]), cancellationToken);

            var orders = await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Orders.SingleOrDefault()?.Status == "Filled");
            Assert.Equal([fresh], broker.Placed);   // the order the broker had was recorded, not sent again
            Assert.Equal((20m, 1m), (orders.Orders.Single().Legs.Single().Filled, orders.Orders.Single().Fees));
            var holdings = await WaitForAsync(() => tool.GetHoldingsAsync(tenantId, cancellationToken), found => found.Holdings.Any(holding => holding.AssetId == Joe10Account.AssetIdOf("AAA") && holding.Quantity == 20m));
            Assert.Equal(2_401m, holdings.Holdings.Single(holding => holding.AssetId == Joe10Account.AssetIdOf("AAA")).CostBasis);   // 10 at $120 and 10 at $120, plus $1 in fees
        }

        [Fact]
        public async Task AReplacedOrderStopsWorkingAndItsReplacementTrades()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var server = ServerFixture.With(Trading);
            var (tool, tenantId) = await PricedTenantAsync(server, cancellationToken);

            // A limit far below the market works; replaced by a market order for less, it is cancelled and the new one fills.
            var (original, replacement) = (Guid.NewGuid(), Guid.NewGuid());
            await tool.ApplyAsync(tenantId, ManualOrder(original, 10m, brokerOrders: [(Guid.NewGuid(), 10m, OrderType.Limit, 0.01m)]), cancellationToken);
            await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Orders.SingleOrDefault()?.BrokerOrders.Single().Status == "Working");
            await tool.ApplyAsync(tenantId, ManualOrder(replacement, 6m, replaces: original, brokerOrders: [(Guid.NewGuid(), 6m, OrderType.Market, null)]), cancellationToken);

            var orders = await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Orders.Count == 2 && found.Orders.All(order => order.IsFinished));
            var old = orders.Orders.Single(order => order.OrderId == original);
            var now = orders.Orders.Single(order => order.OrderId == replacement);
            Assert.Equal(("Replaced", replacement, "Cancelled"), (old.Status, old.ReplacedBy!.Value, old.BrokerOrders.Single().Status));
            Assert.Equal(("Filled", original, original), (now.Status, now.Replaces!.Value, now.ChainId));
        }

        [Fact]
        public async Task OneCancelsOtherAndOneTriggersOther()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var server = ServerFixture.With(Trading);
            var (tool, tenantId) = await PricedTenantAsync(server, cancellationToken);

            // One cancels other: a market order and a limit far below; the market order fills, so the limit is cancelled.
            var (market, resting) = (Guid.NewGuid(), Guid.NewGuid());
            await tool.ApplyAsync(tenantId, ManualOrder(resting, 5m, brokerOrders: [(Guid.NewGuid(), 5m, OrderType.Limit, 0.01m)]), cancellationToken);
            await tool.ApplyAsync(tenantId, ManualOrder(market, 5m, link: new OrderLink(OrderLinkType.OneCancelsOther, [resting]), brokerOrders: [(Guid.NewGuid(), 5m, OrderType.Market, null)]), cancellationToken);
            var orders = await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Orders.SingleOrDefault(order => order.OrderId == resting)?.BrokerOrders.Single().Status == "Cancelled");
            Assert.Equal("Filled", orders.Orders.Single(order => order.OrderId == market).Status);

            // One triggers other: the child is held while its trigger (a limit far below) works, and is never sent.
            var (trigger, child) = (Guid.NewGuid(), Guid.NewGuid());
            await tool.ApplyAsync(tenantId, ManualOrder(child, 3m, brokerOrders: [(Guid.NewGuid(), 3m, OrderType.Market, null)]), cancellationToken);
            await tool.ApplyAsync(tenantId, ManualOrder(trigger, 3m, link: new OrderLink(OrderLinkType.OneTriggersOther, [child]), brokerOrders: [(Guid.NewGuid(), 3m, OrderType.Limit, 0.01m)]), cancellationToken);
            await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Orders.SingleOrDefault(order => order.OrderId == trigger)?.BrokerOrders.Single().Status == "Working");
            await Task.Delay(1_000, cancellationToken);
            Assert.Equal("Pending", (await tool.GetOrdersAsync(tenantId, cancellationToken)).Orders.Single(order => order.OrderId == child).BrokerOrders.Single().Status);

            // Its trigger cancelled without filling, the child is refused.
            await tool.ApplyAsync(tenantId, new LedgerScript().AppendEvent(new CancelRequestedEvent { OrderId = trigger }, Guid.NewGuid(), EventSource.Operator, $"cancel-{trigger:N}", DateTimeOffset.UtcNow, correlationId: trigger), cancellationToken);
            orders = await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Orders.Single(order => order.OrderId == child).BrokerOrders.Single().Status == "Rejected");
            Assert.Equal("Cancelled", orders.Orders.Single(order => order.OrderId == trigger).Status);

            // With a trigger that fills, the child is sent and fills.
            var (trigger2, child2) = (Guid.NewGuid(), Guid.NewGuid());
            await tool.ApplyAsync(tenantId, ManualOrder(child2, 2m, brokerOrders: [(Guid.NewGuid(), 2m, OrderType.Market, null)]), cancellationToken);
            await tool.ApplyAsync(tenantId, ManualOrder(trigger2, 2m, link: new OrderLink(OrderLinkType.OneTriggersOther, [child2]), brokerOrders: [(Guid.NewGuid(), 2m, OrderType.Market, null)]), cancellationToken);
            await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Orders.SingleOrDefault(order => order.OrderId == child2)?.Status == "Filled");
        }

        [Fact]
        public async Task AnOrderWithTwoLegsTradesBoth()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var server = ServerFixture.With(Trading);
            var (tool, tenantId) = await PricedTenantAsync(server, cancellationToken);

            // Buy 100 AAA and 50 BBB, in one order and one broker order: each leg fills at its own price, the fees on the first.
            var (orderId, brokerOrderId) = (Guid.NewGuid(), Guid.NewGuid());
            var (aaa, bbb) = (Joe10Account.AssetIdOf("AAA"), Joe10Account.AssetIdOf("BBB"));
            var order = new OrderPlacedEvent
            {
                OrderId = orderId,
                Origin = OrderOrigin.Manual,
                Legs =
                [
                    new OrderLeg(1, aaa, Instruction.Buy, [new AllocationItem(Joe10Account.AccountId, 100m)], PositionEffect.Opening),
                    new OrderLeg(2, bbb, Instruction.Buy, [new AllocationItem(Joe10Account.AccountId, 50m)], PositionEffect.Opening),
                ],
            };
            var sent = new BrokerOrderSentEvent { OrderId = orderId, BrokerOrderId = brokerOrderId, AccountId = Joe10Account.AccountId, Legs = [new BrokerLeg(1, 100m), new BrokerLeg(2, 50m)], OrderType = OrderType.Market, TimeInForce = TimeInForce.Day };
            var now = DateTimeOffset.UtcNow;
            await tool.ApplyAsync(
                tenantId,
                new LedgerScript()
                    .AppendEvent(order, orderId, EventSource.Operator, $"order-{orderId:N}", now, correlationId: orderId)
                    .AppendEvent(sent, brokerOrderId, EventSource.Operator, $"sent-{brokerOrderId:N}", now, correlationId: orderId, causationId: orderId),
                cancellationToken);

            var orders = await WaitForAsync(() => tool.GetOrdersAsync(tenantId, cancellationToken), found => found.Orders.SingleOrDefault()?.Status == "Filled");
            Assert.Equal([(1, 100m), (2, 50m)], orders.Orders.Single().BrokerOrders.Single().Legs.Select(leg => (leg.LegId, leg.Filled)));
            var holdings = await WaitForAsync(() => tool.GetHoldingsAsync(tenantId, cancellationToken), found => found.Holdings.Count == 3);
            Assert.Equal((100m, 50m), (holdings.Holdings.Single(holding => holding.AssetId == aaa).Quantity, holdings.Holdings.Single(holding => holding.AssetId == bbb).Quantity));
        }

        /// <summary>
        /// A manual order of AAA for the Joe 10 account, with its broker orders, as events.
        /// </summary>
        private static LedgerScript ManualOrder(Guid orderId, decimal quantity, OrderLink? link = null, Guid? replaces = null, params (Guid BrokerOrderId, decimal Quantity, OrderType Type, decimal? Limit)[] brokerOrders)
        {
            var legs = new[] { new OrderLeg(1, Joe10Account.AssetIdOf("AAA"), Instruction.Buy, [new AllocationItem(Joe10Account.AccountId, quantity)]) };
            OrderPlacedEvent order = replaces is { } replaced
                ? new OrderReplacedEvent { OrderId = orderId, Replaces = replaced, Origin = OrderOrigin.Manual, Legs = legs, Link = link }
                : new OrderPlacedEvent { OrderId = orderId, Origin = OrderOrigin.Manual, Legs = legs, Link = link };
            var now = DateTimeOffset.UtcNow;
            var chain = replaces ?? orderId;
            var script = new LedgerScript().AppendEvent(order, orderId, EventSource.Operator, $"order-{orderId:N}", now, correlationId: chain);
            foreach (var (brokerOrderId, brokerQuantity, type, limit) in brokerOrders)
            {
                var sent = new BrokerOrderSentEvent
                {
                    OrderId = orderId,
                    BrokerOrderId = brokerOrderId,
                    AccountId = Joe10Account.AccountId,
                    Legs = [new BrokerLeg(1, brokerQuantity)],
                    OrderType = type,
                    LimitPrice = limit,
                    TimeInForce = type == OrderType.Limit ? TimeInForce.GoodTillCancelled : TimeInForce.Day,
                };
                script.AppendEvent(sent, brokerOrderId, EventSource.Operator, $"sent-{brokerOrderId:N}", now, correlationId: chain, causationId: orderId);
            }

            return script;
        }

        /// <summary>
        /// A Joe 10 tenant with its deposit, once the simulated feed has priced its assets (so the simulated broker accepts).
        /// </summary>
        private static async Task<(LedgerApiClient Tool, Guid TenantId)> PricedTenantAsync(ServerFixture server, CancellationToken cancellationToken)
        {
            await server.InitializeAsync();
            var tool = new LedgerApiClient(server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            var state = ReadState();
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(state), cancellationToken);
            await tool.ApplyAsync(tenantId, Joe10Account.Opening(state, 1_000_000m), cancellationToken);
            await WaitForAsync(() => tool.GetHoldingsAsync(tenantId, cancellationToken), found => found.Accounts.Count > 0);
            await Task.Delay(1_000, cancellationToken);
            return (tool, tenantId);
        }

        internal static async Task<int> OrderCountAsync(LedgerApiClient tool, Guid tenantId, CancellationToken cancellationToken) =>
            (await tool.ListAsync<EventDto>(tenantId, "Event", cancellationToken)).Count(row => row.EventType is EventType.OrderPlaced or EventType.OrderReplaced);

        internal static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> condition)
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

        internal static Joe10State ReadState()
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

                var reports = new[] { new BrokerReport($"new-{order.BrokerOrderId:N}-0", ExecutionReportType.Accepted, null, null, null, DateTimeOffset.UtcNow, null), new BrokerReport($"new-{order.BrokerOrderId:N}-1", ExecutionReportType.Fill, order.Legs.Single().Quantity, 120m, 0.5m, DateTimeOffset.UtcNow, null) };
                this.Already[order.BrokerOrderId] = reports;
                return Task.FromResult<IReadOnlyList<BrokerReport>>(reports);
            }

            public Task<IReadOnlyList<BrokerReport>> CancelAsync(BrokerOrderRequest order, CancellationToken cancellationToken) => this.GetReportsAsync(order, cancellationToken);

            public Task<IReadOnlyList<BrokerReport>> GetReportsAsync(BrokerOrderRequest order, CancellationToken cancellationToken) =>
                Task.FromResult(this.Already.TryGetValue(order.BrokerOrderId, out var reports) ? reports : (IReadOnlyList<BrokerReport>)Array.Empty<BrokerReport>());
        }
    }
}
