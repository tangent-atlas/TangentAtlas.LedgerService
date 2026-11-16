// <copyright file="JournalWindow.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Compliance
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Keeps a window of each tenant's changes in its journal for sync clients.  The rules engine applies a change within
    /// moments, and the journal used to be trimmed to it; a client (the website) that had read through a slightly earlier
    /// sequence, or whose long poll was answered at the durable watermark while the engine had read further, then fell
    /// behind the horizon and had to take a whole snapshot.  The journal now keeps every change for the window's length,
    /// so such a client gets only what changed.
    /// </summary>
    public sealed class JournalWindow
    {
        private readonly TimeSpan length;

        private readonly Dictionary<Guid, Queue<(DateTimeOffset At, long Sequence)>> marks = new Dictionary<Guid, Queue<(DateTimeOffset At, long Sequence)>>();

        /// <summary>
        /// Initializes a new instance of the <see cref="JournalWindow"/> class.
        /// </summary>
        /// <param name="length">How long a change stays in the journal at least; zero keeps nothing extra.</param>
        public JournalWindow(TimeSpan length)
        {
            this.length = length < TimeSpan.Zero ? TimeSpan.Zero : length;
        }

        /// <summary>
        /// Notes a tenant's committed sequence at a time, and returns the newest sequence it had committed at least the
        /// window's length before then: the journal may be trimmed through it, and no further.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="committedSequence">Its committed sequence now.</param>
        /// <param name="now">The time.</param>
        /// <returns>The sequence; zero while no mark is old enough.</returns>
        public long Mark(Guid tenantId, long committedSequence, DateTimeOffset now)
        {
            if (!this.marks.TryGetValue(tenantId, out var queue))
            {
                queue = new Queue<(DateTimeOffset At, long Sequence)>();
                this.marks.Add(tenantId, queue);
            }

            queue.Enqueue((now, committedSequence));
            var cutoff = now - this.length;
            var through = 0L;
            while (queue.Count > 0 && queue.Peek().At <= cutoff)
            {
                through = queue.Dequeue().Sequence;
            }

            return through;
        }

        /// <summary>
        /// Forgets the tenants that are gone.
        /// </summary>
        /// <param name="present">The tenants still present.</param>
        public void Keep(IReadOnlySet<Guid> present)
        {
            ArgumentNullException.ThrowIfNull(present);
            this.marks.Keys.Where(tenantId => !present.Contains(tenantId)).ToList().ForEach(tenantId => this.marks.Remove(tenantId));
        }
    }
}
