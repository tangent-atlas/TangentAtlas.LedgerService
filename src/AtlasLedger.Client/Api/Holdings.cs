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
    /// <param name="QuoteCurrency">The currency the price is in.</param>
    /// <param name="PriceSource">Where the price came from: Market, Override or LastTrade.</param>
    /// <param name="Currency">The account's currency: the cost basis and market value are in it.</param>
    /// <param name="MarketValue">Quantity × QuantityFactor × price × PriceFactor × the price of the quote currency in the
    /// account's currency, if there is a price and a rate.</param>
    /// <param name="Weight">Its share of the account's value.</param>
    /// <param name="TargetWeight">Its weight in the account's model, if the model has weights.</param>
    public sealed record Holding(Guid AccountId, Guid AssetId, AssetType AssetType, decimal Quantity, decimal? CostBasis, bool IsOverridden, decimal? Price, string QuoteCurrency, string? PriceSource, string Currency, decimal? MarketValue, decimal? Weight, decimal? TargetWeight);

    /// <summary>
    /// An account's totals, for the dashboard: every amount in the account's currency.
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="Currency">The account's currency.</param>
    /// <param name="MarketValue">Every holding's value, cash included; null while a holding has no value.</param>
    /// <param name="Cash">The value of its cash.</param>
    /// <param name="NetContributions">Deposits less withdrawals.</param>
    /// <param name="Gain">Market value less net contributions.</param>
    /// <param name="ModelId">The account's model.</param>
    /// <param name="ModelName">The model's name.</param>
    /// <param name="TargetCash">The share the model's weights leave in cash.</param>
    /// <param name="LastRebalance">The last trading day the account was rebalanced.</param>
    /// <param name="LastRebalanceOrders">How many orders that rebalance created.</param>
    public sealed record AccountSummary(Guid AccountId, string Currency, decimal? MarketValue, decimal Cash, decimal NetContributions, decimal? Gain, Guid? ModelId, string? ModelName, decimal? TargetCash, DateOnly? LastRebalance, int? LastRebalanceOrders);

    /// <summary>
    /// The price of one unit of a currency in another.
    /// </summary>
    /// <param name="FromCurrency">The currency priced.</param>
    /// <param name="ToCurrency">The currency its price is in.</param>
    /// <param name="Rate">The price.</param>
    public sealed record CurrencyRate(string FromCurrency, string ToCurrency, decimal Rate);

    /// <summary>
    /// A tenant's holdings, as the rules derived them from its transactions.
    /// </summary>
    /// <param name="AsOf">When the rules last ran.</param>
    /// <param name="Holdings">The holdings with any quantity, each valued in its account's currency.</param>
    /// <param name="Rates">Each published rate between currencies.</param>
    /// <param name="Accounts">Each account's totals.</param>
    public sealed record TenantHoldings(DateTimeOffset AsOf, IReadOnlyList<Holding> Holdings, IReadOnlyList<CurrencyRate> Rates, IReadOnlyList<AccountSummary> Accounts);
}
