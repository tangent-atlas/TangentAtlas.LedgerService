// <copyright file="ServiceSwitchOptions.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Switches
{
    using System;

    /// <summary>
    /// Settings for turning the server's optional services on and off.
    /// </summary>
    public sealed class ServiceSwitchOptions
    {
        /// <summary>
        /// The settings section.
        /// </summary>
        public const string SectionName = "Services";

        /// <summary>
        /// Gets or sets how long a service stays on when the request does not say.
        /// </summary>
        public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromHours(4);

        /// <summary>
        /// Gets or sets the longest a service may be turned on for at once, so a mistyped duration cannot leave it running for
        /// a month.
        /// </summary>
        public TimeSpan MaximumDuration { get; set; } = TimeSpan.FromHours(24);
    }
}
