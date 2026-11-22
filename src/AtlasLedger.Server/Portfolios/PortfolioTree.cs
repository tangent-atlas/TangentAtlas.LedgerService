// <copyright file="PortfolioTree.cs" company="Tangent Atlas">
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

    /// <summary>
    /// Which model governs an account, and the book it is governed with: derived from the household's tree, never saved.
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="PortfolioId">The portfolio it is in; null when it isn't in one.</param>
    /// <param name="GoverningPortfolioId">The nearest portfolio at or above it with a model; null when none has one (the
    /// account is tracked, not managed).</param>
    /// <param name="ModelId">That portfolio's model.</param>
    /// <param name="Book">Every active account the same model governs through the same portfolio, this one included, in
    /// order of ID: the model is applied to their combined value.  Only a brokerage account that isn't read-only is governed:
    /// a read-only account (a bank account, a card, a 401(k), anything linked read-only) counts in its portfolio's value and
    /// statistics but is never traded, so it is in no book and has no model.</param>
    public sealed record Governance(Guid AccountId, Guid? PortfolioId, Guid? GoverningPortfolioId, Guid? ModelId, IReadOnlyList<Guid> Book);

    /// <summary>
    /// The household's tree: the household at the root, portfolios beneath it to any depth, accounts in portfolios.  Built
    /// from the tenant's nodes, edges and portfolios; plain functions, so the rules call them and the tests call them
    /// directly.
    /// </summary>
    /// <remarks>
    /// The published rules: a portfolio's model governs every account beneath it, except those beneath a lower portfolio
    /// with a model of its own (a child's model overrides its parent's; a portfolio without one inherits).  A model's book is
    /// every active account it governs through one portfolio.  The tree's shape is checked here, and a node that breaks it
    /// gets an alert; it never stops the ledger.
    /// </remarks>
    public sealed class PortfolioTree
    {
        private readonly Dictionary<Guid, NodeFact> nodes;

        private readonly Dictionary<Guid, Guid> parents;

        private readonly Dictionary<Guid, Guid?> models;

        private readonly HashSet<Guid>? traded;

        private PortfolioTree(Dictionary<Guid, NodeFact> nodes, Dictionary<Guid, Guid> parents, Dictionary<Guid, Guid?> models, HashSet<Guid>? traded)
        {
            this.nodes = nodes;
            this.parents = parents;
            this.models = models;
            this.traded = traded;
        }

        /// <summary>
        /// Builds the tree.
        /// </summary>
        /// <param name="nodes">The tenant's nodes (assets and persons are ignored).</param>
        /// <param name="edges">The tenant's edges.</param>
        /// <param name="portfolios">The tenant's portfolios.</param>
        /// <param name="accounts">The tenant's accounts, to tell which a model may trade (<see cref="IsTraded"/>); without
        /// them every account is taken as tradable (for checking the tree's shape, which doesn't depend on it).</param>
        /// <returns>The tree.</returns>
        public static PortfolioTree From(IEnumerable<NodeFact> nodes, IEnumerable<EdgeFact> edges, IEnumerable<PortfolioFact> portfolios, IEnumerable<AccountFact>? accounts = null)
        {
            ArgumentNullException.ThrowIfNull(nodes);
            ArgumentNullException.ThrowIfNull(edges);
            ArgumentNullException.ThrowIfNull(portfolios);
            return new PortfolioTree(
                nodes.Where(node => IsInTree(node.NodeType)).ToDictionary(node => node.NodeId),
                edges.ToDictionary(edge => edge.ChildNodeId, edge => edge.ParentNodeId),
                portfolios.ToDictionary(portfolio => portfolio.PortfolioId, portfolio => portfolio.ModelId),
                accounts?.Where(account => IsTraded(account.AccountType, account.IsReadOnly)).Select(account => account.AccountId).ToHashSet());
        }

        /// <summary>
        /// Whether a model may trade an account: a brokerage account (where securities can be bought) that isn't read-only.
        /// A read-only account is static: it counts toward its portfolio, outside the part its model rebalances.
        /// </summary>
        /// <param name="type">The kind of account.</param>
        /// <param name="isReadOnly">Whether the account is only read.</param>
        /// <returns>True for a brokerage account that isn't read-only.</returns>
        public static bool IsTraded(AccountType type, bool isReadOnly) => type == AccountType.Brokerage && !isReadOnly;

        /// <summary>
        /// Whether a kind of node belongs in the household's tree.
        /// </summary>
        /// <param name="type">The kind.</param>
        /// <returns>True for the household, portfolios and accounts.</returns>
        public static bool IsInTree(NodeType type) => type is NodeType.Household or NodeType.Portfolio or NodeType.Account;

        /// <summary>
        /// A node's parent.
        /// </summary>
        /// <param name="nodeId">The node.</param>
        /// <returns>Its parent; null at the root, or when it has no edge.</returns>
        public Guid? ParentOf(Guid nodeId) => this.parents.TryGetValue(nodeId, out var parent) ? parent : null;

        /// <summary>
        /// A node's parents, nearest first, up to the root.  In a loop it stops before coming back round.
        /// </summary>
        /// <param name="nodeId">The node.</param>
        /// <returns>Its parents.</returns>
        public IReadOnlyList<Guid> AncestorsOf(Guid nodeId)
        {
            var ancestors = new List<Guid>();
            var seen = new HashSet<Guid> { nodeId };
            for (var parent = this.ParentOf(nodeId); parent is { } id && seen.Add(id); parent = this.ParentOf(id))
            {
                ancestors.Add(id);
            }

            return ancestors;
        }

        /// <summary>
        /// Whether a node is beneath itself.
        /// </summary>
        /// <param name="nodeId">The node.</param>
        /// <returns>True when following its parents comes back to it.</returns>
        public bool IsInLoop(Guid nodeId)
        {
            var seen = new HashSet<Guid>();
            for (var parent = this.ParentOf(nodeId); parent is { } id; parent = this.ParentOf(id))
            {
                if (id == nodeId)
                {
                    return true;
                }

                if (!seen.Add(id))
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// The portfolio whose model governs a node: the node itself if it is a portfolio with a model, or else its nearest
        /// parent portfolio with one.
        /// </summary>
        /// <param name="nodeId">The node.</param>
        /// <returns>The portfolio; null when none has a model, or the node is in a loop.</returns>
        public Guid? GoverningPortfolioOf(Guid nodeId)
        {
            if (this.IsInLoop(nodeId))
            {
                return null;
            }

            foreach (var id in this.AncestorsOf(nodeId).Prepend(nodeId))
            {
                if (this.models.TryGetValue(id, out var model) && model is not null && this.IsActivePortfolio(id))
                {
                    return id;
                }
            }

            return null;
        }

        /// <summary>
        /// The active accounts a portfolio's model governs: every active account beneath it that it may trade and that no lower
        /// portfolio with a model governs.
        /// </summary>
        /// <param name="portfolioId">The portfolio.</param>
        /// <returns>The accounts, in order of ID; none when the portfolio has no model.</returns>
        public IReadOnlyList<Guid> BookOf(Guid portfolioId) =>
            this.nodes.Values
                .Where(node => node.NodeType == NodeType.Account && node.NodeStatus == NodeStatus.Active)
                .Select(node => node.NodeId)
                .Where(this.IsTradedAccount)
                .Where(accountId => this.ParentOf(accountId) is { } parent && this.IsActivePortfolio(parent) && this.GoverningPortfolioOf(accountId) == portfolioId)
                .Order()
                .ToArray();

        /// <summary>
        /// Which model governs an account, and its book.
        /// </summary>
        /// <param name="accountId">The account.</param>
        /// <returns>Its governance.  An account not in an active portfolio, or one a model may not trade (read-only, or not a
        /// brokerage account), is governed by nothing.</returns>
        public Governance GovernanceOf(Guid accountId)
        {
            var parent = this.ParentOf(accountId);
            if (parent is not { } portfolioId || !this.IsActivePortfolio(portfolioId) || !this.IsTradedAccount(accountId) || this.GoverningPortfolioOf(accountId) is not { } governing)
            {
                return new Governance(accountId, parent is { } id && this.IsActivePortfolio(id) ? id : null, null, null, Array.Empty<Guid>());
            }

            return new Governance(accountId, portfolioId, governing, this.models[governing], this.BookOf(governing));
        }

        /// <summary>
        /// What is wrong with a node's place in the tree, in words.
        /// </summary>
        /// <param name="nodeId">The node.</param>
        /// <returns>The problem; null when its place is sound, or it isn't an active node of the tree.</returns>
        public string? ProblemOf(Guid nodeId)
        {
            if (!this.nodes.TryGetValue(nodeId, out var node) || node.NodeStatus != NodeStatus.Active)
            {
                return null;
            }

            if (this.IsInLoop(nodeId))
            {
                return "It is in a loop: following its parents comes back to it. Move it, or one of its parents, out of the loop.";
            }

            var parent = this.ParentOf(nodeId) is { } parentId ? this.nodes.GetValueOrDefault(parentId) : null;
            switch (node.NodeType)
            {
                case NodeType.Household:
                    if (this.nodes.Values.Count(other => other.NodeType == NodeType.Household && other.NodeStatus == NodeStatus.Active) > 1)
                    {
                        return "The tenant has more than one household; it should have exactly one.";
                    }

                    return this.ParentOf(nodeId) is null ? null : "The household is the root of the tree: it can't be in anything.";

                case NodeType.Portfolio:
                    return parent switch
                    {
                        null => "The portfolio isn't in the household or in another portfolio.",
                        { NodeType: NodeType.Household or NodeType.Portfolio, NodeStatus: NodeStatus.Closed } => "The portfolio is in a closed portfolio.",
                        { NodeType: NodeType.Household or NodeType.Portfolio } => null,
                        _ => "A portfolio can be only in the household or in another portfolio.",
                    };

                case NodeType.Account:
                    return parent switch
                    {
                        null => "The account isn't in a portfolio.",
                        { NodeType: NodeType.Portfolio, NodeStatus: NodeStatus.Closed } => "The account is in a closed portfolio.",
                        { NodeType: NodeType.Portfolio } => null,
                        { NodeType: NodeType.Household } => "The household holds only portfolios: put the account in a portfolio of its own.",
                        _ => "An account can be only in a portfolio.",
                    };

                default:
                    return null;
            }
        }

        private bool IsTradedAccount(Guid accountId) => this.traded is null || this.traded.Contains(accountId);

        private bool IsActivePortfolio(Guid nodeId) =>
            this.nodes.TryGetValue(nodeId, out var node) && node.NodeType == NodeType.Portfolio && node.NodeStatus == NodeStatus.Active;
    }
}
