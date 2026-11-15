// <copyright file="EventTransactions.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Events
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using AtlasLedger.Server.Holdings;

    /// <summary>
    /// What became of a transaction written to a tenant's log, before it is durable.
    /// </summary>
    /// <param name="Result">The answer to send, once <paramref name="Sequence"/> is durable.</param>
    /// <param name="IsConflict">Whether it was refused because its key, or one of its events, was already used for something else.</param>
    public sealed record TransactionOutcome(TransactionResult Result, bool IsConflict = false)
    {
        /// <summary>Gets the sequence to wait on before answering: the commit, or for a transaction already committed, its last event.</summary>
        public long? Sequence => this.Result.Sequence;
    }

    /// <summary>
    /// Writes whole transactions to a tenant's event log, and reads them back.  A transaction is checked in full under the
    /// tenant's lock, then its events are written in one ledger transaction: every event, or none.
    /// </summary>
    public static class EventTransactions
    {
        /// <summary>
        /// Checks a transaction and, if it holds up, commits it.  The caller waits until the sequence is durable before
        /// answering.
        /// </summary>
        /// <param name="partition">The tenant.</param>
        /// <param name="transaction">The transaction.</param>
        /// <returns>What became of it.</returns>
        public static TransactionOutcome Write(LedgerPartition partition, TransactionMessage transaction)
        {
            ArgumentNullException.ThrowIfNull(partition);
            ArgumentNullException.ThrowIfNull(transaction);
            var id = transaction.TransactionId;
            var messages = transaction.Events ?? [];
            if (messages.Count == 0)
            {
                return Refused(id, "A transaction holds at least one event.");
            }

            if (messages.Count > EventMessages.MaximumEvents)
            {
                return Refused(id, $"A transaction holds at most {EventMessages.MaximumEvents:N0} events; this one holds {messages.Count:N0}. Split it.");
            }

            using var ledgerTransaction = partition.BeginTransaction();

            // Read every event, and make the rows they'd be stored as.
            var problems = new List<string>();
            var rows = new List<Event>(messages.Count);
            var events = new List<LedgerEvent>(messages.Count);
            for (var index = 0; index < messages.Count; index++)
            {
                var message = messages[index];
                var name = $"Event {index + 1} ({message.EventId})";
                if (string.IsNullOrWhiteSpace(message.SourceId) || message.SourceId.Length > 128)
                {
                    problems.Add($"{name}: its sourceId must be 1 to 128 characters.");
                    continue;
                }

                LedgerEvent value;
                try
                {
                    value = EventMessages.Read(message);
                }
                catch (FormatException exception)
                {
                    problems.Add($"{name}: {exception.Message}");
                    continue;
                }

                events.Add(value);
                rows.Add(new Event
                {
                    EventId = message.EventId,
                    TransactionId = id,
                    EventType = message.EventType,
                    SchemaVersion = message.SchemaVersion,
                    Source = message.Source,
                    SourceId = message.SourceId,
                    SourceSequence = message.SourceSequence,
                    EffectiveTime = message.EffectiveTime,
                    EnteredTime = message.EnteredTime ?? message.EffectiveTime,
                    CorrelationId = message.CorrelationId,
                    CausationId = message.CausationId,
                    Payload = EventCodec.Encode(value),
                });
            }

            if (problems.Count > 0)
            {
                return Refused(id, problems);
            }

            foreach (var duplicate in rows.GroupBy(row => row.EventId).Where(group => group.Count() > 1))
            {
                problems.Add($"Event {duplicate.Key} appears {duplicate.Count()} times.");
            }

            foreach (var duplicate in rows.GroupBy(row => (row.Source, row.SourceId)).Where(group => group.Count() > 1))
            {
                problems.Add($"Source {duplicate.Key.Source} {duplicate.Key.SourceId} appears {duplicate.Count()} times.");
            }

            if (problems.Count > 0)
            {
                return Refused(id, problems);
            }

            // Sent before?  The same transaction again is a no-op; anything else that reuses its key or its events is a conflict.
            var stored = rows.Select(row => partition.FindEvent(row.EventId) ?? partition.FindEventByBySource(row.Source, row.SourceId)).ToArray();
            if (stored.Any(row => row is not null))
            {
                var conflicts = new List<string>();
                for (var index = 0; index < rows.Count; index++)
                {
                    if (stored[index] is not { } existing)
                    {
                        conflicts.Add($"Event {rows[index].EventId} isn't in the log, but others in this transaction are: transaction {id} was committed with different events.");
                    }
                    else if (TransactionOf(existing) != id)
                    {
                        conflicts.Add($"Event {rows[index].EventId} (source {rows[index].Source} {rows[index].SourceId}) was already committed, in transaction {TransactionOf(existing)}.");
                    }
                    else if (!Same(existing, rows[index]))
                    {
                        conflicts.Add($"Event {rows[index].EventId} was committed in this transaction with different contents.");
                    }
                }

                if (conflicts.Count == 0 && partition.Events.Count(row => TransactionOf(row) == id) == rows.Count)
                {
                    return new TransactionOutcome(new TransactionResult(id, TransactionStatus.AlreadyCommitted, stored.Max(row => row!.RowVersion), []));
                }

                return new TransactionOutcome(new TransactionResult(id, TransactionStatus.Refused, null, conflicts.Count > 0 ? conflicts : [$"Transaction {id} was committed with different events."]), IsConflict: true);
            }

            if (partition.Events.Any(row => TransactionOf(row) == id))
            {
                return new TransactionOutcome(new TransactionResult(id, TransactionStatus.Refused, null, [$"Transaction {id} was committed with different events."]), IsConflict: true);
            }

            problems.AddRange(Check(partition, events));
            if (problems.Count > 0)
            {
                return Refused(id, problems);
            }

            foreach (var row in rows)
            {
                ledgerTransaction.InsertEvent(row);
            }

            var sequence = ledgerTransaction.Commit();
            return new TransactionOutcome(new TransactionResult(id, TransactionStatus.Committed, sequence, []));
        }

        /// <summary>
        /// Reads whole transactions from a tenant's log: the events after one sequence, through another, grouped by the
        /// transaction they came in.  An event written without one (by the table API) is a transaction by itself, keyed by
        /// its own ID.
        /// </summary>
        /// <param name="partition">The tenant.</param>
        /// <param name="after">The sequence already read.</param>
        /// <param name="through">The last sequence to read: the durable one, so nothing read can be lost.</param>
        /// <returns>The transactions, in log order.</returns>
        public static IReadOnlyList<TransactionMessage> Read(LedgerPartition partition, long after, long through)
        {
            ArgumentNullException.ThrowIfNull(partition);
            Event[] rows;
            lock (partition.SyncRoot)
            {
                rows = partition.Events.Where(row => row.RowVersion > after && row.RowVersion <= through).OrderBy(row => row.RowVersion).ToArray();
            }

            var transactions = new List<TransactionMessage>();
            var index = 0;
            while (index < rows.Length)
            {
                var id = TransactionOf(rows[index]);
                var end = index + 1;
                while (rows[index].TransactionId is not null && end < rows.Length && rows[end].TransactionId == id)
                {
                    end++;
                }

                transactions.Add(new TransactionMessage
                {
                    TransactionId = id,
                    Events = rows[index..end].Select(Message).ToArray(),
                    Sequence = rows[end - 1].RowVersion,
                });
                index = end;
            }

            return transactions;
        }

        /// <summary>
        /// Makes the travelling form of a stored event.
        /// </summary>
        /// <param name="row">The event's row.</param>
        /// <returns>The message.</returns>
        public static EventMessage Message(Event row)
        {
            ArgumentNullException.ThrowIfNull(row);
            using var payload = JsonDocument.Parse(row.Payload);
            return new EventMessage
            {
                EventId = row.EventId,
                EventType = row.EventType,
                SchemaVersion = row.SchemaVersion,
                Source = row.Source,
                SourceId = row.SourceId,
                SourceSequence = row.SourceSequence,
                EffectiveTime = row.EffectiveTime,
                EnteredTime = row.EnteredTime == row.EffectiveTime ? null : row.EnteredTime,
                CorrelationId = row.CorrelationId,
                CausationId = row.CausationId,
                Sequence = row.RowVersion,
                Payload = payload.RootElement.Clone(),
            };
        }

        private static Guid TransactionOf(Event row) => row.TransactionId ?? row.EventId;

        private static bool Same(Event stored, Event sent) =>
            stored.EventType == sent.EventType
            && stored.SchemaVersion == sent.SchemaVersion
            && stored.Source == sent.Source
            && stored.SourceId == sent.SourceId
            && stored.SourceSequence == sent.SourceSequence
            && stored.EffectiveTime == sent.EffectiveTime
            && stored.EnteredTime == sent.EnteredTime
            && stored.CorrelationId == sent.CorrelationId
            && stored.CausationId == sent.CausationId
            && stored.Payload == sent.Payload;

        private static TransactionOutcome Refused(Guid id, params IReadOnlyList<string> problems) =>
            new(new TransactionResult(id, TransactionStatus.Refused, null, problems));

        /// <summary>
        /// Checks that what the events name exists: in the tenant, in the log, or earlier in the same transaction.
        /// </summary>
        private static List<string> Check(LedgerPartition partition, IReadOnlyList<LedgerEvent> events)
        {
            var problems = new List<string>();
            var ids = events.Select(item => item.EventId).ToHashSet();
            void Account(LedgerEvent item, Guid accountId, string role)
            {
                if (partition.FindAccount(accountId) is null)
                {
                    problems.Add($"Event {item.EventId}: {role} {accountId} isn't one of the tenant's accounts.");
                }
            }

            void Asset(LedgerEvent item, Guid assetId, string role)
            {
                if (partition.FindAsset(assetId) is null)
                {
                    problems.Add($"Event {item.EventId}: {role} {assetId} isn't one of the tenant's assets.");
                }
            }

            foreach (var activity in events.OfType<AccountActivityEvent>())
            {
                Account(activity, activity.AccountId, "account");
                foreach (var assetId in activity.Items.Select(item => item.AssetId).Distinct())
                {
                    Asset(activity, assetId, "asset");
                }

                if (activity.MustBalance && !HoldingFold.IsBalanced(activity.Items))
                {
                    problems.Add($"Event {activity.EventId}: the {activity.Envelope.Type} doesn't balance: its items' amounts must sum to zero (they sum to {activity.Items.Sum(item => item.Amount ?? 0m)}).");
                }

                switch (activity)
                {
                    case JournalEvent journal:
                        Account(journal, journal.ToAccountId, "the account journaled to,");
                        break;
                    case IncomeEvent { PaidByAssetId: { } payer } income:
                        Asset(income, payer, "the asset that paid it,");
                        break;
                    case CorrectionEvent correction when !ids.Contains(correction.Corrects) && partition.FindEvent(correction.Corrects) is null:
                        problems.Add($"Event {correction.EventId}: it corrects event {correction.Corrects}, which isn't in the log.");
                        break;
                }
            }

            var orderEvents = events.OfType<OrderEvent>().ToArray();
            if (orderEvents.Length > 0)
            {
                // What the log already says about orders, read only when the transaction has order events.
                var logged = partition.Events
                    .Where(row => row.EventType is EventType.OrderPlaced or EventType.OrderReplaced or EventType.BrokerOrderSent)
                    .Select(EventRows.Read)
                    .ToArray();
                var orders = logged.OfType<OrderPlacedEvent>().ToDictionary(order => order.OrderId);
                var sent = logged.OfType<BrokerOrderSentEvent>().ToDictionary(item => item.BrokerOrderId);
                var linked = new List<OrderPlacedEvent>();
                foreach (var item in orderEvents)
                {
                    switch (item)
                    {
                        case OrderPlacedEvent order:
                            if (!orders.TryAdd(order.OrderId, order))
                            {
                                problems.Add($"Event {order.EventId}: order {order.OrderId} was already placed.");
                            }

                            if (order is OrderReplacedEvent replacement && !orders.ContainsKey(replacement.Replaces))
                            {
                                problems.Add($"Event {order.EventId}: it replaces order {replacement.Replaces}, which wasn't placed.");
                            }

                            foreach (var leg in order.Legs)
                            {
                                Asset(order, leg.AssetId, $"leg {leg.LegId}'s asset");
                                foreach (var allocation in leg.Allocations)
                                {
                                    Account(order, allocation.AccountId, $"leg {leg.LegId}'s account");
                                }
                            }

                            foreach (var duplicate in order.Legs.GroupBy(leg => leg.LegId).Where(group => group.Count() > 1))
                            {
                                problems.Add($"Event {order.EventId}: leg {duplicate.Key} appears {duplicate.Count()} times.");
                            }

                            if (order.Link is not null)
                            {
                                linked.Add(order);
                            }

                            break;
                        case BrokerOrderSentEvent brokerOrder:
                            if (!orders.TryGetValue(brokerOrder.OrderId, out var parent))
                            {
                                problems.Add($"Event {brokerOrder.EventId}: order {brokerOrder.OrderId} wasn't placed.");
                                break;
                            }

                            if (!sent.TryAdd(brokerOrder.BrokerOrderId, brokerOrder))
                            {
                                problems.Add($"Event {brokerOrder.EventId}: broker order {brokerOrder.BrokerOrderId} was already sent.");
                            }

                            if (!parent.Legs.Any(leg => leg.Allocations.Any(allocation => allocation.AccountId == brokerOrder.AccountId)))
                            {
                                problems.Add($"Event {brokerOrder.EventId}: account {brokerOrder.AccountId} has no allocation in order {brokerOrder.OrderId}.");
                            }

                            foreach (var leg in brokerOrder.Legs.Where(leg => parent.Legs.All(orderLeg => orderLeg.LegId != leg.LegId)))
                            {
                                problems.Add($"Event {brokerOrder.EventId}: order {brokerOrder.OrderId} has no leg {leg.LegId}.");
                            }

                            break;
                        case BrokerAnswerEvent answer:
                            if (!sent.TryGetValue(answer.BrokerOrderId, out var answered) || answered.OrderId != answer.OrderId)
                            {
                                problems.Add($"Event {answer.EventId}: broker order {answer.BrokerOrderId} wasn't sent for order {answer.OrderId}.");
                            }

                            break;
                        case CancelRequestedEvent cancel:
                            if (!orders.ContainsKey(cancel.OrderId))
                            {
                                problems.Add($"Event {cancel.EventId}: order {cancel.OrderId} wasn't placed.");
                            }
                            else if (cancel.BrokerOrderId is { } brokerOrderId && !(sent.TryGetValue(brokerOrderId, out var cancelled) && cancelled.OrderId == cancel.OrderId))
                            {
                                problems.Add($"Event {cancel.EventId}: broker order {brokerOrderId} wasn't sent for order {cancel.OrderId}.");
                            }

                            break;
                    }
                }

                // A link may name an order placed later in the same transaction, so links are checked last.
                foreach (var order in linked)
                {
                    foreach (var other in order.Link!.OrderIds.Where(other => !orders.ContainsKey(other)))
                    {
                        problems.Add($"Event {order.EventId}: its {order.Link.Type} link names order {other}, which wasn't placed.");
                    }
                }
            }

            return problems;
        }
    }
}
