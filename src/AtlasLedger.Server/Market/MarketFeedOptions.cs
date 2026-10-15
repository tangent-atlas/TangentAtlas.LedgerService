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
        /// Gets or sets the time between ticks; each tick prices every held symbol.
        /// </summary>
        public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Gets or sets how often the feed sends a bad print (a price of zero for one symbol, corrected on the next tick), as
        /// real feeds occasionally do; zero sends none.
        /// </summary>
        public TimeSpan BadPrintInterval { get; set; } = TimeSpan.FromSeconds(20);
    }
}
