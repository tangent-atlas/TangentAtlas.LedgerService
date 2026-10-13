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
}
