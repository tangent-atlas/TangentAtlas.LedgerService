// <copyright file="ComplianceRules.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Compliance
{
    using System;
    using AtlasLedger.Model.Primary;
    using NRules;
    using NRules.Fluent;
    using NRules.Fluent.Dsl;

    /// <summary>
    /// The compliance rules, written for NRules, and the decisions they record.  These two are the first; the product's rules join them here.
    /// </summary>
    /// <remarks>
    /// Each rule comes in a pair: one raises an alert when the facts break the rule and no alert is open, the other clears
    /// the alert when the facts satisfy the rule again.  A decision comes back as a fact in the next cycle, which removes the
    /// match that caused it, so the rules settle.
    /// </remarks>
    public static class ComplianceRules
    {
        /// <summary>
        /// The scheme of a custodian's account number.
        /// </summary>
        public const string Custodian = "Custodian";

        /// <summary>
        /// The rule that every account has a custodian account number.
        /// </summary>
        public const string MissingCustodianRule = "MissingCustodian";

        /// <summary>
        /// The prefix of the rule that every held symbol's price is positive; the symbol follows it.
        /// </summary>
        public const string InvalidPriceRule = "InvalidPrice";

        /// <summary>
        /// Compiles the rules in this assembly.  Compile once; every tenant's session comes from the result.
        /// </summary>
        /// <returns>The compiled rules.</returns>
        public static ISessionFactory Compile()
        {
            var repository = new RuleRepository();
            repository.Load(spec => spec.From(typeof(ComplianceRules).Assembly));
            return repository.Compile();
        }

        /// <summary>
        /// Gets the name of the invalid price rule for a symbol.
        /// </summary>
        /// <param name="scheme">The symbol's scheme.</param>
        /// <param name="code">The symbol's code.</param>
        /// <returns>The rule name recorded on the symbol's alert.</returns>
        public static string InvalidPriceRuleFor(string scheme, string code) => $"{InvalidPriceRule} {scheme}:{code}";

        /// <summary>
        /// Records a decision to raise an alert.  Rule actions are expression trees, which can't name the transaction, so they
        /// call this ordinary method.
        /// </summary>
        /// <param name="actions">The tenant's rule actions.</param>
        /// <param name="nodeId">The node the alert is about.</param>
        /// <param name="rule">The rule that is broken.</param>
        /// <param name="message">The alert's message.</param>
        internal static void RaiseAlert(LedgerRuleActions actions, Guid nodeId, string rule, string message) =>
            actions.Add(transaction => transaction.InsertAlert(new Alert { AlertId = Guid.NewGuid(), NodeId = nodeId, Rule = rule, Message = message }));

        /// <summary>
        /// Records a decision to clear an alert.
        /// </summary>
        /// <param name="actions">The tenant's rule actions.</param>
        /// <param name="alert">The alert, as the rules saw it.  If it has changed since, the decision is refused and made again.</param>
        internal static void ClearAlert(LedgerRuleActions actions, AlertFact alert)
        {
            var (alertId, rowVersion) = (alert.AlertId, alert.RowVersion);
            actions.Add(transaction => transaction.DeleteAlert(alertId, rowVersion));
        }
    }

    /// <summary>
    /// Raises an alert for an account with no custodian account number.
    /// </summary>
    [Name("MissingCustodian.Raise")]
    public sealed class MissingCustodianRaiseRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            AccountFact account = default!;
            LedgerRuleActions actions = default!;
            this.Dependency().Resolve(() => actions);

            this.When()
                .Match(() => account)
                .Not<SymbolFact>(symbol => symbol.NodeId == account.AccountId, symbol => symbol.Scheme == ComplianceRules.Custodian)
                .Not<AlertFact>(alert => alert.NodeId == account.AccountId, alert => alert.Rule == ComplianceRules.MissingCustodianRule);
            this.Then()
                .Do(_ => ComplianceRules.RaiseAlert(actions, account.AccountId, ComplianceRules.MissingCustodianRule, "The account has no custodian account number."));
        }
    }

    /// <summary>
    /// Clears the alert once the account has a custodian account number.
    /// </summary>
    [Name("MissingCustodian.Clear")]
    public sealed class MissingCustodianClearRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            AccountFact account = default!;
            AlertFact alert = default!;
            LedgerRuleActions actions = default!;
            this.Dependency().Resolve(() => actions);

            this.When()
                .Match(() => account)
                .Exists<SymbolFact>(symbol => symbol.NodeId == account.AccountId, symbol => symbol.Scheme == ComplianceRules.Custodian)
                .Match(() => alert, candidate => candidate.NodeId == account.AccountId, candidate => candidate.Rule == ComplianceRules.MissingCustodianRule);
            this.Then()
                .Do(_ => ComplianceRules.ClearAlert(actions, alert));
        }
    }

    /// <summary>
    /// Raises an alert for a symbol whose latest price is not positive.
    /// </summary>
    [Name("InvalidPrice.Raise")]
    public sealed class InvalidPriceRaiseRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            SymbolFact symbol = default!;
            PriceFact price = default!;
            LedgerRuleActions actions = default!;
            this.Dependency().Resolve(() => actions);

            this.When()
                .Match(() => symbol)
                .Match(() => price, candidate => candidate.Scheme == symbol.Scheme, candidate => candidate.Code == symbol.Code, candidate => candidate.Last <= 0m)
                .Not<AlertFact>(alert => alert.NodeId == symbol.NodeId, alert => alert.Rule == ComplianceRules.InvalidPriceRuleFor(symbol.Scheme, symbol.Code));
            this.Then()
                .Do(_ => ComplianceRules.RaiseAlert(actions, symbol.NodeId, ComplianceRules.InvalidPriceRuleFor(symbol.Scheme, symbol.Code), $"The latest price of {symbol.Code} is {price.Last}."));
        }
    }

    /// <summary>
    /// Clears the alert once the symbol's latest price is positive again.
    /// </summary>
    [Name("InvalidPrice.Clear")]
    public sealed class InvalidPriceClearRule : Rule
    {
        /// <inheritdoc/>
        public override void Define()
        {
            SymbolFact symbol = default!;
            PriceFact price = default!;
            AlertFact alert = default!;
            LedgerRuleActions actions = default!;
            this.Dependency().Resolve(() => actions);

            this.When()
                .Match(() => symbol)
                .Match(() => price, candidate => candidate.Scheme == symbol.Scheme, candidate => candidate.Code == symbol.Code, candidate => candidate.Last > 0m)
                .Match(() => alert, candidate => candidate.NodeId == symbol.NodeId, candidate => candidate.Rule == ComplianceRules.InvalidPriceRuleFor(symbol.Scheme, symbol.Code));
            this.Then()
                .Do(_ => ComplianceRules.ClearAlert(actions, alert));
        }
    }
}
