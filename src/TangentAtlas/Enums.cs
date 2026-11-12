// <copyright file="Enums.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Model
{
    /// <summary>
    /// An account's place in an adviser's tree of accounts.
    /// </summary>
    public enum AccountRole
    {
        /// <summary>An account that groups other accounts.</summary>
        GroupAccount,

        /// <summary>An account managed against a model.</summary>
        ManagedAccount,

        /// <summary>An account whose members are weighted.</summary>
        WeightedAccount,
    }

    /// <summary>
    /// The kinds of account: the first level of the account hierarchy.  Each has an extension table with its more
    /// specific type (only Brokerage so far).
    /// </summary>
    public enum AccountType
    {
        /// <summary>An account at a broker that holds securities and cash: the only kind that is traded.</summary>
        Brokerage,

        /// <summary>An employer's retirement plan (401(k), 403(b), 457): shown, never acted on.</summary>
        RetirementPlan,

        /// <summary>A bank account: checking, savings, money market.</summary>
        Deposit,

        /// <summary>A credit card.</summary>
        CreditCard,

        /// <summary>A loan: mortgage, auto, student.</summary>
        Loan,
    }

    /// <summary>
    /// What a job's schedule row asks for.  Stored as a number: never renumber.
    /// </summary>
    public enum JobStatus
    {
        /// <summary>Off: it doesn't run on its own, whatever the schedule says (a task can still be run by hand).</summary>
        Off = 0,

        /// <summary>On: a service runs now, until the status changes.  Not for a task: run it by hand instead.</summary>
        On = 1,

        /// <summary>Scheduled: a task runs when its cron says; a service is on from its start cron to its stop cron.</summary>
        Scheduled = 2,
    }

    /// <summary>
    /// Why a job ran.  Stored as a number: never renumber.
    /// </summary>
    public enum JobRunReason
    {
        /// <summary>Asked for by hand: a Requested row inserted by a person or a tool.</summary>
        Hand = 0,

        /// <summary>A schedule's time came (a task), or its row turned a service on.</summary>
        Schedule = 1,

        /// <summary>A service's window opened, or its row was set inside its window.</summary>
        Window = 2,

        /// <summary>A service that failed or ended while it should be on, started again.</summary>
        Restart = 3,
    }

    /// <summary>
    /// Where a job's run stands.  Stored as a number: never renumber.
    /// </summary>
    public enum JobRunStatus
    {
        /// <summary>Asked for by hand, not yet taken up.</summary>
        Requested = 0,

        /// <summary>Running.</summary>
        Running = 1,

        /// <summary>A task that ran to the end (its outcome says what it did).</summary>
        Succeeded = 2,

        /// <summary>A service that was stopped.</summary>
        Stopped = 3,

        /// <summary>It failed (its outcome says why); a service is started again.</summary>
        Failed = 4,

        /// <summary>Cancelled: its schedule was turned off, changed or deleted, or the server stopped.</summary>
        Cancelled = 5,

        /// <summary>Not run: the job is unknown, a service, off, or already running.</summary>
        Refused = 6,

        /// <summary>The server stopped while it was requested or running: what it did is not confirmed.</summary>
        Interrupted = 7,
    }

    /// <summary>
    /// The more specific types of brokerage account.
    /// </summary>
    public enum BrokerageType
    {
        /// <summary>An ordinary taxable account.</summary>
        Taxable,

        /// <summary>A traditional IRA.</summary>
        TraditionalIra,

        /// <summary>A Roth IRA.</summary>
        RothIra,
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

        /// <summary>An asset: anything that can be held, named by its symbols.</summary>
        Asset,
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
    /// Which way an order trades.
    /// </summary>
    public enum OrderSide
    {
        /// <summary>Buys the asset: the accounts receive it and give cash.</summary>
        Buy,

        /// <summary>Sells the asset: the accounts give it and receive cash.</summary>
        Sell,
    }

    /// <summary>
    /// What created an order: filled in automatically, never typed.
    /// </summary>
    public enum OrderOrigin
    {
        /// <summary>A rebalance: the accounts brought back to their targets.</summary>
        Rebalance,

        /// <summary>A rule other than a rebalance (the rule's name is recorded with the order).</summary>
        Rule,

        /// <summary>An operator, by hand.</summary>
        Manual,
    }

    /// <summary>
    /// How a broker order is to be priced.
    /// </summary>
    public enum OrderType
    {
        /// <summary>At the market's price.</summary>
        Market,

        /// <summary>At the limit price or better.</summary>
        Limit,
    }

    /// <summary>
    /// How long a broker order stays working.
    /// </summary>
    public enum TimeInForce
    {
        /// <summary>Until the close of the day it was placed.</summary>
        Day,

        /// <summary>Until it is filled or cancelled.</summary>
        GoodTillCancelled,
    }

    /// <summary>
    /// What a broker reports about a broker order.
    /// </summary>
    public enum ExecutionReportType
    {
        /// <summary>The broker accepted the order: it is working.</summary>
        Accepted,

        /// <summary>Some or all of the order was executed: a quantity at a price, with fees.</summary>
        Fill,

        /// <summary>The order was cancelled; what was filled stays filled.</summary>
        Cancelled,

        /// <summary>The broker refused the order.</summary>
        Rejected,

        /// <summary>The order's time ran out; what was filled stays filled.</summary>
        Expired,
    }

    /// <summary>
    /// The kinds of asset: the first level of the asset hierarchy. Each has an extension table holding what is particular to it.
    /// </summary>
    public enum AssetType
    {
        /// <summary>A currency.</summary>
        Cash,

        /// <summary>A share in a company.</summary>
        Equity,

        /// <summary>A share in a pooled vehicle.</summary>
        Fund,

        /// <summary>A bond or note.</summary>
        FixedIncome,

        /// <summary>The right to buy or sell an underlying asset at a set price.</summary>
        Option,

        /// <summary>An agreement to buy or sell an underlying asset at a set price on a set date.</summary>
        Future,

        /// <summary>A coin or token on a blockchain.</summary>
        Crypto,
    }

    /// <summary>
    /// The kinds of cash.
    /// </summary>
    public enum CashType
    {
        /// <summary>A national currency, such as the US dollar.</summary>
        Currency,
    }

    /// <summary>
    /// The kinds of equity.
    /// </summary>
    public enum EquityType
    {
        /// <summary>Common stock.</summary>
        CommonStock,

        /// <summary>Preferred stock.</summary>
        PreferredStock,

        /// <summary>A warrant: the issuer's own option on its stock.</summary>
        Warrant,

        /// <summary>A right: a short-lived offer to buy new shares.</summary>
        Right,

        /// <summary>A depositary receipt: a foreign company's shares held by a bank and traded here.</summary>
        DepositaryReceipt,
    }

    /// <summary>
    /// The kinds of fund.
    /// </summary>
    public enum FundType
    {
        /// <summary>An exchange-traded fund.</summary>
        ExchangeTradedFund,

        /// <summary>A mutual fund, bought and sold at its daily net asset value.</summary>
        MutualFund,

        /// <summary>A money market fund, held as a cash equivalent.</summary>
        MoneyMarketFund,

        /// <summary>A closed-end fund, traded on an exchange with a fixed number of shares.</summary>
        ClosedEndFund,
    }

    /// <summary>
    /// The kinds of fixed income.
    /// </summary>
    public enum FixedIncomeType
    {
        /// <summary>A bond that pays no interest until maturity, sold at a discount (a Treasury bill, say).</summary>
        ZeroCoupon,

        /// <summary>A bond that pays interest at a fixed rate.</summary>
        Coupon,

        /// <summary>A bond whose interest rate is reset from a reference rate.</summary>
        FloatingRate,

        /// <summary>A bond backed by a pool of mortgages.</summary>
        MortgageBacked,

        /// <summary>A bond backed by a pool of other loans or receivables.</summary>
        AssetBacked,

        /// <summary>A bond that can be exchanged for the issuer's stock.</summary>
        Convertible,
    }

    /// <summary>
    /// The right an option gives.
    /// </summary>
    public enum OptionRight
    {
        /// <summary>The right to buy.</summary>
        Call,

        /// <summary>The right to sell.</summary>
        Put,
    }

    /// <summary>
    /// When an option can be exercised.
    /// </summary>
    public enum ExerciseStyle
    {
        /// <summary>On any day until it expires.</summary>
        American,

        /// <summary>Only on its expiration date.</summary>
        European,
    }

    /// <summary>
    /// The kinds of future, by what underlies it.
    /// </summary>
    public enum FutureType
    {
        /// <summary>A single company's stock.</summary>
        Equity,

        /// <summary>A stock index.</summary>
        EquityIndex,

        /// <summary>An interest rate or a bond.</summary>
        InterestRate,

        /// <summary>A currency.</summary>
        Currency,

        /// <summary>A commodity.</summary>
        Commodity,
    }

    /// <summary>
    /// The kinds of crypto asset.
    /// </summary>
    public enum CryptoType
    {
        /// <summary>A blockchain's own currency.</summary>
        Coin,

        /// <summary>A token issued on a blockchain.</summary>
        Token,

        /// <summary>A token whose value is tied to a currency.</summary>
        Stablecoin,
    }

    /// <summary>
    /// What a transaction line does to a holding.
    /// </summary>
    public enum LineEffect
    {
        /// <summary>Adds its quantity (negative to remove).</summary>
        Change,

        /// <summary>Sets the holding to its quantity (an override).</summary>
        SetTo,
    }
}
