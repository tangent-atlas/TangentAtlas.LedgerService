// <copyright file="ServiceState.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Switches
{
    using System;

    /// <summary>
    /// A service and whether it is on, as the API shows it.
    /// </summary>
    public sealed class ServiceState
    {
        /// <summary>
        /// Gets or sets the service's name.
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

        /// <summary>
        /// Describes a switch as it is now.
        /// </summary>
        /// <param name="serviceSwitch">The switch.</param>
        /// <returns>Its state.</returns>
        public static ServiceState From(ServiceSwitch serviceSwitch)
        {
            ArgumentNullException.ThrowIfNull(serviceSwitch);
            var onUntil = serviceSwitch.OnUntil;
            return new ServiceState { Name = serviceSwitch.Name, Description = serviceSwitch.Description, IsOn = onUntil is not null, OnUntil = onUntil };
        }
    }
}
