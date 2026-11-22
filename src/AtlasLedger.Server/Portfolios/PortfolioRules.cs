// <copyright file="PortfolioRules.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Portfolios
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using AtlasLedger.Server.Compliance;
    using NRules.Fluent.Dsl;

    /// <summary>
    /// The rules of the household's tree: which model governs each account, and the alerts that say when the tree, or a
    /// portfolio's model, can't be acted on.  The tree changes only when the customer changes it, so these rules don't run
    /// on price ticks.
    /// </summary>
    public static class PortfolioRules
    {
        /// <summary>
        /// The alert on a node whose place in the tree is wrong.
        /// </summary>
        public const string TreeRule = "PortfolioTree";

        /// <summary>
        /// The alert on a portfolio whose model doesn't trade yet.
        /// </summary>
        public const string NotTradingRule = "ModelNotTrading";

        /// <summary>
        /// Why a portfolio's model doesn't trade, in words: only a fixed-weights model with no cash rule, governing one
        /// account, trades today.  Index and custom models wait for the ranking data and the dial calculation; the cash
        /// rule for the dial calculation; a book of several accounts for placement across them, which comes with Commit
        /// Changes.
        /// </summary>
        /// <param name="portfolioId">The portfolio.</param>
        /// <param name="portfolios">The tenant's portfolios.</param>
        /// <param name="nodes">The tenant's nodes.</param>
        /// <param name="models">The tenant's models.</param>
        /// <param name="governances">Every account's governance.</param>
        /// <returns>Why not; null when it trades, or it has no model, or it isn't active.</returns>
        public static string? WhyNotTrading(Guid portfolioId, IEnumerable<PortfolioFact> portfolios, IEnumerable<NodeFact> nodes, IEnumerable<ModelFact> models, IEnumerable<Governance> governances)
        {
            ArgumentNullException.ThrowIfNull(portfolios);
            ArgumentNullException.ThrowIfNull(nodes);
            ArgumentNullException.ThrowIfNull(models);
            ArgumentNullException.ThrowIfNull(governances);
            if (portfolios.FirstOrDefault(item => item.PortfolioId == portfolioId) is not { ModelId: { } modelId }
                || nodes.FirstOrDefault(node => node.NodeId == portfolioId) is not { NodeStatus: NodeStatus.Active }
                || models.FirstOrDefault(model => model.ModelId == modelId) is not { } model)
            {
                return null;
            }

            if (model.ModelKind != ModelKind.FixedWeights)
            {
                return $"Its model, {model.Name}, is a {Describe(model.ModelKind)}, which doesn't trade yet: only fixed weights do.";
            }

            if (model.CashRule != CashRule.None)
            {
                return $"Its model, {model.Name}, has a cash rule, which doesn't trade yet: only fixed weights with no cash rule do.";
            }

            var book = governances.Count(governance => governance.GoverningPortfolioId == portfolioId);
            return book > 1
                ? $"Its model governs {book} accounts. Placing whole shares across several accounts comes with Commit Changes; until then a model trades only a single account."
                : null;
        }

        /// <summary>
        /// Whether a model trades an account it governs: a fixed-weights model with no cash rule, and the account the only
        /// one in its book.
        /// </summary>
        /// <param name="governance">The account's governance.</param>
        /// <param name="model">Its model.</param>
        /// <returns>True when it trades.</returns>
        public static bool Trades(Governance governance, ModelFact model)
        {
            ArgumentNullException.ThrowIfNull(governance);
            ArgumentNullException.ThrowIfNull(model);
            return governance.ModelId == model.ModelId && governance.Book.Count == 1 && model.ModelKind == ModelKind.FixedWeights && model.CashRule == CashRule.None;
        }

        private static string Describe(ModelKind kind) => kind switch
        {
            ModelKind.ConstantWeightIndex => "Constant Weight index",
            ModelKind.IndexWeightIndex => "Index Weight index",
            ModelKind.CustomList => "custom list",
            _ => "fixed-weights model",
        };
    }

    /// <summary>
    /// Each account's governance: which portfolio's model governs it, and its book (<see cref="PortfolioTree"/>).
    /// </summary>
    [Name("Portfolios.Governance")]
    public sealed class GovernanceRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            AccountFact account = default!;
            IEnumerable<NodeFact> nodes = default!;
            IEnumerable<EdgeFact> edges = default!;
            IEnumerable<PortfolioFact> portfolios = default!;
            IEnumerable<AccountFact> accounts = default!;
            this.When()
                .Match(() => account)
                .Query(() => nodes, query => query.Match<NodeFact>(node => PortfolioTree.IsInTree(node.NodeType)).Collect())
                .Query(() => edges, query => query.Match<EdgeFact>().Collect())
                .Query(() => portfolios, query => query.Match<PortfolioFact>().Collect())
                .Query(() => accounts, query => query.Match<AccountFact>().Collect());
            this.Then()
                .Yield(_ => PortfolioTree.From(nodes, edges, portfolios, accounts).GovernanceOf(account.AccountId));
        }
    }

    /// <summary>
    /// Raises an alert on a node whose place in the tree is wrong (<see cref="PortfolioTree.ProblemOf"/>).
    /// </summary>
    [Name("PortfolioTree.Raise")]
    public sealed class TreeRaiseRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            NodeFact node = default!;
            IEnumerable<NodeFact> nodes = default!;
            IEnumerable<EdgeFact> edges = default!;
            string? problem = default;
            LedgerRuleActions actions = default!;
            this.Dependency().Resolve(() => actions);
            this.When()
                .Match(() => node, candidate => PortfolioTree.IsInTree(candidate.NodeType), candidate => candidate.NodeStatus == NodeStatus.Active)
                .Query(() => nodes, query => query.Match<NodeFact>(item => PortfolioTree.IsInTree(item.NodeType)).Collect())
                .Query(() => edges, query => query.Match<EdgeFact>().Collect())
                .Let(() => problem, () => PortfolioTree.From(nodes, edges, Array.Empty<PortfolioFact>()).ProblemOf(node.NodeId))
                .Having(() => problem != null)
                .Not<AlertFact>(alert => alert.NodeId == node.NodeId, alert => alert.Rule == PortfolioRules.TreeRule);
            this.Then()
                .Do(_ => ComplianceRules.RaiseAlert(actions, node.NodeId, PortfolioRules.TreeRule, problem!));
        }
    }

    /// <summary>
    /// Clears a tree alert once the node's place is sound, or says something else is wrong now (the raise rule then raises
    /// it again, with the new problem).
    /// </summary>
    [Name("PortfolioTree.Clear")]
    public sealed class TreeClearRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            AlertFact alert = default!;
            IEnumerable<NodeFact> nodes = default!;
            IEnumerable<EdgeFact> edges = default!;
            string? problem = default;
            LedgerRuleActions actions = default!;
            this.Dependency().Resolve(() => actions);
            this.When()
                .Match(() => alert, candidate => candidate.Rule == PortfolioRules.TreeRule)
                .Query(() => nodes, query => query.Match<NodeFact>(item => PortfolioTree.IsInTree(item.NodeType)).Collect())
                .Query(() => edges, query => query.Match<EdgeFact>().Collect())
                .Let(() => problem, () => PortfolioTree.From(nodes, edges, Array.Empty<PortfolioFact>()).ProblemOf(alert.NodeId))
                .Having(() => problem != alert.Message);
            this.Then()
                .Do(_ => ComplianceRules.ClearAlert(actions, alert));
        }
    }

    /// <summary>
    /// Raises an alert on a portfolio whose model doesn't trade yet (<see cref="PortfolioRules.WhyNotTrading"/>).
    /// </summary>
    [Name("ModelNotTrading.Raise")]
    public sealed class NotTradingRaiseRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            PortfolioFact portfolio = default!;
            IEnumerable<PortfolioFact> portfolios = default!;
            IEnumerable<NodeFact> nodes = default!;
            IEnumerable<ModelFact> models = default!;
            IEnumerable<Governance> governances = default!;
            string? reason = default;
            LedgerRuleActions actions = default!;
            this.Dependency().Resolve(() => actions);
            this.When()
                .Match(() => portfolio, candidate => candidate.ModelId != null)
                .Query(() => portfolios, query => query.Match<PortfolioFact>(item => item.PortfolioId == portfolio.PortfolioId).Collect())
                .Query(() => nodes, query => query.Match<NodeFact>(item => item.NodeId == portfolio.PortfolioId).Collect())
                .Query(() => models, query => query.Match<ModelFact>(item => item.ModelId == portfolio.ModelId).Collect())
                .Query(() => governances, query => query.Match<Governance>(item => item.GoverningPortfolioId == portfolio.PortfolioId).Collect())
                .Let(() => reason, () => PortfolioRules.WhyNotTrading(portfolio.PortfolioId, portfolios, nodes, models, governances))
                .Having(() => reason != null)
                .Not<AlertFact>(alert => alert.NodeId == portfolio.PortfolioId, alert => alert.Rule == PortfolioRules.NotTradingRule);
            this.Then()
                .Do(_ => ComplianceRules.RaiseAlert(actions, portfolio.PortfolioId, PortfolioRules.NotTradingRule, reason!));
        }
    }

    /// <summary>
    /// Clears the alert once the portfolio's model trades, or the reason has changed (the raise rule then raises it again).
    /// </summary>
    [Name("ModelNotTrading.Clear")]
    public sealed class NotTradingClearRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            AlertFact alert = default!;
            IEnumerable<PortfolioFact> portfolios = default!;
            IEnumerable<NodeFact> nodes = default!;
            IEnumerable<ModelFact> models = default!;
            IEnumerable<Governance> governances = default!;
            string? reason = default;
            LedgerRuleActions actions = default!;
            this.Dependency().Resolve(() => actions);
            this.When()
                .Match(() => alert, candidate => candidate.Rule == PortfolioRules.NotTradingRule)
                .Query(() => portfolios, query => query.Match<PortfolioFact>(item => item.PortfolioId == alert.NodeId).Collect())
                .Query(() => nodes, query => query.Match<NodeFact>(item => item.NodeId == alert.NodeId).Collect())
                .Query(() => models, query => query.Match<ModelFact>().Collect())
                .Query(() => governances, query => query.Match<Governance>(item => item.GoverningPortfolioId == alert.NodeId).Collect())
                .Let(() => reason, () => PortfolioRules.WhyNotTrading(alert.NodeId, portfolios, nodes, models, governances))
                .Having(() => reason != alert.Message);
            this.Then()
                .Do(_ => ComplianceRules.ClearAlert(actions, alert));
        }
    }
}
