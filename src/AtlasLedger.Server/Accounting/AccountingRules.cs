// <copyright file="AccountingRules.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Accounting
{
    using System.Collections.Generic;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using AtlasLedger.Server.Holdings;
    using NRules.Fluent.Dsl;

    /// <summary>
    /// Each portfolio's history and statistics (<see cref="PortfolioBooks"/>), recomputed whenever an account's history, the
    /// tree or a move changes (about once a day); live ticks don't touch it.
    /// </summary>
    [Name("Accounting.PortfolioHistory")]
    public sealed class PortfolioHistoryRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            PortfolioFact portfolio = default!;
            IEnumerable<EdgeFact> edges = default!;
            IEnumerable<NodeMovedEvent> moves = default!;
            IEnumerable<AccountFact> accounts = default!;
            IEnumerable<AccountHistory> histories = default!;
            this.When()
                .Match(() => portfolio)
                .Query(() => edges, query => query.Match<EdgeFact>().Collect())
                .Query(() => moves, query => query.Match<NodeMovedEvent>().Collect())
                .Query(() => accounts, query => query.Match<AccountFact>().Collect())
                .Query(() => histories, query => query.Match<AccountHistory>().Collect());
            this.Then()
                .Yield(_ => PortfolioBooks.Build(portfolio.PortfolioId, new TreeOverTime(edges, moves), accounts, histories));
        }
    }
}
