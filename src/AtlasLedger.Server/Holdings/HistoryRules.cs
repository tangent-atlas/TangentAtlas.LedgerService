// <copyright file="HistoryRules.cs" company="Tangent Atlas">
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
    using AtlasLedger.Server.Events;
    using AtlasLedger.Server.Jobs;
    using Bellerophon.Rules;
    using NRules.Fluent.Dsl;

    /// <summary>
    /// An account's history and statistics: for each account held somewhere, its posted lines folded day by day at the
    /// closes loaded, with the risk-free rate.  Recomputed whenever its lines, the closes, the rates or the corporate actions
    /// change (about once a day); live ticks don't touch it.
    /// </summary>
    [Name("Holdings.AccountHistory")]
    public sealed class AccountHistoryRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            AccountFact account = default!;
            IEnumerable<PostedLine> lines = default!;
            IEnumerable<InstitutionFact> institutions = default!;
            IEnumerable<AssetFact> assets = default!;
            IEnumerable<SymbolFact> symbols = default!;
            IEnumerable<ClosingPriceFact> closes = default!;
            IEnumerable<InterestRateFact> rates = default!;
            IEnumerable<DividendFact> dividends = default!;
            IEnumerable<SplitFact> splits = default!;
            this.When()
                .Match(() => account, candidate => candidate.InstitutionId != null)
                .Query(() => lines, query => query.Match<PostedLines>().SelectMany(posted => posted.Lines).Where(line => line.AccountId == account.AccountId).Collect())
                .Query(() => institutions, query => query.Match<InstitutionFact>().Collect())
                .Query(() => assets, query => query.Match<AssetFact>().Collect())
                .Query(() => symbols, query => query.Match<SymbolFact>().Collect())
                .Query(() => closes, query => query.Match<ClosingPriceFact>().Collect())
                .Query(() => rates, query => query.Match<InterestRateFact>().Collect())
                .Query(() => dividends, query => query.Match<DividendFact>().Collect())
                .Query(() => splits, query => query.Match<SplitFact>().Collect());
            this.Then()
                .Yield(_ => AccountHistoryFold.Build(
                    account,
                    institutions.Any(institution => institution.InstitutionId == account.InstitutionId && institution.Source == EventSource.Simulator),
                    lines,
                    assets,
                    symbols,
                    closes,
                    rates,
                    dividends,
                    splits));
        }
    }

    /// <summary>
    /// Credits a paper account the corporate actions it is owed, once their date has come: the paper broker's dividends and
    /// splits, as events in the log.  The event's key is made from the action, so it is recorded once.
    /// </summary>
    [Name("Holdings.CreditCorporateActions")]
    public sealed class CreditCorporateActionsRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            ClockFact clock = default!;
            AccountHistory history = default!;
            LedgerRuleActions actions = default!;
            this.Dependency().Resolve(() => actions);
            this.When()
                .Match(() => clock)
                .Match(() => history, candidate => candidate.Due.Count > 0);
            this.Then()
                .Do(_ => Credit(actions, history, clock.Now));
        }

        /// <summary>
        /// Records the events for the actions whose date has come.
        /// </summary>
        /// <param name="actions">Where the rules' decisions go.</param>
        /// <param name="history">The account's history.</param>
        /// <param name="now">The time now.</param>
        public static void Credit(LedgerRuleActions actions, AccountHistory history, DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(actions);
            ArgumentNullException.ThrowIfNull(history);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, MarketCalendar.Zone).DateTime);
            foreach (var due in history.Due.Where(item => item.Date <= today))
            {
                if (AccountHistoryFold.Event(due) is not { } value)
                {
                    continue;
                }

                var row = EventRows.Row(value, AccountHistoryFold.EventId(due.SourceId), EventSource.Simulator, due.SourceId, AccountHistoryFold.OpenTime(due.Date), enteredTime: now);
                actions.Add(transaction => transaction.InsertEvent(row));
            }
        }
    }
}
