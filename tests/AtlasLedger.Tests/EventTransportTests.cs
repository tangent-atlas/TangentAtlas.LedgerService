// <copyright file="EventTransportTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias client;

namespace AtlasLedger.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using client::AtlasLedger.Client;
    using client::AtlasLedger.Client.Api;
    using client::AtlasLedger.Client.Scripts;
    using client::AtlasLedger.Client.TestData;
    using client::AtlasLedger.Model;
    using Xunit;

    /// <summary>
    /// The events endpoint: transactions in, as JSON or JSON Lines, each committed or refused as a unit; transactions out,
    /// as JSON Lines, from any sequence, following the log.
    /// </summary>
    public sealed class EventTransportTests : IClassFixture<ServerFixture>
    {
        private static readonly Joe10State State = Joe10State.Generic(new DateTimeOffset(2026, 11, 11, 1, 30, 0, TimeSpan.Zero));

        private readonly ServerFixture server;

        public EventTransportTests(ServerFixture server)
        {
            this.server = server;
        }

        [Fact]
        public async Task ATransactionCommitsOnceAndSendingItAgainChangesNothing()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var (tool, tenantId) = await this.TenantAsync(cancellationToken);
            var deposit = Joe10Account.Opening(State, 1_000m).Single();

            var first = await tool.SendTransactionAsync(tenantId, deposit, cancellationToken);
            var again = await tool.SendTransactionAsync(tenantId, deposit, cancellationToken);

            Assert.Equal(TransactionStatus.Committed, first.Status);
            Assert.Equal(TransactionStatus.AlreadyCommitted, again.Status);
            Assert.Equal(first.Sequence, again.Sequence);
            var stored = Assert.Single(await tool.ListAsync<EventDto>(tenantId, "Event", cancellationToken));
            Assert.Equal(deposit.TransactionId, stored.TransactionId);
        }

        [Fact]
        public async Task ReusingATransactionsKeyOrAnEventForSomethingElseIsAConflict()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var (tool, tenantId) = await this.TenantAsync(cancellationToken);
            var deposit = Joe10Account.Opening(State, 1_000m).Single();
            await tool.SendTransactionAsync(tenantId, deposit, cancellationToken);

            // The same key, a different event.
            var other = Deposit(2_000m, "other-deposit") with { TransactionId = deposit.TransactionId };
            var reused = await tool.SendTransactionAsync(tenantId, other, cancellationToken);
            Assert.Equal(TransactionStatus.Refused, reused.Status);
            Assert.Contains("different events", Assert.Single(reused.Problems));

            // The same event, in a new transaction.
            var moved = deposit with { TransactionId = Guid.NewGuid() };
            var refused = await tool.SendTransactionAsync(tenantId, moved, cancellationToken);
            Assert.Equal(TransactionStatus.Refused, refused.Status);
            Assert.Contains("already committed", Assert.Single(refused.Problems));

            // The same key and event, different contents.
            var changed = deposit with { Events = [deposit.Events[0] with { EnteredTime = deposit.Events[0].EffectiveTime.AddDays(1) }] };
            Assert.Contains("different contents", Assert.Single((await tool.SendTransactionAsync(tenantId, changed, cancellationToken)).Problems));

            Assert.Single(await tool.ListAsync<EventDto>(tenantId, "Event", cancellationToken));
        }

        [Fact]
        public async Task ATransactionWithAnyProblemIsRefusedWholeWithEveryProblem()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var (tool, tenantId) = await this.TenantAsync(cancellationToken);
            var unknownAccount = Guid.NewGuid();
            var unknownAsset = Guid.NewGuid();
            var good = Deposit(1_000m, "good").Events[0];
            var unbalanced = Message(new TradeEvent
            {
                AccountId = Joe10Account.AccountId,
                TradeDate = new DateOnly(2026, 11, 10),
                Items = [new ActivityItem(Joe10Account.AssetIdOf("AAPL"), 1m, 100m), new ActivityItem(Joe10Account.DollarId, -90m, -90m)],
            }, "unbalanced");
            var stranger = Message(new CashMovementEvent { AccountId = unknownAccount, TradeDate = new DateOnly(2026, 11, 10), Method = CashMethod.Ach, Items = [new ActivityItem(unknownAsset, 1m, 1m)] }, "stranger");
            var correction = Message(new CorrectionEvent { AccountId = Joe10Account.AccountId, TradeDate = new DateOnly(2026, 11, 10), Corrects = Guid.NewGuid(), Items = [] }, "correction");
            var unreadable = good with { EventId = Guid.NewGuid(), SourceId = "unreadable", Payload = System.Text.Json.JsonDocument.Parse("""{ "accountId": "not a guid" }""").RootElement.Clone() };
            var transaction = new TransactionMessage { TransactionId = Guid.NewGuid(), Events = [good, unbalanced, stranger, correction] };

            var result = await tool.SendTransactionAsync(tenantId, transaction, cancellationToken);

            Assert.Equal(TransactionStatus.Refused, result.Status);
            Assert.Null(result.Sequence);
            Assert.Collection(
                result.Problems,
                problem => Assert.Contains("doesn't balance", problem),
                problem => Assert.Contains($"account {unknownAccount}", problem),
                problem => Assert.Contains($"asset {unknownAsset}", problem),
                problem => Assert.Contains("isn't in the log", problem));
            Assert.Empty(await tool.ListAsync<EventDto>(tenantId, "Event", cancellationToken));   // not even the good one

            var unread = await tool.SendTransactionAsync(tenantId, transaction with { Events = [good, unreadable] }, cancellationToken);
            Assert.Contains("Event 2", Assert.Single(unread.Problems));
            Assert.Empty(await tool.ListAsync<EventDto>(tenantId, "Event", cancellationToken));
        }

        [Fact]
        public async Task TheTransactionsSizeIsLimited()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var (tool, tenantId) = await this.TenantAsync(cancellationToken);
            var events = Enumerable.Range(0, EventMessages.MaximumEvents + 1).Select(index => Deposit(1m, $"small-{index}").Events[0]).ToArray();

            var tooMany = await tool.SendTransactionAsync(tenantId, new TransactionMessage { TransactionId = Guid.NewGuid(), Events = events }, cancellationToken);
            var none = await tool.SendTransactionAsync(tenantId, new TransactionMessage { TransactionId = Guid.NewGuid(), Events = [] }, cancellationToken);
            var most = await tool.SendTransactionAsync(tenantId, new TransactionMessage { TransactionId = Guid.NewGuid(), Events = events[..EventMessages.MaximumEvents] }, cancellationToken);

            Assert.Contains("at most 1,000", Assert.Single(tooMany.Problems));
            Assert.Contains("at least one", Assert.Single(none.Problems));
            Assert.Equal(TransactionStatus.Committed, most.Status);
        }

        [Fact]
        public async Task AnEventMayNameWhatAnEarlierEventInTheSameTransactionCreated()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var (tool, tenantId) = await this.TenantAsync(cancellationToken);
            var orderId = Guid.NewGuid();
            var brokerOrderId = Guid.NewGuid();
            var asset = Joe10Account.AssetIdOf("AAPL");
            var placed = Message(new OrderPlacedEvent { OrderId = orderId, Origin = OrderOrigin.Manual, Legs = [new OrderLeg(1, asset, Instruction.Buy, [new AllocationItem(Joe10Account.AccountId, 5m)])] }, "placed", orderId);
            var sent = Message(new BrokerOrderSentEvent { OrderId = orderId, BrokerOrderId = brokerOrderId, AccountId = Joe10Account.AccountId, Legs = [new BrokerLeg(1, 5m)], OrderType = OrderType.Market, TimeInForce = TimeInForce.Day }, "sent", brokerOrderId);
            var strayFill = Message(new ExecutionReportedEvent { OrderId = orderId, BrokerOrderId = Guid.NewGuid(), ReportTime = DateTimeOffset.UtcNow, ExecutionId = "x", Quantity = 5m, Price = 1m }, "stray");
            var wrongLeg = Message(new BrokerOrderSentEvent { OrderId = orderId, BrokerOrderId = Guid.NewGuid(), AccountId = Joe10Account.AccountId, Legs = [new BrokerLeg(2, 5m)], OrderType = OrderType.Market, TimeInForce = TimeInForce.Day }, "wrong-leg");

            var sentFirst = await tool.SendTransactionAsync(tenantId, new TransactionMessage { TransactionId = Guid.NewGuid(), Events = [sent, placed] }, cancellationToken);
            var inOrder = await tool.SendTransactionAsync(tenantId, new TransactionMessage { TransactionId = Guid.NewGuid(), Events = [placed, sent] }, cancellationToken);
            var refused = await tool.SendTransactionAsync(tenantId, new TransactionMessage { TransactionId = Guid.NewGuid(), Events = [strayFill, wrongLeg] }, cancellationToken);

            Assert.Contains($"order {orderId} wasn't placed", Assert.Single(sentFirst.Problems));   // in order: a send can't come before its order
            Assert.Equal(TransactionStatus.Committed, inOrder.Status);
            Assert.Collection(
                refused.Problems,
                problem => Assert.Contains("wasn't sent", problem),
                problem => Assert.Contains("has no leg 2", problem));
        }

        [Fact]
        public async Task AStreamCommitsEachLineByItselfAndAnswersEachLine()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var (tool, tenantId) = await this.TenantAsync(cancellationToken);
            var first = Deposit(1m, "line-1");
            var bad = Deposit(1m, "line-2") with { Events = [Deposit(1m, "line-2").Events[0] with { SourceId = string.Empty }] };
            var third = Deposit(1m, "line-3");
            var body = $"{EventMessages.WriteLine(first)}\n\n{EventMessages.WriteLine(bad)}\nnot json\n{EventMessages.WriteLine(third)}\n";

            using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/tenants/{tenantId}/events") { Content = new StringContent(body, Encoding.UTF8, EventMessages.StreamMediaType) };
            Ledger.AddContractHeaders(request.Headers);
            request.Headers.Add(DevelopmentCallers.Header, DevelopmentCallers.Tool);
            using var response = await this.server.HttpClient.SendAsync(request, cancellationToken);
            var lines = (await response.Content.ReadAsStringAsync(cancellationToken)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => System.Text.Json.JsonSerializer.Deserialize<TransactionResult>(line, EventMessages.Options)!)
                .ToArray();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(EventMessages.StreamMediaType, response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(
                [(1, TransactionStatus.Committed), (3, TransactionStatus.Refused), (4, TransactionStatus.Refused), (5, TransactionStatus.Committed)],
                lines.Select(line => (line.Line!.Value, line.Status)));
            Assert.Null(lines[2].TransactionId);   // not even its key could be read
            Assert.Equal(2, (await tool.ListAsync<EventDto>(tenantId, "Event", cancellationToken)).Count);

            // The client sends in streams, and says which were refused.
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.CommitTransactionsAsync(tenantId, [first, bad, third], cancellationToken));
            Assert.Contains(bad.TransactionId.ToString(), error.Message);
        }

        [Fact]
        public async Task TheLogReadsBackAsTheTransactionsThatWroteItAndLoadsAgainUnchanged()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var (tool, tenantId) = await this.TenantAsync(cancellationToken);
            var trading = Joe10Account.Trading(State, 1_000_000m);
            await tool.CommitTransactionsAsync(tenantId, trading, cancellationToken);

            // One written alone, through the table API: a transaction by itself.
            var loose = Guid.NewGuid();
            await tool.ApplyAsync(tenantId, new LedgerScript().AppendEvent(new CashMovementEvent { AccountId = Joe10Account.AccountId, TradeDate = new DateOnly(2026, 11, 10), Method = CashMethod.Wire, Items = [new ActivityItem(Joe10Account.DollarId, 5m, 5m)] }, loose, EventSource.Operator, "loose", DateTimeOffset.UtcNow), cancellationToken);

            var read = await ReadAllAsync(tool, tenantId, 0, cancellationToken);

            Assert.Equal(trading.Select(transaction => transaction.TransactionId).Append(loose), read.Select(transaction => transaction.TransactionId));
            Assert.Equal(trading.Select(transaction => transaction.Events.Select(item => item.EventId)).Append([loose]), read.Select(transaction => transaction.Events.Select(item => item.EventId)));
            var sequences = read.SelectMany(transaction => transaction.Events.Select(item => item.Sequence!.Value)).ToArray();
            Assert.Equal(sequences.Order(), sequences);
            Assert.All(read, transaction => Assert.Equal(transaction.Events[^1].Sequence, transaction.Sequence));
            Assert.Equal(
                EventMessages.WriteLine(trading[1] with { Sequence = null }),
                EventMessages.WriteLine(read[1] with { Sequence = null, Events = read[1].Events.Select(item => item with { Sequence = null }).ToArray() }));   // byte for byte

            // After a sequence: only what came later.
            var later = await ReadAllAsync(tool, tenantId, read[^2].Sequence!.Value, cancellationToken);
            Assert.Equal([loose], later.Select(transaction => transaction.TransactionId));

            // What was read loads again, as a no-op.
            var again = await tool.CommitTransactionsAsync(tenantId, read, cancellationToken);
            Assert.All(again, result => Assert.Equal(TransactionStatus.AlreadyCommitted, result.Status));
        }

        [Fact]
        public async Task FollowingTheLogSendsEachTransactionAsItBecomesDurable()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var (tool, tenantId) = await this.TenantAsync(cancellationToken);
            var first = Deposit(1m, "follow-1");
            var second = Deposit(2m, "follow-2");
            await tool.SendTransactionAsync(tenantId, first, cancellationToken);

            using var following = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            following.CancelAfter(TimeSpan.FromSeconds(20));
            var received = new List<Guid>();
            var reading = Task.Run(
                async () =>
                {
                    await foreach (var transaction in tool.ReadTransactionsAsync(tenantId, 0, follow: true, following.Token))
                    {
                        received.Add(transaction.TransactionId);
                        if (received.Count == 2)
                        {
                            return;
                        }
                    }
                },
                cancellationToken);
            await Task.Delay(300, cancellationToken);
            await tool.SendTransactionAsync(tenantId, second, cancellationToken);
            await reading;

            Assert.Equal([first.TransactionId, second.TransactionId], received);
        }

        [Fact]
        public async Task ATransactionScriptRoundTripsAndTheLoaderTellsItFromATableScript()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var (tool, tenantId) = await this.TenantAsync(cancellationToken);
            var history = Joe10Account.History(State, 1_000_000m);
            var text = EventMessages.WriteScript(history);

            Assert.True(EventMessages.IsTransactionScript(text));
            Assert.False(EventMessages.IsTransactionScript("""{ "operations": [] }"""));
            Assert.Equal(text, EventMessages.WriteScript(EventMessages.ReadScript(text)));
            Assert.Contains("\"payload\": {", text);   // nested, not a string
            Assert.Contains("\"eventType\": \"Trade\"", text);

            var path = Path.Combine(Path.GetTempPath(), $"joe10-history-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, text);
            try
            {
                using var output = new StringWriter();
                await Commands.LoadAsync(tool, tenantId, path, output, cancellationToken);
                await Commands.LoadAsync(tool, tenantId, path, output, cancellationToken);
                Assert.Equal(
                    $"Loaded 11 transaction(s) (11 event(s)) from {path} into {tenantId}: 11 committed, 0 already committed.{Environment.NewLine}Loaded 11 transaction(s) (11 event(s)) from {path} into {tenantId}: 0 committed, 11 already committed.{Environment.NewLine}",
                    output.ToString());
            }
            finally
            {
                File.Delete(path);
            }
        }

        internal static async Task<IReadOnlyList<TransactionMessage>> ReadAllAsync(LedgerApiClient tool, Guid tenantId, long after, CancellationToken cancellationToken)
        {
            var read = new List<TransactionMessage>();
            await foreach (var transaction in tool.ReadTransactionsAsync(tenantId, after, follow: false, cancellationToken))
            {
                read.Add(transaction);
            }

            return read;
        }

        private static TransactionMessage Deposit(decimal amount, string sourceId) =>
            new()
            {
                TransactionId = Guid.NewGuid(),
                Events = [Message(new CashMovementEvent { AccountId = Joe10Account.AccountId, TradeDate = new DateOnly(2026, 11, 10), Method = CashMethod.Ach, Items = [new ActivityItem(Joe10Account.DollarId, amount, amount)] }, sourceId)],
            };

        private static EventMessage Message(LedgerEvent value, string sourceId, Guid? eventId = null) =>
            EventMessages.From(value, eventId ?? Guid.NewGuid(), EventSource.Operator, sourceId, new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.Zero));

        private async Task<(LedgerApiClient Tool, Guid TenantId)> TenantAsync(CancellationToken cancellationToken)
        {
            var tool = new LedgerApiClient(this.server.HttpClient, DevelopmentCallers.Tool);
            var tenantId = Guid.NewGuid();
            await tool.ApplyAsync(tenantId, Joe10Account.Reference(State), cancellationToken);
            return (tool, tenantId);
        }
    }
}
