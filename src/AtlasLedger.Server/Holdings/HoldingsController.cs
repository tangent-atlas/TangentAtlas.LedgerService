// <copyright file="HoldingsController.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Holdings
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;

    /// <summary>
    /// A holding as the API returns it, valued at its effective price.
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="AssetId">The asset.</param>
    /// <param name="AssetType">The asset's type.</param>
    /// <param name="Quantity">The quantity held.</param>
    /// <param name="CostBasis">The cost of what is held; null when unknown.</param>
    /// <param name="IsOverridden">True when an override in force set the quantity.</param>
    /// <param name="Price">The effective price, as quoted, if the asset has one.</param>
    /// <param name="PriceSource">Where the price came from.</param>
    /// <param name="Currency">The currency the price is in: the asset's quote currency.</param>
    /// <param name="MarketValue">Quantity × QuantityFactor × price × PriceFactor × the quote currency's rate, in the valuation
    /// currency, if there is a price and a rate.</param>
    public sealed record HoldingDto(
        Guid AccountId,
        Guid AssetId,
        AssetType AssetType,
        decimal Quantity,
        decimal? CostBasis,
        bool IsOverridden,
        decimal? Price,
        string Currency,
        PriceSource? PriceSource,
        decimal? MarketValue);

    /// <summary>
    /// A tenant's holdings and prices at the end of the last rules cycle.
    /// </summary>
    /// <param name="AsOf">When the rules last ran.</param>
    /// <param name="ValuationCurrency">The currency market values are in.</param>
    /// <param name="Holdings">The holdings with any quantity (zero holdings are left out).</param>
    /// <param name="Prices">Each priced asset's effective price.</param>
    /// <param name="Rates">Each currency's price in the valuation currency.</param>
    public sealed record HoldingsDto(DateTimeOffset AsOf, string ValuationCurrency, IReadOnlyList<HoldingDto> Holdings, IReadOnlyList<EffectivePrice> Prices, IReadOnlyList<CurrencyRate> Rates);

    /// <summary>
    /// Serves what the tenant's transactions add up to: the rules' published snapshot, never the ledger's tables.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("v1/tenants/{tenantId:guid}/holdings")]
    public sealed class HoldingsController : ControllerBase
    {
        private readonly HoldingsStore store;

        /// <summary>
        /// Initializes a new instance of the <see cref="HoldingsController"/> class.
        /// </summary>
        /// <param name="store">The rules' snapshots.</param>
        public HoldingsController(HoldingsStore store)
        {
            this.store = store;
        }

        /// <summary>
        /// Gets the tenant's holdings.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <returns>The holdings, or 409 when the rules haven't run for the tenant (the compliance switch is off).</returns>
        [HttpGet]
        [Authorize(Policy = LedgerAccessPolicies.TransactionRead)]
        public ActionResult<HoldingsDto> Get(Guid tenantId)
        {
            if (this.store.Find(tenantId) is not { } snapshot)
            {
                return this.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "No holdings yet.",
                    detail: "The rules haven't run for this tenant since the server started. Switch the rules on (services on compliance) and try again.");
            }

            var prices = snapshot.Prices.ToDictionary(price => price.AssetId);
            var rates = snapshot.Rates.ToDictionary(rate => rate.CurrencyCode, rate => rate.Rate);
            var holdings = snapshot.Positions
                .Where(position => position.Quantity != 0m)
                .OrderBy(position => position.AccountId)
                .ThenBy(position => position.AssetType)
                .ThenBy(position => position.AssetId)
                .Select(position =>
                {
                    var price = prices.GetValueOrDefault(position.AssetId);
                    decimal? rate = rates.TryGetValue(position.QuoteCurrency, out var found) ? found : null;
                    return new HoldingDto(
                        position.AccountId,
                        position.AssetId,
                        position.AssetType,
                        position.Quantity,
                        position.CostBasis,
                        position.IsOverridden,
                        price?.Price,
                        position.QuoteCurrency,
                        price?.Source,
                        price is null || rate is null ? null : HoldingFold.MarketValue(position.Quantity, position.QuantityFactor, price.Price, position.PriceFactor, rate.Value));
                })
                .ToArray();
            return this.Ok(new HoldingsDto(
                snapshot.AsOf,
                CurrencyCode.Valuation,
                holdings,
                snapshot.Prices.OrderBy(price => price.AssetId).ToArray(),
                snapshot.Rates.OrderBy(rate => rate.CurrencyCode, StringComparer.Ordinal).ToArray()));
        }
    }
}
