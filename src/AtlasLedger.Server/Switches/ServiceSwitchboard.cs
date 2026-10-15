// <copyright file="ServiceSwitchboard.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Switches
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// The switches for the server's optional services.  The base system (the API, persistence, and recovery) always runs;
    /// the services on top of it run only while switched on.
    /// </summary>
    public sealed class ServiceSwitchboard
    {
        /// <summary>
        /// The market data feed's name.
        /// </summary>
        public const string MarketFeed = "market-feed";

        /// <summary>
        /// The compliance engine's name.
        /// </summary>
        public const string Compliance = "compliance";

        private readonly Dictionary<string, ServiceSwitch> switches;

        /// <summary>
        /// Initializes a new instance of the <see cref="ServiceSwitchboard"/> class, with every service off.
        /// </summary>
        /// <param name="timeProvider">The clock.</param>
        public ServiceSwitchboard(TimeProvider timeProvider)
        {
            this.switches = new[]
            {
                new ServiceSwitch(MarketFeed, "Prices every held symbol (simulated until a broker feed is connected).", timeProvider),
                new ServiceSwitch(Compliance, "Runs the compliance rules for every tenant and raises and clears alerts.", timeProvider),
            }.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets every switch, by name.
        /// </summary>
        public IEnumerable<ServiceSwitch> All => this.switches.Values.OrderBy(item => item.Name, StringComparer.Ordinal);

        /// <summary>
        /// Gets a switch by name.
        /// </summary>
        /// <param name="name">The service's name.</param>
        /// <returns>The switch.</returns>
        public ServiceSwitch this[string name] => this.switches[name];

        /// <summary>
        /// Finds a switch by name.
        /// </summary>
        /// <param name="name">The service's name.</param>
        /// <returns>The switch, or null if there is no such service.</returns>
        public ServiceSwitch? Find(string name) => this.switches.TryGetValue(name, out var found) ? found : null;
    }
}
