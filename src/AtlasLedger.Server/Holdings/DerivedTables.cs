// <copyright file="DerivedTables.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Holdings
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using AtlasLedger.Model.Primary;

    /// <summary>
    /// Writes what the rules derive into the model's derived tables (Position, EffectivePrice, CurrencyRate, AccountFlow,
    /// AccountDay, AccountStatistic, PortfolioDay, PortfolioStatistic): ephemeral, written only by the server, read through the API and client sync like any
    /// other table.  Each cycle replaces each table with what the rules hold, in the cycle's one transaction; only rows that
    /// differ are written, so unchanged rows keep their versions and nothing about them reaches the change feed.
    /// </summary>
    /// <remarks>
    /// Values are rounded to their columns' scale (money to 4 places; prices, rates and returns to 6), since a value with
    /// more places doesn't fit its column.  Rows whose account or asset isn't in the tenant are left out: the rules may hold
    /// facts from events naming one that was never declared, and the tables' references must resolve.
    /// </remarks>
    public static class DerivedTables
    {
        private const int MoneyPlaces = 4;

        private const int PricePlaces = 6;

        /// <summary>
        /// The change that makes the derived tables what the rules hold: recorded as a rule action, so it commits with the
        /// cycle's other decisions.
        /// </summary>
        /// <param name="positions">Every holding.</param>
        /// <param name="prices">Every effective price.</param>
        /// <param name="rates">Every rate.</param>
        /// <param name="flows">Every account's net contributions.</param>
        /// <param name="histories">Every account's history and statistics.</param>
        /// <param name="portfolios">Every portfolio's history and statistics.</param>
        /// <returns>The change.</returns>
        public static Action<LedgerTransaction> Materialize(
            IReadOnlyList<PositionState> positions,
            IReadOnlyList<EffectivePrice> prices,
            IReadOnlyList<CurrencyRate> rates,
            IReadOnlyList<AccountFlow> flows,
            IReadOnlyList<AccountHistory> histories,
            IReadOnlyList<Accounting.PortfolioHistory> portfolios)
        {
            ArgumentNullException.ThrowIfNull(positions);
            ArgumentNullException.ThrowIfNull(prices);
            ArgumentNullException.ThrowIfNull(rates);
            ArgumentNullException.ThrowIfNull(flows);
            ArgumentNullException.ThrowIfNull(histories);
            ArgumentNullException.ThrowIfNull(portfolios);
            return transaction =>
            {
                var partition = transaction.Partition;
                bool HasAccount(Guid accountId) => partition.FindAccount(accountId) is not null;
                bool HasAsset(Guid assetId) => partition.FindAsset(assetId) is not null;

                transaction.ReplacePositions(positions
                    .Where(position => position.Quantity != 0m && HasAccount(position.AccountId) && HasAsset(position.AssetId))
                    .Select(position => new global::AtlasLedger.Model.Primary.Position
                    {
                        AccountId = position.AccountId,
                        AssetId = position.AssetId,
                        AssetType = position.AssetType,
                        AccountCurrency = position.AccountCurrency,
                        QuoteCurrency = position.QuoteCurrency,
                        QuantityFactor = position.QuantityFactor,
                        PriceFactor = position.PriceFactor,
                        Quantity = Math.Round(position.Quantity, PricePlaces),
                        CostBasis = Round(position.CostBasis, MoneyPlaces),
                        IsOverridden = position.IsOverridden,
                        LastEffectiveTime = position.LastEffectiveTime,
                    }));
                transaction.ReplaceEffectivePrices(prices
                    .Where(price => HasAsset(price.AssetId))
                    .Select(price => new global::AtlasLedger.Model.Primary.EffectivePrice
                    {
                        AssetId = price.AssetId,
                        Price = Math.Round(price.Price, PricePlaces),
                        Currency = price.Currency,
                        AsOf = price.AsOf,
                        Source = price.Source,
                    }));
                transaction.ReplaceCurrencyRates(rates.Select(rate => new global::AtlasLedger.Model.Primary.CurrencyRate
                {
                    FromCurrency = rate.FromCurrency,
                    ToCurrency = rate.ToCurrency,
                    Rate = Math.Round(rate.Rate, PricePlaces),
                }));
                transaction.ReplaceAccountFlows(flows
                    .Where(flow => HasAccount(flow.AccountId))
                    .Select(flow => new global::AtlasLedger.Model.Primary.AccountFlow { AccountId = flow.AccountId, NetContributions = Math.Round(flow.NetContributions, MoneyPlaces) }));

                var measured = histories.Where(history => HasAccount(history.AccountId)).ToArray();
                transaction.ReplaceAccountDays(measured.SelectMany(history => history.Days.Select(day => new global::AtlasLedger.Model.Primary.AccountDay
                {
                    AccountId = history.AccountId,
                    Date = day.Date,
                    Value = Round(day.Value, MoneyPlaces),
                    Flow = Math.Round(day.Flow, MoneyPlaces),
                    Return = Round(day.Return, PricePlaces),
                    RiskFreeReturn = Round(day.RiskFreeReturn, PricePlaces),
                })));
                transaction.ReplaceAccountStatistics(measured.Select(history => history.Statistics).Select(statistics => new AccountStatistic
                {
                    AccountId = statistics.AccountId,
                    From = statistics.From,
                    Through = statistics.Through,
                    Value = Round(statistics.Value, MoneyPlaces),
                    NetFlows = Math.Round(statistics.NetFlows, MoneyPlaces),
                    Returns = statistics.Returns,
                    TotalReturn = Round(statistics.TotalReturn, PricePlaces),
                    AnnualizedReturn = Round(statistics.AnnualizedReturn, PricePlaces),
                    Volatility = Round(statistics.Volatility, PricePlaces),
                    SharpeRatio = Round(statistics.SharpeRatio, PricePlaces),
                    MaximumDrawdown = Round(statistics.MaximumDrawdown, PricePlaces),
                    IsRanked = statistics.IsRanked,
                }));

                var books = portfolios.Where(history => partition.FindPortfolio(history.PortfolioId) is not null).ToArray();
                transaction.ReplacePortfolioDays(books.SelectMany(history => history.Days.Select(day => new PortfolioDay
                {
                    PortfolioId = history.PortfolioId,
                    Date = day.Date,
                    Value = Round(day.Value, MoneyPlaces),
                    Flow = Math.Round(day.Flow, MoneyPlaces),
                    Return = Round(day.Return, PricePlaces),
                    RiskFreeReturn = Round(day.RiskFreeReturn, PricePlaces),
                })));
                transaction.ReplacePortfolioStatistics(books.Select(history => history.Statistics).Select(statistics => new PortfolioStatistic
                {
                    PortfolioId = statistics.AccountId,
                    From = statistics.From,
                    Through = statistics.Through,
                    Value = Round(statistics.Value, MoneyPlaces),
                    NetFlows = Math.Round(statistics.NetFlows, MoneyPlaces),
                    Returns = statistics.Returns,
                    TotalReturn = Round(statistics.TotalReturn, PricePlaces),
                    AnnualizedReturn = Round(statistics.AnnualizedReturn, PricePlaces),
                    Volatility = Round(statistics.Volatility, PricePlaces),
                    SharpeRatio = Round(statistics.SharpeRatio, PricePlaces),
                    MaximumDrawdown = Round(statistics.MaximumDrawdown, PricePlaces),
                    IsRanked = statistics.IsRanked,
                }));
            };
        }

        private static decimal? Round(decimal? value, int places) => value is { } number ? Math.Round(number, places) : null;
    }
}
