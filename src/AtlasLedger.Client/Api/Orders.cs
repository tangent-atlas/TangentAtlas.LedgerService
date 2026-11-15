// <copyright file="Orders.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Api
{
    using System;
    using System.Collections.Generic;
    using AtlasLedger.Model;

    /// <summary>One leg of a broker order.</summary>
    /// <param name="LegId">The order's leg.</param>
    /// <param name="Quantity">How much this broker order carries.</param>
    /// <param name="Filled">How much has filled.</param>
    /// <param name="AveragePrice">The average fill price, if any filled.</param>
    /// <param name="Open">How much is still working.</param>
    public sealed record BrokerLegState(int LegId, decimal Quantity, decimal Filled, decimal? AveragePrice, decimal Open);

    /// <summary>A broker order's state.</summary>
    /// <param name="BrokerOrderId">The broker order.</param>
    /// <param name="AccountId">The account it trades.</param>
    /// <param name="Status">Pending, Working, PartiallyFilled, Filled, Cancelled, Rejected or Expired.</param>
    /// <param name="Legs">Its legs.</param>
    /// <param name="Fees">The fees reported.</param>
    /// <param name="IsOpen">Whether it is still working.</param>
    public sealed record BrokerOrderState(Guid BrokerOrderId, Guid AccountId, string Status, IReadOnlyList<BrokerLegState> Legs, decimal Fees, bool IsOpen);

    /// <summary>One account's share of a leg.</summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="Quantity">Its share.</param>
    /// <param name="Filled">How much has filled.</param>
    /// <param name="Remaining">How much hasn't.</param>
    /// <param name="Fees">Its fees.</param>
    public sealed record AllocationState(Guid AccountId, decimal Quantity, decimal Filled, decimal Remaining, decimal Fees);

    /// <summary>One leg of an order.</summary>
    /// <param name="LegId">Its number.</param>
    /// <param name="AssetId">The asset.</param>
    /// <param name="Instruction">What it does.</param>
    /// <param name="Side">Buy or sell.</param>
    /// <param name="Quantity">The sum of its allocations.</param>
    /// <param name="Placed">Working or filled at brokers.</param>
    /// <param name="Unplaced">Not.</param>
    /// <param name="Filled">Filled.</param>
    /// <param name="Unallocated">Fills beyond what the allocations need.</param>
    /// <param name="Fees">Its fees.</param>
    /// <param name="Allocations">Each account's share.</param>
    public sealed record LegState(int LegId, Guid AssetId, Instruction Instruction, OrderSide Side, decimal Quantity, decimal Placed, decimal Unplaced, decimal Filled, decimal Unallocated, decimal Fees, IReadOnlyList<AllocationState> Allocations);

    /// <summary>An order's state.</summary>
    /// <param name="OrderId">The order.</param>
    /// <param name="ChainId">The first order of its replace chain.</param>
    /// <param name="Replaces">The order it replaced.</param>
    /// <param name="ReplacedBy">The order that replaced it.</param>
    /// <param name="Origin">What created it.</param>
    /// <param name="Link">How it is linked to other orders.</param>
    /// <param name="Status">New, Working, PartiallyFilled, Filled, Replaced or Cancelled.</param>
    /// <param name="IsCancelRequested">Whether a cancel was asked for.</param>
    /// <param name="Fees">Its fees.</param>
    /// <param name="Legs">Its legs.</param>
    /// <param name="BrokerOrders">Its broker orders.</param>
    /// <param name="IsWorking">Whether anything is working at a broker.</param>
    /// <param name="IsFinished">Whether it is filled, replaced or cancelled.</param>
    public sealed record OrderState(
        Guid OrderId,
        Guid ChainId,
        Guid? Replaces,
        Guid? ReplacedBy,
        OrderOrigin Origin,
        OrderLink? Link,
        string Status,
        bool IsCancelRequested,
        decimal Fees,
        IReadOnlyList<LegState> Legs,
        IReadOnlyList<BrokerOrderState> BrokerOrders,
        bool IsWorking,
        bool IsFinished);

    /// <summary>
    /// One trade an account's model calls for now.
    /// </summary>
    /// <param name="AssetId">The asset.</param>
    /// <param name="Side">Buy or sell.</param>
    /// <param name="Quantity">How many units.</param>
    public sealed record PlannedTrade(Guid AssetId, OrderSide Side, decimal Quantity);

    /// <summary>
    /// An account's proposed allocations: what its model calls for now, not yet ordered.
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="ModelId">Its model.</param>
    /// <param name="Trades">The trades.</param>
    /// <param name="IsWaiting">True when a price is missing or stale, so no trade can be proposed yet.</param>
    public sealed record ProposedAllocations(Guid AccountId, Guid ModelId, IReadOnlyList<PlannedTrade> Trades, bool IsWaiting);

    /// <summary>
    /// What one run of the order generator wrote.
    /// </summary>
    /// <param name="Orders">Orders written.</param>
    /// <param name="BrokerOrders">Broker orders written, one per account in each order.</param>
    /// <param name="Waiting">Accounts left out because a price was missing or stale.</param>
    /// <param name="Reason">Why nothing was written, when nothing could be.</param>
    public sealed record GeneratedOrders(int Orders, int BrokerOrders, int Waiting, string? Reason);

    /// <summary>
    /// A tenant's orders as the rules last worked them out.
    /// </summary>
    /// <param name="AsOf">When the rules last ran.</param>
    /// <param name="Orders">Every order's state.</param>
    /// <param name="Proposals">Every account's proposed allocations.</param>
    public sealed record TenantOrders(DateTimeOffset AsOf, IReadOnlyList<OrderState> Orders, IReadOnlyList<ProposedAllocations> Proposals);
}
