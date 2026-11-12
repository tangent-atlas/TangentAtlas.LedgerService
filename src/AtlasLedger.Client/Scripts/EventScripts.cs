// <copyright file="EventScripts.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Scripts
{
    using System;
    using AtlasLedger.Model;

    /// <summary>
    /// Writes events into scripts: each event becomes an Event row, its payload encoded by its kind.
    /// </summary>
    public static class EventScripts
    {
        /// <summary>
        /// Adds an event to a script.
        /// </summary>
        /// <param name="script">The script.</param>
        /// <param name="value">The event.</param>
        /// <param name="eventId">Its key (the caller supplies every key).</param>
        /// <param name="source">Where it comes from.</param>
        /// <param name="sourceId">Its identity there.</param>
        /// <param name="effectiveTime">When it happened.</param>
        /// <param name="enteredTime">When it was recorded.</param>
        /// <param name="sourceSequence">Its order at its source, among events at the same time.</param>
        /// <param name="correlationId">What it belongs to (an order).</param>
        /// <param name="causationId">What caused it.</param>
        /// <returns>The script.</returns>
        public static LedgerScript AppendEvent(
            this LedgerScript script,
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
            ArgumentNullException.ThrowIfNull(script);
            return script.Insert("Event", new EventDto
            {
                EventId = eventId,
                EventType = EventCodec.TypeOf(value),
                SchemaVersion = EventCodec.CurrentVersion,
                Source = source,
                SourceId = sourceId,
                EffectiveTime = effectiveTime,
                EnteredTime = enteredTime ?? effectiveTime,
                SourceSequence = sourceSequence,
                CorrelationId = correlationId,
                CausationId = causationId,
                Payload = EventCodec.Encode(value),
            });
        }
    }
}
