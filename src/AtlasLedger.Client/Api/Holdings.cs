// <copyright file="Holdings.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Api
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A holding, as the server's holdings endpoint returns it.
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="SecurityId">The security.</param>
    /// <param name="Quantity">The number of shares.</param>
    /// <param name="CostBasis">The total paid, fees included; null when unknown.</param>
    /// <param name="IsOverridden">True when an operator's override set the quantity.</param>
    /// <param name="Price">The effective price, if any.</param>
    /// <param name="PriceSource">Where the price came from: Market, Override or LastTrade.</param>
    /// <param name="MarketValue">Quantity times price, if there is a price.</param>
    public sealed record Holding(Guid AccountId, Guid SecurityId, decimal Quantity, decimal? CostBasis, bool IsOverridden, decimal? Price, string? PriceSource, decimal? MarketValue);

    /// <summary>
    /// An account's cash.
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="Cash">The cash, in dollars.</param>
    /// <param name="LastEffectiveTime">The effective time of the last event folded.</param>
    /// <param name="EventCount">How many events were folded.</param>
    public sealed record AccountCash(Guid AccountId, decimal Cash, DateTimeOffset LastEffectiveTime, int EventCount);

    /// <summary>
    /// A tenant's holdings and cash, as the rules derived them from its events.
    /// </summary>
    /// <param name="AsOf">When the rules last ran.</param>
    /// <param name="Holdings">The holdings with any shares.</param>
    /// <param name="Cash">Each account's cash.</param>
    public sealed record TenantHoldings(DateTimeOffset AsOf, IReadOnlyList<Holding> Holdings, IReadOnlyList<AccountCash> Cash);
}
