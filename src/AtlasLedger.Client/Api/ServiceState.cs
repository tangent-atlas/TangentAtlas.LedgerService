// <copyright file="ServiceState.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Api
{
    using System;

    /// <summary>
    /// One of the server's optional services (the market feed, compliance) and whether it is on.
    /// </summary>
    public sealed class ServiceState
    {
        /// <summary>
        /// Gets or sets the service's name, for example <c>market-feed</c>.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets what the service does.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether the service is on.
        /// </summary>
        public bool IsOn { get; set; }

        /// <summary>
        /// Gets or sets when the service turns itself off; null when it is off.
        /// </summary>
        public DateTimeOffset? OnUntil { get; set; }
    }
}
