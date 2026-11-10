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
    /// The kinds of transaction. An exchange (Buy, Sell) gives one asset for another, so its lines balance; the others move value into, out of or between accounts, or correct a holding.
    /// </summary>
    public enum TransactionKind
    {
        /// <summary>Holdings as they stood where the source's history begins.</summary>
        OpeningBalance,

        /// <summary>An asset paid into an account.</summary>
        Deposit,

        /// <summary>An asset paid out of an account.</summary>
        Withdrawal,

        /// <summary>An exchange: an asset received for another given (cash, usually).</summary>
        Buy,

        /// <summary>An exchange: an asset given for another received (cash, usually).</summary>
        Sell,

        /// <summary>A distribution from a holding.</summary>
        Dividend,

        /// <summary>Interest paid on a holding.</summary>
        Interest,

        /// <summary>A charge to the account.</summary>
        Fee,

        /// <summary>A change in the number of units held, with no change in value.</summary>
        Split,

        /// <summary>An asset moved from one account to another of the same tenant.</summary>
        Transfer,

        /// <summary>A permanent correction by the operator.</summary>
        Adjustment,

        /// <summary>A temporary correction by the operator: it sets holdings until it expires.</summary>
        Override,
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
