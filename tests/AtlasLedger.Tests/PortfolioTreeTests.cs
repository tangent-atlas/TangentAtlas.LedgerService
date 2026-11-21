// <copyright file="PortfolioTreeTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias server;

namespace AtlasLedger.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using AtlasLedger.Model;
    using server::AtlasLedger.Model;
    using server::AtlasLedger.Model.Primary;
    using server::AtlasLedger.Server.Portfolios;
    using Xunit;

    /// <summary>
    /// The household's tree: which model governs each account, its book, and what is wrong with a node's place.
    /// </summary>
    public sealed class PortfolioTreeTests
    {
        private static readonly DateOnly Monday = new DateOnly(2026, 11, 16);

        private static readonly Guid Household = Guid.NewGuid();

        private static readonly Guid Retirement = Guid.NewGuid();

        private static readonly Guid Joe10 = Guid.NewGuid();

        private static readonly Guid Picks = Guid.NewGuid();

        private static readonly Guid Ira = Guid.NewGuid();

        private static readonly Guid RothIra = Guid.NewGuid();

        private static readonly Guid PicksAccount = Guid.NewGuid();

        private static readonly Guid Joe10Model = Guid.NewGuid();

        private static readonly Guid RetirementModel = Guid.NewGuid();

        /// <summary>
        /// The household holds Retirement (with a model), which holds the Joe 10 (with its own model, holding the IRA and the
        /// Roth IRA) and Picks (no model, holding one account): Picks inherits Retirement's model, the Joe 10 overrides it.
        /// </summary>
        [Fact]
        public void AChildsModelOverridesItsParentsAndAPortfolioWithoutOneInherits()
        {
            var tree = Household_();
            Assert.Equal((Joe10, Joe10Model), Governing(tree, Ira));
            Assert.Equal((Joe10, Joe10Model), Governing(tree, RothIra));
            Assert.Equal((Retirement, RetirementModel), Governing(tree, PicksAccount));
            Assert.Equal(new[] { Ira, RothIra }.Order(), tree.BookOf(Joe10));
            Assert.Equal(new[] { PicksAccount }, tree.BookOf(Retirement));
            Assert.Equal(Picks, tree.GovernanceOf(PicksAccount).PortfolioId);
        }

        [Fact]
        public void APortfolioWithNoModelAboveItIsTrackedNotManaged()
        {
            var tree = Household_(withModels: false);
            var governance = tree.GovernanceOf(Ira);
            Assert.Equal((Joe10, (Guid?)null, (Guid?)null), (governance.PortfolioId!.Value, governance.GoverningPortfolioId, governance.ModelId));
            Assert.Empty(governance.Book);
        }

        [Fact]
        public void AClosedAccountLeavesTheBookAndAClosedPortfoliosModelGovernsNothing()
        {
            var closedAccount = Household_(closed: [RothIra]);
            Assert.Equal(new[] { Ira }, closedAccount.BookOf(Joe10));

            var closedPortfolio = Household_(closed: [Joe10]);
            Assert.Null(closedPortfolio.GovernanceOf(Ira).ModelId);
            Assert.Equal("The account is in a closed portfolio.", closedPortfolio.ProblemOf(Ira));
        }

        [Fact]
        public void ASoundTreeHasNoProblems()
        {
            var tree = Household_();
            Assert.All(new[] { Household, Retirement, Joe10, Picks, Ira, RothIra, PicksAccount }, nodeId => Assert.Null(tree.ProblemOf(nodeId)));
        }

        [Fact]
        public void EachWrongPlaceIsNamed()
        {
            var loose = Guid.NewGuid();
            var underHousehold = Guid.NewGuid();
            var looseFolder = Guid.NewGuid();
            var tree = PortfolioTree.From(
                [Node(Household, NodeType.Household), Node(Joe10, NodeType.Portfolio), Node(loose, NodeType.Account), Node(underHousehold, NodeType.Account), Node(looseFolder, NodeType.Portfolio), Node(Ira, NodeType.Account)],
                [Edge(Joe10, Household), Edge(underHousehold, Household), Edge(Ira, Joe10), Edge(Household, Joe10)],
                []);
            Assert.Equal("The account isn't in a portfolio.", tree.ProblemOf(loose));
            Assert.Equal("The household holds only portfolios: put the account in a portfolio of its own.", tree.ProblemOf(underHousehold));
            Assert.Equal("The portfolio isn't in the household or in another portfolio.", tree.ProblemOf(looseFolder));
            Assert.StartsWith("It is in a loop", tree.ProblemOf(Joe10));
            Assert.StartsWith("It is in a loop", tree.ProblemOf(Household));
            Assert.Null(tree.ProblemOf(Ira));   // beneath a loop, but not in it: its own place is a portfolio
        }

        [Fact]
        public void ALoopGovernsNothingAndEndsTheWalkUp()
        {
            var tree = PortfolioTree.From(
                [Node(Retirement, NodeType.Portfolio), Node(Joe10, NodeType.Portfolio), Node(Ira, NodeType.Account)],
                [Edge(Retirement, Joe10), Edge(Joe10, Retirement), Edge(Ira, Joe10)],
                [Portfolio(Retirement, RetirementModel), Portfolio(Joe10, null)]);
            Assert.True(tree.IsInLoop(Joe10));
            Assert.False(tree.IsInLoop(Ira));
            Assert.Equal(new[] { Joe10, Retirement }, tree.AncestorsOf(Ira));
            Assert.Equal(RetirementModel, tree.GovernanceOf(Ira).ModelId);
            Assert.Null(tree.GoverningPortfolioOf(Joe10));
        }

        [Fact]
        public void OnlyFixedWeightsWithNoCashRuleOnASingleAccountTrade()
        {
            var nodes = new[] { Node(Joe10, NodeType.Portfolio) };
            var portfolios = new[] { Portfolio(Joe10, Joe10Model) };
            Governance One(Guid accountId) => new Governance(accountId, Joe10, Joe10, Joe10Model, [accountId]);
            string? Why(ModelFact model, params Governance[] governances) => PortfolioRules.WhyNotTrading(Joe10, portfolios, nodes, [model], governances);

            var fixedWeights = Model(ModelKind.FixedWeights, CashRule.None);
            Assert.Null(Why(fixedWeights, One(Ira)));
            Assert.True(PortfolioRules.Trades(One(Ira), fixedWeights));

            var index = Model(ModelKind.ConstantWeightIndex, CashRule.VolatilityTarget);
            Assert.Equal("Its model, Joe 10, is a Constant Weight index, which doesn't trade yet: only fixed weights do.", Why(index, One(Ira)));
            Assert.False(PortfolioRules.Trades(One(Ira), index));

            var thermostat = Model(ModelKind.FixedWeights, CashRule.VolatilityTarget);
            Assert.StartsWith("Its model, Joe 10, has a cash rule", Why(thermostat, One(Ira)));

            var two = new[] { new Governance(Ira, Joe10, Joe10, Joe10Model, [Ira, RothIra]), new Governance(RothIra, Joe10, Joe10, Joe10Model, [Ira, RothIra]) };
            Assert.StartsWith("Its model governs 2 accounts.", Why(fixedWeights, two));
            Assert.False(PortfolioRules.Trades(two[0], fixedWeights));
        }

        private static (Guid Portfolio, Guid Model) Governing(PortfolioTree tree, Guid accountId)
        {
            var governance = tree.GovernanceOf(accountId);
            return (governance.GoverningPortfolioId!.Value, governance.ModelId!.Value);
        }

        private static PortfolioTree Household_(bool withModels = true, IReadOnlyCollection<Guid>? closed = null) =>
            Build(withModels ? RetirementModel : null, withModels ? Joe10Model : null, closed ?? []);

        private static PortfolioTree Build(Guid? retirementModel, Guid? joe10Model, IReadOnlyCollection<Guid> closed)
        {
            NodeFact Of(Guid id, NodeType type) => Node(id, type, closed.Contains(id) ? NodeStatus.Closed : NodeStatus.Active);
            return PortfolioTree.From(
                [Of(Household, NodeType.Household), Of(Retirement, NodeType.Portfolio), Of(Joe10, NodeType.Portfolio), Of(Picks, NodeType.Portfolio), Of(Ira, NodeType.Account), Of(RothIra, NodeType.Account), Of(PicksAccount, NodeType.Account)],
                [Edge(Retirement, Household), Edge(Joe10, Retirement), Edge(Picks, Retirement, 1), Edge(Ira, Joe10), Edge(RothIra, Joe10, 1), Edge(PicksAccount, Picks)],
                [Portfolio(Retirement, retirementModel), Portfolio(Joe10, joe10Model), Portfolio(Picks, null)]);
        }

        private static NodeFact Node(Guid id, NodeType type, NodeStatus status = NodeStatus.Active) =>
            new NodeFact(new NodeDto { NodeId = id, DisplayName = type.ToString(), FullName = type.ToString(), NodeType = type, NodeStatus = status });

        private static EdgeFact Edge(Guid child, Guid parent, int position = 0) =>
            new EdgeFact(new EdgeDto { ChildNodeId = child, ParentNodeId = parent, Position = position, Since = Monday });

        private static PortfolioFact Portfolio(Guid id, Guid? modelId) =>
            new PortfolioFact(new PortfolioDto { PortfolioId = id, ModelId = modelId });

        private static ModelFact Model(ModelKind kind, CashRule cashRule) =>
            new ModelFact(new ModelDto { ModelId = Joe10Model, Name = "Joe 10", ModelKind = kind, CashRule = cashRule, Band = 0.005m });
    }
}
