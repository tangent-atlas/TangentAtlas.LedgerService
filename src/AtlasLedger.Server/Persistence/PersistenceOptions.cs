// <copyright file="PersistenceOptions.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Persistence
{
    using System;

    /// <summary>
    /// Settings for writing the ledger to the database.
    /// </summary>
    public sealed class PersistenceOptions
    {
        /// <summary>
        /// The settings section.
        /// </summary>
        public const string SectionName = "Persistence";

        /// <summary>
        /// The name of the SQL Server connection string (<c>ConnectionStrings:Ledger</c>).  When it is not set, the server
        /// keeps its database in memory, and nothing survives a restart.
        /// </summary>
        public const string ConnectionName = "Ledger";

        /// <summary>
        /// Gets or sets how many times a failed write is retried before the tenant is stopped.
        /// </summary>
        public int RetryAttempts { get; set; } = 5;

        /// <summary>
        /// Gets or sets the delay before the first retry; each later retry waits twice as long.
        /// </summary>
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);
    }
}
