// <copyright file="ServiceSwitchboard.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Switches
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Microsoft.Extensions.Options;

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
        /// The rules engine's name.
        /// </summary>
        public const string Rules = "rules";

        /// <summary>
        /// The order router's name.
        /// </summary>
        public const string Broker = "broker";

        private readonly Dictionary<string, ServiceSwitch> switches;

        /// <summary>
        /// Initializes a new instance of the <see cref="ServiceSwitchboard"/> class, with the services the settings name on
        /// (until turned off) and the rest off.
        /// </summary>
        /// <param name="timeProvider">The clock.</param>
        /// <param name="options">The settings, of which <see cref="ServiceSwitchOptions.StartOn"/> names the services on at start.</param>
        public ServiceSwitchboard(TimeProvider timeProvider, IOptions<ServiceSwitchOptions> options)
        {
            ArgumentNullException.ThrowIfNull(options);
            this.switches = new[]
            {
                new ServiceSwitch(MarketFeed, "Prices every held symbol (simulated until a broker feed is connected).", timeProvider),
                new ServiceSwitch(Rules, "Runs the rules engine for every tenant: derives holdings, prices and orders, books fills, rebalances, and raises and clears alerts.", timeProvider),
                new ServiceSwitch(Broker, "Sends broker orders to the brokers and records their reports. Nothing is sent while it is off.", timeProvider),
            }.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);

            // The services the settings start on stay on until turned off.  A name that isn't a service stops the server
            // starting, rather than silently leaving something off.
            foreach (var name in options.Value.StartOn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (this.Find(name) is not { } found)
                {
                    throw new InvalidOperationException($"Services:StartOn names \"{name}\", which is not a service. The services are: {string.Join(", ", this.switches.Keys.Order(StringComparer.Ordinal))}.");
                }

                found.TurnOnUntilTurnedOff();
            }
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
