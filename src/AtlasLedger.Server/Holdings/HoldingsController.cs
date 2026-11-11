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
    using AtlasLedger.Server.Orders;
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
    /// <param name="QuoteCurrency">The currency the price is in.</param>
    /// <param name="PriceSource">Where the price came from.</param>
    /// <param name="Currency">The account's currency: the cost basis and market value are in it.</param>
    /// <param name="MarketValue">Quantity × QuantityFactor × price × PriceFactor × the price of the quote currency in the
    /// account's currency, if there is a price and a rate.</param>
    /// <param name="Weight">Its share of the account's value, if every holding has a value.</param>
    /// <param name="TargetWeight">Its weight in the account's model, if the model has weights (none for an asset the model
    /// leaves out; cash is what the weights leave over).</param>
    public sealed record HoldingDto(
        Guid AccountId,
        Guid AssetId,
        AssetType AssetType,
        decimal Quantity,
        decimal? CostBasis,
        bool IsOverridden,
        decimal? Price,
        string QuoteCurrency,
        PriceSource? PriceSource,
        string Currency,
        decimal? MarketValue,
        decimal? Weight,
        decimal? TargetWeight);

    /// <summary>
    /// An account's totals, for the dashboard.
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="Currency">The account's currency: every amount is in it.</param>
    /// <param name="MarketValue">Every holding's value, cash included; null while a holding has no value.</param>
    /// <param name="Cash">The value of its cash.</param>
    /// <param name="NetContributions">Deposits less withdrawals.</param>
    /// <param name="Gain">Market value less net contributions.</param>
    /// <param name="ModelId">The account's model.</param>
    /// <param name="ModelName">The model's name.</param>
    /// <param name="TargetCash">The share the model's weights leave in cash, if it has weights.</param>
    /// <param name="LastRebalance">The last trading day the account was rebalanced, if ever.</param>
    /// <param name="LastRebalanceOrders">How many orders that rebalance created.</param>
    public sealed record AccountSummaryDto(
        Guid AccountId,
        string Currency,
        decimal? MarketValue,
        decimal Cash,
        decimal NetContributions,
        decimal? Gain,
        Guid? ModelId,
        string? ModelName,
        decimal? TargetCash,
        DateOnly? LastRebalance,
        int? LastRebalanceOrders);

    /// <summary>
    /// A tenant's holdings and prices at the end of the last rules cycle.
    /// </summary>
    /// <param name="AsOf">When the rules last ran.</param>
    /// <param name="Holdings">The holdings with any quantity (zero holdings are left out), each valued in its account's
    /// currency.</param>
    /// <param name="Prices">Each priced asset's effective price.</param>
    /// <param name="Rates">Each published rate between currencies.</param>
    /// <param name="Accounts">Each account's totals.</param>
    public sealed record HoldingsDto(DateTimeOffset AsOf, IReadOnlyList<HoldingDto> Holdings, IReadOnlyList<EffectivePrice> Prices, IReadOnlyList<CurrencyRate> Rates, IReadOnlyList<AccountSummaryDto> Accounts);

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
        /// <returns>The holdings, or 409 when the rules engine hasn't run for the tenant (it is switched off, or just starting).</returns>
        [HttpGet]
        [Authorize(Policy = LedgerAccessPolicies.TransactionRead)]
        public ActionResult<HoldingsDto> Get(Guid tenantId)
        {
            if (this.store.Find(tenantId) is not { } snapshot)
            {
                return this.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "No holdings yet.",
                    detail: "The rules engine hasn't run for this tenant since it was switched on. Switch it on (services on rules) and try again.");
            }

            var prices = snapshot.Prices.ToDictionary(price => price.AssetId);
            var rates = snapshot.Rates.ToDictionary(rate => (rate.FromCurrency, rate.ToCurrency), rate => rate.Rate);
            var held = snapshot.Positions.Where(position => position.Quantity != 0m).ToArray();
            var values = held.ToDictionary(position => (position.AccountId, position.AssetId), position => HoldingFold.MarketValue(position, prices.GetValueOrDefault(position.AssetId), rates));
            var accountIds = held.Select(position => position.AccountId)
                .Concat(snapshot.Flows.Select(flow => flow.AccountId))
                .Concat(snapshot.Accounts.Select(account => account.AccountId))
                .Distinct()
                .OrderBy(accountId => accountId)
                .ToArray();
            var totals = accountIds.ToDictionary(
                accountId => accountId,
                accountId =>
                {
                    var accountValues = held.Where(position => position.AccountId == accountId).Select(position => values[(position.AccountId, position.AssetId)]).ToArray();
                    return accountValues.Any(value => value is null) ? (decimal?)null : accountValues.Sum(value => value!.Value);
                });
            var accountFacts = snapshot.Accounts.ToDictionary(account => account.AccountId);
            var models = snapshot.Models.ToDictionary(model => model.ModelId);
            var weights = accountIds.ToDictionary(
                accountId => accountId,
                accountId => accountFacts.TryGetValue(accountId, out var account) ? Rebalancing.WeightsOf(snapshot.Weights, account.ModelId) : new Dictionary<Guid, decimal>());

            var holdings = held
                .OrderBy(position => position.AccountId)
                .ThenBy(position => position.AssetType)
                .ThenBy(position => position.AssetId)
                .Select(position =>
                {
                    var price = prices.GetValueOrDefault(position.AssetId);
                    var value = values[(position.AccountId, position.AssetId)];
                    var total = totals[position.AccountId];
                    var targets = weights[position.AccountId];
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
                        position.AccountCurrency,
                        value,
                        value is { } worth && total is > 0m ? worth / total.Value : null,
                        targets.Count == 0 || position.AssetType == AssetType.Cash ? null : targets.GetValueOrDefault(position.AssetId));
                })
                .ToArray();

            var accounts = accountIds
                .Select(accountId =>
                {
                    var positions = held.Where(position => position.AccountId == accountId).ToArray();
                    var currency = positions.FirstOrDefault()?.AccountCurrency ?? (accountFacts.TryGetValue(accountId, out var found) ? found.Currency : string.Empty);
                    var cash = positions.Where(position => position.AssetType == AssetType.Cash).Sum(position => values[(position.AccountId, position.AssetId)] ?? 0m);
                    var contributions = snapshot.Flows.FirstOrDefault(flow => flow.AccountId == accountId)?.NetContributions ?? 0m;
                    var total = totals[accountId];
                    var modelId = accountFacts.TryGetValue(accountId, out var account) ? account.ModelId : (Guid?)null;
                    var targets = weights[accountId];
                    var last = snapshot.Rebalances.Where(rebalance => rebalance.AccountId == accountId).OrderByDescending(rebalance => rebalance.TradingDay).FirstOrDefault();
                    return new AccountSummaryDto(
                        accountId,
                        currency,
                        total,
                        cash,
                        contributions,
                        total - contributions,
                        modelId,
                        modelId is { } id && models.TryGetValue(id, out var model) ? model.Name : null,
                        targets.Count == 0 ? null : 1m - targets.Values.Sum(),
                        last?.TradingDay,
                        last?.OrderCount);
                })
                .ToArray();

            return this.Ok(new HoldingsDto(
                snapshot.AsOf,
                holdings,
                snapshot.Prices.OrderBy(price => price.AssetId).ToArray(),
                snapshot.Rates.OrderBy(rate => rate.FromCurrency, StringComparer.Ordinal).ThenBy(rate => rate.ToCurrency, StringComparer.Ordinal).ToArray(),
                accounts));
        }
    }
}
