// <copyright file="HoldingsController.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Holdings
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using AtlasLedger.Model.Primary;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;

    /// <summary>
    /// A holding as the API returns it, valued at its effective price.
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="SecurityId">The security.</param>
    /// <param name="Quantity">The number of shares.</param>
    /// <param name="CostBasis">The total paid, fees included; null when unknown.</param>
    /// <param name="IsOverridden">True when an operator's override set the quantity.</param>
    /// <param name="Price">The effective price, if the security has one.</param>
    /// <param name="PriceSource">Where the price came from.</param>
    /// <param name="MarketValue">Quantity times price, if there is a price.</param>
    public sealed record HoldingDto(Guid AccountId, Guid SecurityId, decimal Quantity, decimal? CostBasis, bool IsOverridden, decimal? Price, PriceSource? PriceSource, decimal? MarketValue);

    /// <summary>
    /// A tenant's holdings, cash and prices at the end of the last rules cycle.
    /// </summary>
    /// <param name="AsOf">When the rules last ran.</param>
    /// <param name="Holdings">The holdings with any shares (zero holdings are left out).</param>
    /// <param name="Cash">Each account's cash.</param>
    /// <param name="Prices">Each priced security's effective price.</param>
    public sealed record HoldingsDto(DateTimeOffset AsOf, IReadOnlyList<HoldingDto> Holdings, IReadOnlyList<CashState> Cash, IReadOnlyList<EffectivePrice> Prices);

    /// <summary>
    /// Serves what the tenant's events add up to: the rules' published snapshot, never the ledger's tables.
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
        [Authorize(Policy = LedgerAccessPolicies.TradeRead)]
        public ActionResult<HoldingsDto> Get(Guid tenantId)
        {
            if (this.store.Find(tenantId) is not { } snapshot)
            {
                return this.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "No holdings yet.",
                    detail: "The rules haven't run for this tenant since the server started. Switch the rules on (services on compliance) and try again.");
            }

            var prices = snapshot.Prices.ToDictionary(price => price.SecurityId);
            var holdings = snapshot.Positions
                .Where(position => position.Quantity != 0m)
                .OrderBy(position => position.AccountId)
                .ThenBy(position => position.SecurityId)
                .Select(position =>
                {
                    var price = prices.GetValueOrDefault(position.SecurityId);
                    return new HoldingDto(
                        position.AccountId,
                        position.SecurityId,
                        position.Quantity,
                        position.CostBasis,
                        position.IsOverridden,
                        price?.Price,
                        price?.Source,
                        price is null ? null : position.Quantity * price.Price);
                })
                .ToArray();
            return this.Ok(new HoldingsDto(snapshot.AsOf, holdings, snapshot.Cash.OrderBy(cash => cash.AccountId).ToArray(), snapshot.Prices.OrderBy(price => price.SecurityId).ToArray()));
        }
    }
}
