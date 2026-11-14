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

        /// <summary>
        /// Gets or sets when the simulated broker fills: at once, at the latest price (for the simulated feed), or at the
        /// next close (for paper accounts on daily closes: an order placed before a market day's close fills at that close,
        /// one placed after it at the next day's, once the close is loaded).
        /// </summary>
        public SimulatedFills SimulatedFills { get; set; } = SimulatedFills.Immediately;
    }

    /// <summary>
    /// When the simulated broker fills an order.
    /// </summary>
    public enum SimulatedFills
    {
        /// <summary>At once, at the latest price.</summary>
        Immediately,

        /// <summary>At the next close after the order was placed.</summary>
        AtNextClose,
    }
}
