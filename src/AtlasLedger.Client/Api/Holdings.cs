// <copyright file="Holdings.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Api
{
    using System;
    using System.Collections.Generic;
    using AtlasLedger.Model;

    /// <summary>
    /// A holding, as the server's holdings endpoint returns it: an account's quantity of one asset (cash, a stock, a fund,
    /// a bond, an option, a future or a coin, all alike).
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="AssetId">The asset.</param>
    /// <param name="AssetType">The asset's type.</param>
    /// <param name="Quantity">The quantity held.</param>
    /// <param name="CostBasis">The cost of what is held; null when unknown.</param>
    /// <param name="IsOverridden">True when an override in force set the quantity.</param>
    /// <param name="Price">The effective price, as quoted, if any.</param>
    /// <param name="Currency">The currency the price is in: the asset's quote currency.</param>
    /// <param name="PriceSource">Where the price came from: Market, Override or LastTrade.</param>
    /// <param name="MarketValue">Quantity × QuantityFactor × price × PriceFactor × the quote currency's rate, in the valuation
    /// currency, if there is a price and a rate.</param>
    public sealed record Holding(Guid AccountId, Guid AssetId, AssetType AssetType, decimal Quantity, decimal? CostBasis, bool IsOverridden, decimal? Price, string Currency, string? PriceSource, decimal? MarketValue);

    /// <summary>
    /// The price of one unit of a currency in the valuation currency.
    /// </summary>
    /// <param name="CurrencyCode">The currency.</param>
    /// <param name="Rate">Its price in the valuation currency.</param>
    public sealed record CurrencyRate(string CurrencyCode, decimal Rate);

    /// <summary>
    /// A tenant's holdings, as the rules derived them from its transactions.
    /// </summary>
    /// <param name="AsOf">When the rules last ran.</param>
    /// <param name="ValuationCurrency">The currency market values are in.</param>
    /// <param name="Holdings">The holdings with any quantity.</param>
    /// <param name="Rates">Each currency's price in the valuation currency.</param>
    public sealed record TenantHoldings(DateTimeOffset AsOf, string ValuationCurrency, IReadOnlyList<Holding> Holdings, IReadOnlyList<CurrencyRate> Rates);
}
