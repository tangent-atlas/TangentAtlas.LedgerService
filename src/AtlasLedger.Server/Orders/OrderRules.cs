// <copyright file="OrderRules.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Orders
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using AtlasLedger.Server.Compliance;
    using AtlasLedger.Server.Holdings;
    using NRules.Fluent.Dsl;

    /// <summary>
    /// An order's state: its allocations, broker orders and their execution reports, worked out together.  Every join is
    /// on the order's key.
    /// </summary>
    [Name("Orders.State")]
    public sealed class OrderStateRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            OrderFact order = default!;
            IEnumerable<AllocationFact> allocations = default!;
            IEnumerable<BrokerOrderFact> brokerOrders = default!;
            IEnumerable<ExecutionReportFact> reports = default!;

            this.When()
                .Match(() => order)
                .Query(() => allocations, query => query.Match<AllocationFact>(item => item.OrderId == order.OrderId).Collect())
                .Query(() => brokerOrders, query => query.Match<BrokerOrderFact>(item => item.OrderId == order.OrderId).Collect())
                .Query(() => reports, query => query
                    .Match<ExecutionReportFact>(item => brokerOrders.Any(brokerOrder => brokerOrder.BrokerOrderId == item.BrokerOrderId))
                    .Collect());
            this.Then()
                .Yield(_ => OrderBook.State(order, allocations, brokerOrders, reports));
        }
    }

    /// <summary>
    /// Books every fill not yet booked to the order's accounts, as Buy or Sell transactions, one per account and fill.  The
    /// division is recomputed from all the order's fills every time (it depends only on them, in the order recorded), and
    /// only the fills without a booking are written, so a fill is booked once: the next cycle sees the bookings as facts,
    /// and a booking written twice is the same row (its key comes from the fill and the account).
    /// </summary>
    [Name("Orders.BookFills")]
    public sealed class BookFillsRule : Rule
    {
        /// <summary>
        /// The alert raised when a fill can't be booked because an account is kept in another currency than the asset is
        /// quoted in (booking it needs an exchange of currencies, not built yet).
        /// </summary>
        public const string UnbookedFillRule = "UnbookedFill";

        /// <inheritdoc/>
        public override void Define()
        {
            OrderFact order = default!;
            AssetFact asset = default!;
            CashFact cash = default!;
            IEnumerable<AllocationFact> allocations = default!;
            IEnumerable<AccountFact> accounts = default!;
            IEnumerable<BrokerOrderFact> brokerOrders = default!;
            IEnumerable<ExecutionReportFact> fills = default!;
            IEnumerable<TransactionFact> booked = default!;
            LedgerRuleActions actions = default!;
            this.Dependency().Resolve(() => actions);

            this.When()
                .Match(() => order)
                .Match(() => asset, candidate => candidate.AssetId == order.AssetId)
                .Match(() => cash, candidate => candidate.CurrencyCode == asset.QuoteCurrency)
                .Query(() => brokerOrders, query => query.Match<BrokerOrderFact>(item => item.OrderId == order.OrderId).Collect())
                .Query(() => fills, query => query
                    .Match<ExecutionReportFact>(item => item.ReportType == ExecutionReportType.Fill, item => brokerOrders.Any(brokerOrder => brokerOrder.BrokerOrderId == item.BrokerOrderId))
                    .Collect())
                .Query(() => booked, query => query.Match<TransactionFact>(item => item.OrderId == order.OrderId).Collect())
                .Having(() => fills.Any(fill => !booked.Any(transaction => transaction.ExecutionReportId == fill.ExecutionReportId)))
                .Query(() => allocations, query => query.Match<AllocationFact>(item => item.OrderId == order.OrderId).Collect())
                .Query(() => accounts, query => query
                    .Match<AccountFact>(item => allocations.Any(allocation => allocation.AccountId == item.AccountId))
                    .Collect());
            this.Then()
                .Do(_ => Book(actions, order, asset, cash, allocations, accounts, fills, booked));
        }

        private static void Book(
            LedgerRuleActions actions,
            OrderFact order,
            AssetFact asset,
            CashFact cash,
            IEnumerable<AllocationFact> allocations,
            IEnumerable<AccountFact> accounts,
            IEnumerable<ExecutionReportFact> fills,
            IEnumerable<TransactionFact> booked)
        {
            var alreadyBooked = booked.Select(transaction => transaction.ExecutionReportId).OfType<Guid>().ToHashSet();
            var currencies = accounts.ToDictionary(account => account.AccountId, account => account.Currency);
            foreach (var fill in OrderBook.AllocateFills(allocations, fills).Where(item => !alreadyBooked.Contains(item.Fill.ExecutionReportId)))
            {
                foreach (var share in fill.Shares)
                {
                    if (currencies.GetValueOrDefault(share.AccountId) != asset.QuoteCurrency)
                    {
                        ComplianceRules.RaiseAlert(
                            actions,
                            share.AccountId,
                            $"{UnbookedFillRule}:{order.OrderId:N}",
                            $"A fill of order {order.OrderId} can't be booked: the account isn't kept in {asset.QuoteCurrency}, the currency the asset is quoted in.");
                        continue;
                    }

                    var (transaction, received, given) = OrderBook.Booking(order, asset, cash, fill.Fill, share);
                    actions.Add(ledger =>
                    {
                        ledger.InsertTransaction(transaction);
                        ledger.InsertTransactionLine(received);
                        ledger.InsertTransactionLine(given);
                    });
                }
            }
        }
    }

    /// <summary>
    /// An account's proposed allocations: the trades its model calls for now (<see cref="Rebalancing.Plan"/>), kept current
    /// as prices, holdings and orders change.  Derived and never saved: nothing is ordered until the order generator turns
    /// them into orders.  It fires after every other rule (lowest priority), so it sees the holdings, prices and order states
    /// of this cycle, not the last.
    /// </summary>
    [Name("Orders.Propose")]
    [Priority(-100)]
    public sealed class ProposeRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            AccountFact account = default!;
            ModelFact model = default!;
            ClockFact clock = default!;
            IEnumerable<WeightFact> weights = default!;
            IEnumerable<PositionState> positions = default!;
            IEnumerable<AssetFact> assets = default!;
            IEnumerable<EffectivePrice> prices = default!;
            IEnumerable<CurrencyRate> rates = default!;
            IEnumerable<OrderState> orders = default!;

            this.When()
                .Match(() => account, candidate => candidate.ModelId != null)
                .Match(() => model, candidate => candidate.ModelId == account.ModelId)
                .Match(() => clock)
                .Query(() => weights, query => query.Match<WeightFact>(item => item.ModelId == model.ModelId).Collect())
                .Query(() => positions, query => query.Match<PositionState>(item => item.AccountId == account.AccountId).Collect())
                .Query(() => assets, query => query.Match<AssetFact>().Collect())
                .Query(() => prices, query => query.Match<EffectivePrice>().Collect())
                .Query(() => rates, query => query.Match<CurrencyRate>().Collect())
                .Query(() => orders, query => query.Match<OrderState>().Collect());
            this.Then()
                .Yield(_ => Propose(account, model, clock, weights, positions, assets, prices, rates, orders));
        }

        private static ProposedAllocations Propose(
            AccountFact account,
            ModelFact model,
            ClockFact clock,
            IEnumerable<WeightFact> weights,
            IEnumerable<PositionState> positions,
            IEnumerable<AssetFact> assets,
            IEnumerable<EffectivePrice> prices,
            IEnumerable<CurrencyRate> rates,
            IEnumerable<OrderState> orders)
        {
            var plan = Rebalancing.Plan(
                account,
                Rebalancing.WeightsOf(weights, model.ModelId),
                positions,
                assets.ToDictionary(asset => asset.AssetId),
                prices.ToDictionary(price => price.AssetId),
                rates.ToDictionary(rate => (rate.FromCurrency, rate.ToCurrency), rate => rate.Rate),
                Rebalancing.OpenQuantities(account.AccountId, orders),
                model.Band,
                Rebalancing.StartOf(Rebalancing.TradingDay(clock.Now)));
            return new ProposedAllocations(account.AccountId, model.ModelId, plan ?? Array.Empty<PlannedTrade>(), plan is null);
        }
    }
}
