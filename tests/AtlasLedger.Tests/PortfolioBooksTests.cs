// <copyright file="PortfolioBooksTests.cs" company="Tangent Atlas">
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
    using server::AtlasLedger.Server.Accounting;
    using server::AtlasLedger.Server.Holdings;
    using Xunit;
    using AccountDay = server::AtlasLedger.Server.Holdings.AccountDay;

    /// <summary>
    /// A portfolio's books, rough for now: the sum of its accounts' days, with joins, leaves and internal transfers handled
    /// so they don't count as gains or losses.
    /// </summary>
    public sealed class PortfolioBooksTests
    {
        private static readonly DateOnly Monday = new DateOnly(2026, 11, 16);

        private static readonly DateOnly Tuesday = Monday.AddDays(1);

        private static readonly DateOnly Wednesday = Monday.AddDays(2);

        private static readonly DateOnly Thursday = Monday.AddDays(3);

        private static readonly Guid Household = Guid.NewGuid();

        private static readonly Guid Retirement = Guid.NewGuid();

        private static readonly Guid Joe10 = Guid.NewGuid();

        private static readonly Guid Picks = Guid.NewGuid();

        private static readonly Guid Ira = Guid.NewGuid();

        private static readonly Guid Roth = Guid.NewGuid();

        [Fact]
        public void APortfolioOfOneAccountIsThatAccount()
        {
            var ira = History(Ira, (Monday, 1000m, 1000m), (Tuesday, 1010m, 0m), (Wednesday, 999.9m, 0m));
            var books = PortfolioBooks.Build(Joe10, Tree(Edge(Joe10, Household), Edge(Ira, Joe10)), Accounts(Ira), [ira]);
            Assert.Equal(ira.Days.Select(day => (day.Date, day.Value, day.Flow, day.Return)), books.Days.Select(day => (day.Date, day.Value, day.Flow, day.Return)));
            Assert.Equal(ira.Statistics with { AccountId = Joe10 }, books.Statistics with { Gaps = ira.Statistics.Gaps });
        }

        [Fact]
        public void TwoAccountsAddUpAndATransferBetweenThemNetsOut()
        {
            // Tuesday: both gain 1%, and 100 moves from the IRA to the Roth at the close.
            var ira = History(Ira, (Monday, 1000m, 1000m), (Tuesday, 910m, -100m));
            var roth = History(Roth, (Monday, 500m, 500m), (Tuesday, 605m, 100m));
            var books = PortfolioBooks.Build(Joe10, Tree(Edge(Joe10, Household), Edge(Ira, Joe10), Edge(Roth, Joe10)), Accounts(Ira, Roth), [ira, roth]);
            Assert.Equal(1500m, books.Days[0].Value);
            Assert.Equal(1515m, books.Days[1].Value);
            Assert.Equal(0m, books.Days[1].Flow);
            Assert.Equal(0.01m, books.Days[1].Return);
        }

        [Fact]
        public void AParentCountsTheAccountsInItsChildren()
        {
            var ira = History(Ira, (Monday, 1000m, 1000m), (Tuesday, 1100m, 0m));
            var roth = History(Roth, (Monday, 1000m, 1000m), (Tuesday, 1000m, 0m));
            var tree = Tree(Edge(Retirement, Household), Edge(Joe10, Retirement), Edge(Picks, Retirement), Edge(Ira, Joe10), Edge(Roth, Picks));
            var retirement = PortfolioBooks.Build(Retirement, tree, Accounts(Ira, Roth), [ira, roth]);
            Assert.Equal(2100m, retirement.Days[1].Value);
            Assert.Equal(0.05m, retirement.Days[1].Return);
            Assert.Equal(0.1m, PortfolioBooks.Build(Joe10, tree, Accounts(Ira, Roth), [ira, roth]).Days[1].Return);
            Assert.DoesNotContain(PortfolioBooks.Build(Household, tree, Accounts(Ira, Roth), [ira, roth]).Days, day => day.Value is null);
        }

        /// <summary>
        /// The Roth moves from Picks to the Joe 10 at Tuesday's close.  Each portfolio's history before the move is left as it
        /// was; the move counts as a withdrawal from Picks and a deposit into the Joe 10, so neither return moves because of it.
        /// </summary>
        [Fact]
        public void AMoveIsAWithdrawalFromOnePortfolioAndADepositIntoTheOtherAndRewritesNothing()
        {
            var ira = History(Ira, (Monday, 1000m, 1000m), (Tuesday, 1000m, 0m), (Wednesday, 1020m, 0m), (Thursday, 1020m, 0m));
            var roth = History(Roth, (Monday, 1000m, 1000m), (Tuesday, 1050m, 0m), (Wednesday, 1050m, 0m), (Thursday, 1102.5m, 0m));
            var edges = new[] { Edge(Joe10, Household), Edge(Picks, Household), Edge(Ira, Joe10), Edge(Roth, Joe10) };
            var move = new NodeMovedEvent { NodeId = Roth, FromParentNodeId = Picks, ToParentNodeId = Joe10, Date = Tuesday };
            var tree = new TreeOverTime(edges, [move]);

            var picks = PortfolioBooks.Build(Picks, tree, Accounts(Ira, Roth), [ira, roth]);
            Assert.Equal(new[] { Monday, Tuesday }, picks.Days.Select(day => day.Date));
            Assert.Equal(0.05m, picks.Days[1].Return);

            var joe10 = PortfolioBooks.Build(Joe10, tree, Accounts(Ira, Roth), [ira, roth]);
            Assert.Equal(new[] { 1000m, 1000m, 2070m, 2122.5m }, joe10.Days.Select(day => day.Value!.Value));
            Assert.Equal(1050m, joe10.Days[2].Flow);
            Assert.Equal(0.009756m, Math.Round(joe10.Days[2].Return!.Value, 6));    // 2070 ÷ (1000 + 1050) − 1: the IRA's 2% on its share
            Assert.Equal(0.025362m, Math.Round(joe10.Days[3].Return!.Value, 6));

            // The same accounts with no move recorded: the Roth has been in the Joe 10 all along.
            var withoutMove = PortfolioBooks.Build(Joe10, new TreeOverTime(edges, []), Accounts(Ira, Roth), [ira, roth]);
            Assert.Equal(2000m, withoutMove.Days[0].Value);
        }

        [Fact]
        public void AnAccountOpenedInThePortfolioBringsOnlyItsDeposit()
        {
            var ira = History(Ira, (Monday, 1000m, 1000m), (Tuesday, 1010m, 0m), (Wednesday, 1010m, 0m));
            var roth = History(Roth, (Tuesday, 500m, 500m), (Wednesday, 505m, 0m));
            var books = PortfolioBooks.Build(Joe10, Tree(Edge(Joe10, Household), Edge(Ira, Joe10), Edge(Roth, Joe10)), Accounts(Ira, Roth), [ira, roth]);
            Assert.Equal(500m, books.Days[1].Flow);
            Assert.Equal(0.01m, books.Days[1].Return);
            Assert.Equal(0.003311m, Math.Round(books.Days[2].Return!.Value, 6));
            Assert.Empty(books.Statistics.Gaps);
        }

        private static TreeOverTime Tree(params EdgeFact[] edges) => new TreeOverTime(edges, []);

        private static EdgeFact Edge(Guid child, Guid parent) =>
            new EdgeFact(new EdgeDto { ChildNodeId = child, ParentNodeId = parent, Position = 0, Since = Monday });

        private static IEnumerable<AccountFact> Accounts(params Guid[] ids) =>
            ids.Select(id => new AccountFact(new AccountDto { AccountId = id, AccountType = AccountType.Brokerage, Currency = CurrencyCode.UnitedStatesDollar }));

        /// <summary>An account's days from (date, value, flow), with returns measured as the account history does.</summary>
        private static AccountHistory History(Guid accountId, params (DateOnly Date, decimal Value, decimal Flow)[] days)
        {
            var list = new List<AccountDay>();
            decimal? previous = null;
            foreach (var (date, value, flow) in days)
            {
                list.Add(new AccountDay(date, value, flow, previous is > 0m ? Math.Round(((value - flow) / previous.Value) - 1m, 10) : null, null));
                previous = value;
            }

            return new AccountHistory(accountId, list, AccountHistoryFold.Statistics(accountId, list, []), []);
        }
    }
}
