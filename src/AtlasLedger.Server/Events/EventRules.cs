// <copyright file="EventRules.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Events
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using AtlasLedger.Server.Holdings;
    using NRules.Fluent.Dsl;

    /// <summary>
    /// An event the log holds but can't read: an unknown kind or version, or a payload that doesn't read as its kind.
    /// </summary>
    /// <param name="Problem">Why.</param>
    public sealed record UnreadableEvent(string Problem) : LedgerEvent;

    /// <summary>
    /// Reads events from their rows, and writes them as rows.
    /// </summary>
    public static class EventRows
    {
        /// <summary>
        /// Reads an event from its row: its kind's type, with the row's columns as its envelope.
        /// </summary>
        /// <param name="row">The row.</param>
        /// <returns>The event, or an <see cref="UnreadableEvent"/> saying why it can't be read.</returns>
        public static LedgerEvent Read(EventFact row)
        {
            ArgumentNullException.ThrowIfNull(row);
            return Read(new EventEnvelope(row.EventId, row.EventType, row.SchemaVersion, row.RowVersion, row.Source, row.SourceId, row.EffectiveTime, row.EnteredTime, row.SourceSequence, row.CorrelationId, row.CausationId), row.Payload);
        }

        /// <summary>
        /// Reads an event from its stored row.
        /// </summary>
        /// <param name="row">The row.</param>
        /// <returns>The event, or an <see cref="UnreadableEvent"/> saying why it can't be read.</returns>
        public static LedgerEvent Read(Event row)
        {
            ArgumentNullException.ThrowIfNull(row);
            return Read(new EventEnvelope(row.EventId, row.EventType, row.SchemaVersion, row.RowVersion, row.Source, row.SourceId, row.EffectiveTime, row.EnteredTime, row.SourceSequence, row.CorrelationId, row.CausationId), row.Payload);
        }

        /// <summary>
        /// Makes the row that stores an event.
        /// </summary>
        /// <param name="value">The event.</param>
        /// <param name="eventId">Its key.</param>
        /// <param name="source">Where it comes from.</param>
        /// <param name="sourceId">Its identity there.</param>
        /// <param name="time">When it happened (and, unless said otherwise, was recorded).</param>
        /// <param name="correlationId">What it belongs to.</param>
        /// <param name="causationId">What caused it.</param>
        /// <param name="enteredTime">When it was recorded, if later than it happened.</param>
        /// <param name="transactionId">The transaction it is written in.</param>
        /// <returns>The row.</returns>
        public static Event Row(LedgerEvent value, Guid eventId, EventSource source, string sourceId, DateTimeOffset time, Guid? correlationId = null, Guid? causationId = null, DateTimeOffset? enteredTime = null, Guid? transactionId = null) =>
            new Event
            {
                EventId = eventId,
                TransactionId = transactionId,
                EventType = EventCodec.TypeOf(value),
                SchemaVersion = EventCodec.CurrentVersion,
                Source = source,
                SourceId = sourceId,
                EffectiveTime = time,
                EnteredTime = enteredTime ?? time,
                CorrelationId = correlationId,
                CausationId = causationId,
                Payload = EventCodec.Encode(value),
            };

        private static LedgerEvent Read(EventEnvelope envelope, string payload)
        {
            try
            {
                return EventCodec.Decode(envelope, payload);
            }
            catch (FormatException exception)
            {
                return new UnreadableEvent(exception.Message) { Envelope = envelope };
            }
        }
    }

    /// <summary>
    /// Reads each event row into its kind's type: the facts every other rule works from.
    /// </summary>
    [Name("Events.Read")]
    public sealed class ReadEventRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            EventFact row = default!;
            this.When()
                .Match(() => row);
            this.Then()
                .Yield(_ => EventRows.Read(row));
        }
    }

    /// <summary>
    /// An event that can't be read is a problem.
    /// </summary>
    [Name("Events.Unreadable")]
    public sealed class UnreadableEventRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            UnreadableEvent unreadable = default!;
            this.When()
                .Match(() => unreadable);
            this.Then()
                .Yield(_ => new EventProblem(unreadable.EventId, unreadable.Problem));
        }
    }

    /// <summary>
    /// An exchange within the books (a trade) that doesn't balance is a problem: a bug in whatever wrote it.
    /// </summary>
    [Name("Events.Unbalanced")]
    public sealed class UnbalancedEventRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            AccountActivityEvent activity = default!;
            this.When()
                .Match(() => activity, candidate => candidate.MustBalance, candidate => !HoldingFold.IsBalanced(candidate.Items));
            this.Then()
                .Yield(_ => new EventProblem(activity.EventId, $"The {activity.Envelope.Type} doesn't balance: its items' amounts must sum to zero (they sum to {activity.Items.Sum(item => item.Amount ?? 0m)})."));
        }
    }

    /// <summary>
    /// An account-activity event for an account the tenant doesn't have is a problem.
    /// </summary>
    [Name("Events.UnknownAccount")]
    public sealed class UnknownAccountRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            AccountActivityEvent activity = default!;
            this.When()
                .Match(() => activity)
                .Not<AccountFact>(account => account.AccountId == activity.AccountId);
            this.Then()
                .Yield(_ => new EventProblem(activity.EventId, $"Account {activity.AccountId} doesn't exist."));
        }
    }

    /// <summary>
    /// An account-activity event naming an asset the tenant doesn't have is a problem.
    /// </summary>
    [Name("Events.UnknownAsset")]
    public sealed class UnknownAssetRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            AccountActivityEvent activity = default!;
            IEnumerable<AssetFact> assets = default!;
            this.When()
                .Match(() => activity)
                .Query(() => assets, query => query
                    .Match<AssetFact>(asset => activity.Items.Any(item => item.AssetId == asset.AssetId))
                    .Collect())
                .Having(() => activity.Items.Select(item => item.AssetId).Distinct().Count() > assets.Count());
            this.Then()
                .Yield(_ => new EventProblem(
                    activity.EventId,
                    $"Asset {activity.Items.Select(item => item.AssetId).First(id => !assets.Any(asset => asset.AssetId == id))} doesn't exist."));
        }
    }
}
