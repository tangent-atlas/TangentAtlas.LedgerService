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
    /// Each order's state, from its events: its broker orders, the broker's answers, cancel requests, and any replacement.
    /// </summary>
    [Name("Orders.State")]
    public sealed class OrderStateRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            OrderPlacedEvent order = default!;
            IEnumerable<OrderEvent> events = default!;
            IEnumerable<OrderReplacedEvent> replacements = default!;
            this.When()
                .Match(() => order)
                .Query(() => events, query => query.Match<OrderEvent>(item => item.OrderId == order.OrderId, item => !(item is OrderPlacedEvent)).Collect())
                .Query(() => replacements, query => query.Match<OrderReplacedEvent>(item => item.Replaces == order.OrderId).Collect());
            this.Then()
                .Yield(_ => OrderBook.State(order, events, replacements.OrderBy(OrderBook.ProcessingOrder).FirstOrDefault()));
        }
    }

    /// <summary>
    /// Posts an order's fills to its accounts' holdings, by the published allocation rule.  Derived from the fills, so
    /// nothing is written: the fills are the record.
    /// </summary>
    [Name("Orders.PostFills")]
    public sealed class PostFillsRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            OrderPlacedEvent order = default!;
            IEnumerable<ExecutionReportedEvent> fills = default!;
            IEnumerable<AssetFact> assets = default!;
            IEnumerable<CashFact> cash = default!;
            IEnumerable<AccountFact> accounts = default!;
            this.When()
                .Match(() => order)
                .Query(() => fills, query => query.Match<ExecutionReportedEvent>(item => item.OrderId == order.OrderId).Collect())
                .Having(() => fills.Any())
                .Query(() => assets, query => query.Match<AssetFact>(item => order.Legs.Any(leg => leg.AssetId == item.AssetId)).Collect())
                .Query(() => cash, query => query.Match<CashFact>().Collect())
                .Query(() => accounts, query => query.Match<AccountFact>(item => order.Legs.Any(leg => leg.Allocations.Any(allocation => allocation.AccountId == item.AccountId))).Collect());
            this.Then()
                .Yield(_ => new PostedLines(
                    order.OrderId,
                    OrderBook.Lines(
                        order,
                        fills,
                        assets.ToDictionary(asset => asset.AssetId),
                        cash.GroupBy(item => item.CurrencyCode).ToDictionary(group => group.Key, group => group.First()),
                        accounts.ToDictionary(account => account.AccountId, account => account.Currency))));
        }
    }

    /// <summary>
    /// A fill for an account kept in another currency than the asset's quote currency can't be posted: an alert says so.
    /// </summary>
    [Name("Orders.UnpostedFill")]
    public sealed class UnpostedFillRule : Rule
    {
        /// <summary>
        /// The alert's rule name, followed by the order.
        /// </summary>
        public const string UnpostedFillAlert = "UnpostedFill";

        /// <inheritdoc/>
        public override void Define()
        {
            OrderPlacedEvent order = default!;
            ExecutionReportedEvent fill = default!;
            AssetFact asset = default!;
            AccountFact account = default!;
            LedgerRuleActions actions = default!;
            this.Dependency().Resolve(() => actions);
            this.When()
                .Match(() => fill)
                .Match(() => order, candidate => candidate.OrderId == fill.OrderId)
                .Match(() => asset, candidate => order.Legs.Any(leg => leg.LegId == fill.LegId && leg.AssetId == candidate.AssetId))
                .Match(() => account, candidate => order.Legs.Any(leg => leg.LegId == fill.LegId && leg.Allocations.Any(allocation => allocation.AccountId == candidate.AccountId)), candidate => candidate.Currency != asset.QuoteCurrency)
                .Not<AlertFact>(alert => alert.NodeId == account.AccountId, alert => alert.Rule == UnpostedFillAlert + ":" + order.OrderId.ToString("N"));
            this.Then()
                .Do(_ => ComplianceRules.RaiseAlert(
                    actions,
                    account.AccountId,
                    UnpostedFillAlert + ":" + order.OrderId.ToString("N"),
                    $"A fill of order {order.OrderId} can't be posted: the account isn't kept in {asset.QuoteCurrency}, the currency the asset is quoted in."));
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
