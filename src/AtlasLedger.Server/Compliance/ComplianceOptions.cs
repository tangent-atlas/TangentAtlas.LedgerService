// <copyright file="ComplianceOptions.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Compliance
{
    using System;

    /// <summary>
    /// Settings for the compliance engine.
    /// </summary>
    public sealed class ComplianceOptions
    {
        /// <summary>
        /// The settings section.
        /// </summary>
        public const string SectionName = "Compliance";

        /// <summary>
        /// Gets or sets the shortest time between a tenant's rules cycles: the cost control.  Changes in between coalesce.
        /// </summary>
        public TimeSpan MinimumInterval { get; set; } = TimeSpan.FromMilliseconds(50);

        /// <summary>
        /// Gets or sets how many tenants' cycles may run at once; zero means one per processor.
        /// </summary>
        public int MaximumConcurrentCycles { get; set; }

        /// <summary>
        /// Gets or sets how often each tenant's change journal is trimmed to what its rules engine and sync clients still need.
        /// </summary>
        public TimeSpan JournalTrimInterval { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets how long a change stays in the journal after it is committed, whatever the rules engine has applied:
        /// a sync client (the website) that is a few moments behind, or briefly disconnected, then gets only the changes
        /// rather than a snapshot.
        /// </summary>
        public TimeSpan JournalWindow { get; set; } = TimeSpan.FromMinutes(10);
    }
}
