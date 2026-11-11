// <copyright file="BrokerOptions.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Brokers
{
    using System;

    /// <summary>
    /// Settings for the order router and the simulated broker.
    /// </summary>
    public sealed class BrokerOptions
    {
        /// <summary>
        /// The settings section.
        /// </summary>
        public const string SectionName = "Broker";

        /// <summary>
        /// Gets or sets how often the router looks for orders to send and asks the brokers about open ones.
        /// </summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Gets or sets the fees the simulated broker charges on each fill, as one amount.
        /// </summary>
        public decimal SimulatedFees { get; set; }
    }
}
