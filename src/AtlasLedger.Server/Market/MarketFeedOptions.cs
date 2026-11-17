// <copyright file="MarketFeedOptions.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Market
{
    using System;

    /// <summary>
    /// Settings for the simulated market data feed.  Whether it runs is not a setting: it runs while its switch is on.
    /// </summary>
    public sealed class MarketFeedOptions
    {
        /// <summary>
        /// The settings section.
        /// </summary>
        public const string SectionName = "MarketFeed";

        /// <summary>
        /// Gets or sets the time between ticks; each tick moves some of the held symbols (<see cref="MoveChance"/>).
        /// </summary>
        public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Gets or sets the chance that a symbol's price moves on a tick, from 0 to 1: at 0.3 and a tick a second, each symbol
        /// moves about every three seconds, and not all together.
        /// </summary>
        public double MoveChance { get; set; } = 0.3;

        /// <summary>
        /// Gets or sets the most a price moves on one tick, up or down, in its quote currency: a few pennies, so prices drift
        /// around where the market left them (the last close) rather than wander off.
        /// </summary>
        public decimal MaximumMove { get; set; } = 0.05m;

        /// <summary>
        /// Gets or sets how often the feed sends a bad print (a price of zero for one symbol, corrected on the next tick), as
        /// real feeds occasionally do; zero (the default) sends none.
        /// </summary>
        public TimeSpan BadPrintInterval { get; set; } = TimeSpan.Zero;
    }
}
