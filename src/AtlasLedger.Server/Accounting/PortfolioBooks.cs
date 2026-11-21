// <copyright file="PortfolioBooks.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Accounting
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using AtlasLedger.Server.Holdings;
    using AccountDay = AtlasLedger.Server.Holdings.AccountDay;

    /// <summary>
    /// A portfolio's history and statistics: its days (the combined value and flows of the accounts beneath it on each day)
    /// and the statistics measured from them, exactly as an account's are.
    /// </summary>
    /// <param name="PortfolioId">The portfolio.</param>
    /// <param name="Days">Its days, oldest first.</param>
    /// <param name="Statistics">Its statistics (their <c>AccountId</c> is the portfolio's).</param>
    public sealed record PortfolioHistory(Guid PortfolioId, IReadOnlyList<AccountDay> Days, AccountStatistics Statistics);

    /// <summary>
    /// Where a node was in the household's tree on a given day, from its edge and its moves: the history follows the account,
    /// and a portfolio counts an account only for the days it was beneath it.
    /// </summary>
    /// <remarks>
    /// A move at the close of a day leaves the node in its old parent that day and puts it in its new parent from the next.
    /// A node with no moves has been in its current parent all along.  A move with no "from" (the node was just added) means
    /// it was in no parent before.
    /// </remarks>
    public sealed class TreeOverTime
    {
        private readonly Dictionary<Guid, Guid> current;

        private readonly Dictionary<Guid, NodeMovedEvent[]> moves;

        /// <summary>
        /// Initializes a new instance of the <see cref="TreeOverTime"/> class.
        /// </summary>
        /// <param name="edges">The tenant's edges: where each node is now.</param>
        /// <param name="moves">Every move recorded.</param>
        public TreeOverTime(IEnumerable<EdgeFact> edges, IEnumerable<NodeMovedEvent> moves)
        {
            ArgumentNullException.ThrowIfNull(edges);
            ArgumentNullException.ThrowIfNull(moves);
            this.current = edges.ToDictionary(edge => edge.ChildNodeId, edge => edge.ParentNodeId);
            this.moves = moves
                .GroupBy(move => move.NodeId)
                .ToDictionary(group => group.Key, group => group.OrderBy(move => move.Date).ToArray());
        }

        /// <summary>
        /// A node's parent on a day.
        /// </summary>
        /// <param name="nodeId">The node.</param>
        /// <param name="day">The day.</param>
        /// <returns>Its parent that day; null when it had none.</returns>
        public Guid? ParentOn(Guid nodeId, DateOnly day)
        {
            if (!this.moves.TryGetValue(nodeId, out var list))
            {
                return this.current.TryGetValue(nodeId, out var parent) ? parent : null;
            }

            Guid? result = list[0].FromParentNodeId;
            foreach (var move in list)
            {
                if (move.Date < day)
                {
                    result = move.ToParentNodeId;
                }
            }

            return result;
        }

        /// <summary>
        /// Whether a node was beneath a portfolio on a day (anywhere below it, through its parents that day).
        /// </summary>
        /// <param name="nodeId">The node.</param>
        /// <param name="portfolioId">The portfolio.</param>
        /// <param name="day">The day.</param>
        /// <returns>True when it was.</returns>
        public bool IsBeneath(Guid nodeId, Guid portfolioId, DateOnly day)
        {
            var seen = new HashSet<Guid> { nodeId };
            for (var parent = this.ParentOn(nodeId, day); parent is { } id && seen.Add(id); parent = this.ParentOn(id, day))
            {
                if (id == portfolioId)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// A portfolio's books, rough for now: the first piece of the mini-accounting system (<c>portfolio-design.md</c> §4).
    /// Each day the portfolio's value is the sum of the values of the accounts beneath it that day; its flows are theirs,
    /// plus an account's value at the previous close when it joins and less it when it leaves.  Its return is measured on
    /// those, time-weighted like an account's, and its statistics by the same function.  A transfer between two accounts in
    /// the portfolio is −x in one and +x in the other, so it nets out.
    /// </summary>
    /// <remarks>
    /// <para>Not yet (each a later step that replaces a piece of this, not the shape): accounts in different currencies (a gap
    /// for now); money in transit between institutions; the fee; the matched-flow benchmark; money-weighted return; standard
    /// periods; restatement (today the books are simply recomputed from the accounts' days).</para>
    /// <para>The day's return: (value − flows at the close) ÷ (the previous value + what joined at the previous close − what
    /// left) − 1.  An account that joins earns the portfolio's return from the next day, as a deposit made at the close would.</para>
    /// </remarks>
    public static class PortfolioBooks
    {
        /// <summary>
        /// Builds a portfolio's history and statistics from its accounts' days.
        /// </summary>
        /// <param name="portfolioId">The portfolio.</param>
        /// <param name="tree">Where each node was on each day.</param>
        /// <param name="accounts">The tenant's accounts (closed ones too: their past still counts).</param>
        /// <param name="histories">Every account's history.</param>
        /// <returns>The portfolio's history.</returns>
        public static PortfolioHistory Build(Guid portfolioId, TreeOverTime tree, IEnumerable<AccountFact> accounts, IEnumerable<AccountHistory> histories)
        {
            ArgumentNullException.ThrowIfNull(tree);
            ArgumentNullException.ThrowIfNull(accounts);
            ArgumentNullException.ThrowIfNull(histories);
            var currencyOf = accounts.ToDictionary(account => account.AccountId, account => account.Currency);
            var daysOf = histories
                .Where(history => currencyOf.ContainsKey(history.AccountId))
                .ToDictionary(history => history.AccountId, history => history.Days.ToDictionary(day => day.Date));
            var gaps = new SortedSet<string>(StringComparer.Ordinal);

            // An account counts from its first day to its last (opened, then closed), on the days it was beneath the portfolio.
            var spanOf = daysOf.Where(pair => pair.Value.Count > 0).ToDictionary(pair => pair.Key, pair => (First: pair.Value.Keys.Min(), Last: pair.Value.Keys.Max()));
            IReadOnlyList<Guid> MembersOn(DateOnly day) =>
                spanOf.Where(pair => pair.Value.First <= day && day <= pair.Value.Last && tree.IsBeneath(pair.Key, portfolioId, day)).Select(pair => pair.Key).Order().ToArray();

            var dates = daysOf
                .SelectMany(pair => pair.Value.Keys.Where(day => tree.IsBeneath(pair.Key, portfolioId, day)))
                .Distinct()
                .Order()
                .ToArray();
            var result = new List<AccountDay>(dates.Length);
            IReadOnlyList<Guid> previousMembers = [];
            decimal? previousValue = null;
            DateOnly? previousDate = null;
            foreach (var day in dates)
            {
                var members = MembersOn(day);
                if (members.Select(accountId => currencyOf[accountId]).Distinct(StringComparer.Ordinal).Count() > 1)
                {
                    gaps.Add("Its accounts are kept in different currencies: they are added as they are, unconverted.");
                }

                decimal? value = 0m;
                var closeFlow = 0m;
                decimal? riskFree = null;
                foreach (var accountId in members)
                {
                    if (daysOf[accountId].TryGetValue(day, out var accountDay))
                    {
                        value = value is { } sum && accountDay.Value is { } worth ? sum + worth : null;
                        closeFlow += accountDay.Flow;
                        riskFree ??= accountDay.RiskFreeReturn;
                    }
                    else
                    {
                        value = null;
                        gaps.Add("An account in it has no value on some days: those days have no value or return.");
                    }
                }

                // What joined or left at the previous close, at its value then.
                var openFlow = 0m;
                if (previousDate is { } before)
                {
                    foreach (var joined in members.Except(previousMembers))
                    {
                        openFlow += ValueOn(joined, before);
                    }

                    foreach (var left in previousMembers.Except(members))
                    {
                        openFlow -= ValueOn(left, before);
                    }
                }

                var start = previousValue + openFlow;
                decimal? dayReturn = start is > 0m && value is { } today ? ((today - closeFlow) / start.Value) - 1m : null;
                result.Add(new AccountDay(day, value, closeFlow + openFlow, dayReturn is { } r ? Math.Round(r, 10) : null, riskFree));
                previousMembers = members;
                previousValue = value;
                previousDate = day;
            }

            return new PortfolioHistory(portfolioId, result, AccountHistoryFold.Statistics(portfolioId, result, gaps));

            decimal ValueOn(Guid accountId, DateOnly day)
            {
                // An account that didn't exist yet (opened in the portfolio) brings nothing: its deposit is its flow.
                if (!daysOf[accountId].TryGetValue(day, out var accountDay))
                {
                    return 0m;
                }

                if (accountDay.Value is { } worth)
                {
                    return worth;
                }

                gaps.Add("An account joined or left on a day it had no value: counted as 0.");
                return 0m;
            }
        }
    }
}
