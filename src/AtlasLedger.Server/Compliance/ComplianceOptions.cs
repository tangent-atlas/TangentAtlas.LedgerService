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
        /// Gets or sets how often each tenant's change journal is trimmed to what its rules engine still needs.
        /// </summary>
        public TimeSpan JournalTrimInterval { get; set; } = TimeSpan.FromSeconds(5);
    }
}
