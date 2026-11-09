// <copyright file="Enums.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Model
{
    /// <summary>
    /// The kinds of account.
    /// </summary>
    public enum AccountType
    {
        /// <summary>An account that groups other accounts.</summary>
        GroupAccount,

        /// <summary>An account managed against a model.</summary>
        ManagedAccount,

        /// <summary>An account whose members are weighted.</summary>
        WeightedAccount,
    }

    /// <summary>
    /// The lifecycle status of a node.
    /// </summary>
    public enum NodeStatus
    {
        /// <summary>The node is active.</summary>
        Active,

        // The remaining members are not known yet; add them here.
    }

    /// <summary>
    /// The kinds of node in the hierarchy.
    /// </summary>
    public enum NodeType
    {
        /// <summary>An account.</summary>
        Account,

        /// <summary>A person.</summary>
        Person,

        /// <summary>A security: a stock or a fund, named by its symbols.</summary>
        Security,
    }

    /// <summary>
    /// The kinds of user.  To be reviewed: these members described users as nodes, before users became principals.
    /// </summary>
    public enum UserType
    {
        /// <summary>An administrator.</summary>
        Administrator,

        /// <summary>A group.</summary>
        Group,

        /// <summary>An individual.</summary>
        Individual,
    }
    /// <summary>
    /// The kinds of cash entry.
    /// </summary>
    public enum CashEntryKind
    {
        /// <summary>Money paid into the account.</summary>
        Deposit,

        /// <summary>Money paid out of the account.</summary>
        Withdrawal,

        /// <summary>A dividend paid in cash.</summary>
        Dividend,

        /// <summary>Interest paid on cash.</summary>
        Interest,

        /// <summary>A fee charged to the account.</summary>
        Fee,

        /// <summary>The cash in the account where the source's history begins.</summary>
        OpeningBalance,
    }

    /// <summary>
    /// Where an event came from.
    /// </summary>
    public enum EventSource
    {
        /// <summary>Charles Schwab, through its API.</summary>
        Schwab,

        /// <summary>The data aggregator, Plaid.</summary>
        Plaid,

        /// <summary>An operator, by hand.</summary>
        Operator,

        /// <summary>A simulator or a test script.</summary>
        Simulator,

        /// <summary>Our own rules engine.</summary>
        Rules,
    }

    /// <summary>
    /// The kinds of security.
    /// </summary>
    public enum SecurityType
    {
        /// <summary>A company's common stock.</summary>
        CommonStock,

        /// <summary>An exchange-traded fund.</summary>
        ExchangeTradedFund,
    }

    /// <summary>
    /// The side of a trade.
    /// </summary>
    public enum TradeSide
    {
        /// <summary>A purchase.</summary>
        Buy,

        /// <summary>A sale.</summary>
        Sell,
    }
}
