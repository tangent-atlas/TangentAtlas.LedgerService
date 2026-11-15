// <copyright file="EventMessages.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Model
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// An event as it travels: the Event table's columns, and its payload as nested JSON (in the table, the payload is
    /// text).  The same shape in scripts, in requests, in streams and in exports.
    /// </summary>
    public sealed record EventMessage
    {
        /// <summary>Gets its key, supplied by its source.</summary>
        public required Guid EventId { get; init; }

        /// <summary>Gets its kind.</summary>
        public required EventType EventType { get; init; }

        /// <summary>Gets its payload's version.</summary>
        public int SchemaVersion { get; init; } = EventCodec.CurrentVersion;

        /// <summary>Gets where it came from.</summary>
        public required EventSource Source { get; init; }

        /// <summary>Gets its identity there: with <see cref="Source"/>, recorded once however often it is sent.</summary>
        public required string SourceId { get; init; }

        /// <summary>Gets its order at its source, among events at the same time.</summary>
        public long? SourceSequence { get; init; }

        /// <summary>Gets when it happened.</summary>
        public required DateTimeOffset EffectiveTime { get; init; }

        /// <summary>Gets when it was recorded at its source; when it happened, if not given.</summary>
        public DateTimeOffset? EnteredTime { get; init; }

        /// <summary>Gets what it belongs to (an order: the first of its replace chain).</summary>
        public Guid? CorrelationId { get; init; }

        /// <summary>Gets what caused it.</summary>
        public Guid? CausationId { get; init; }

        /// <summary>Gets its place in the log, when read from it (never sent).</summary>
        public long? Sequence { get; init; }

        /// <summary>Gets its kind's own fields.</summary>
        public required JsonElement Payload { get; init; }
    }

    /// <summary>
    /// A transaction: one or more events, committed or refused as a unit.  Its key is supplied by the client; sending it
    /// again is a no-op.
    /// </summary>
    public sealed record TransactionMessage
    {
        /// <summary>Gets its key.</summary>
        public required Guid TransactionId { get; init; }

        /// <summary>Gets its events, in order.</summary>
        public required IReadOnlyList<EventMessage> Events { get; init; }

        /// <summary>Gets its place in the log (its last event's), when read from it (never sent).</summary>
        public long? Sequence { get; init; }
    }

    /// <summary>
    /// What became of a transaction sent to the ledger.
    /// </summary>
    public enum TransactionStatus
    {
        /// <summary>Committed, and durable.</summary>
        Committed,

        /// <summary>Already committed: sent before, nothing changed.</summary>
        AlreadyCommitted,

        /// <summary>Refused: nothing was written.  The problems say why.</summary>
        Refused,
    }

    /// <summary>
    /// The ledger's answer to a transaction.
    /// </summary>
    /// <param name="TransactionId">The transaction, if it could be read.</param>
    /// <param name="Status">What became of it.</param>
    /// <param name="Sequence">Its place in the log, if committed.</param>
    /// <param name="Problems">Every reason it was refused.</param>
    /// <param name="Line">Its line, in a stream.</param>
    public sealed record TransactionResult(Guid? TransactionId, TransactionStatus Status, long? Sequence, IReadOnlyList<string> Problems, int? Line = null);

    /// <summary>
    /// Writes and reads the travelling shapes: events, transactions, scripts (a JSON array of transactions) and streams (JSON
    /// Lines: one transaction per line).
    /// </summary>
    public static class EventMessages
    {
        /// <summary>
        /// The media type of a stream: one JSON value per line.
        /// </summary>
        public const string StreamMediaType = "application/x-ndjson";

        /// <summary>
        /// The most events one transaction may hold: far above any natural unit, low enough to keep memory predictable.
        /// </summary>
        public const int MaximumEvents = 1_000;

        /// <summary>
        /// Gets the options every travelling shape is written and read with: names in camel case, enums by name, nulls left
        /// out.
        /// </summary>
        public static JsonSerializerOptions Options { get; } = CreateOptions();

        /// <summary>
        /// Makes the travelling form of an event.
        /// </summary>
        /// <param name="value">The event.</param>
        /// <param name="eventId">Its key.</param>
        /// <param name="source">Where it comes from.</param>
        /// <param name="sourceId">Its identity there.</param>
        /// <param name="effectiveTime">When it happened.</param>
        /// <param name="enteredTime">When it was recorded, if later.</param>
        /// <param name="sourceSequence">Its order at its source.</param>
        /// <param name="correlationId">What it belongs to.</param>
        /// <param name="causationId">What caused it.</param>
        /// <returns>The message.</returns>
        public static EventMessage From(
            LedgerEvent value,
            Guid eventId,
            EventSource source,
            string sourceId,
            DateTimeOffset effectiveTime,
            DateTimeOffset? enteredTime = null,
            long? sourceSequence = null,
            Guid? correlationId = null,
            Guid? causationId = null)
        {
            ArgumentNullException.ThrowIfNull(value);
            using var payload = JsonDocument.Parse(EventCodec.Encode(value));
            return new EventMessage
            {
                EventId = eventId,
                EventType = EventCodec.TypeOf(value),
                SchemaVersion = EventCodec.CurrentVersion,
                Source = source,
                SourceId = sourceId,
                SourceSequence = sourceSequence,
                EffectiveTime = effectiveTime,
                EnteredTime = enteredTime == effectiveTime ? null : enteredTime,
                CorrelationId = correlationId,
                CausationId = causationId,
                Payload = payload.RootElement.Clone(),
            };
        }

        /// <summary>
        /// Reads a message's payload as its kind.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <returns>The event, with an envelope from the message.</returns>
        /// <exception cref="FormatException">The kind or version is unknown, or the payload doesn't read as it.</exception>
        public static LedgerEvent Read(EventMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            var envelope = new EventEnvelope(
                message.EventId,
                message.EventType,
                message.SchemaVersion,
                message.Sequence ?? 0,
                message.Source,
                message.SourceId,
                message.EffectiveTime,
                message.EnteredTime ?? message.EffectiveTime,
                message.SourceSequence,
                message.CorrelationId,
                message.CausationId);
            return EventCodec.Decode(envelope, message.Payload.GetRawText());
        }

        /// <summary>
        /// Reads a script: a JSON array of transactions.
        /// </summary>
        /// <param name="json">The script.</param>
        /// <returns>Its transactions.</returns>
        public static IReadOnlyList<TransactionMessage> ReadScript(string json) =>
            JsonSerializer.Deserialize<List<TransactionMessage>>(json, Options) ?? throw new FormatException("The script is empty.");

        /// <summary>
        /// Writes a script: a JSON array of transactions, indented for reading.
        /// </summary>
        /// <param name="transactions">The transactions.</param>
        /// <returns>The script.</returns>
        public static string WriteScript(IEnumerable<TransactionMessage> transactions) =>
            JsonSerializer.Serialize(transactions.ToList(), new JsonSerializerOptions(Options) { WriteIndented = true });

        /// <summary>
        /// Writes one line of a stream: a transaction on one line.
        /// </summary>
        /// <param name="transaction">The transaction.</param>
        /// <returns>The line, without its line break.</returns>
        public static string WriteLine(TransactionMessage transaction) => JsonSerializer.Serialize(transaction, Options);

        /// <summary>
        /// Reads one line of a stream.
        /// </summary>
        /// <param name="line">The line.</param>
        /// <returns>The transaction.</returns>
        public static TransactionMessage ReadLine(string line) =>
            JsonSerializer.Deserialize<TransactionMessage>(line, Options) ?? throw new FormatException("The line is empty.");

        /// <summary>
        /// Whether a script's text is a script of transactions (a JSON array whose items have events), rather than a table
        /// script.
        /// </summary>
        /// <param name="json">The script's text.</param>
        /// <returns>True for transactions.</returns>
        public static bool IsTransactionScript(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.ValueKind == JsonValueKind.Array
                    && document.RootElement.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("events", out _));
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                RespectNullableAnnotations = true,
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
    }
}
