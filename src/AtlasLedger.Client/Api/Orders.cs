// <copyright file="Orders.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Api
{
    using System;
    using System.Collections.Generic;
    using AtlasLedger.Model;

    /// <summary>
    /// A broker order's state, as the rules derived it from the broker's reports.
    /// </summary>
    /// <param name="BrokerOrderId">The broker order.</param>
    /// <param name="Quantity">The quantity sent.</param>
    /// <param name="Status">Pending, Working, PartiallyFilled, Filled, Cancelled, Rejected or Expired.</param>
    /// <param name="Filled">The quantity filled.</param>
    /// <param name="AveragePrice">The average fill price, if anything filled.</param>
    /// <param name="Fees">The fees reported.</param>
    /// <param name="Open">The quantity still working at the broker.</param>
    public sealed record BrokerOrderState(Guid BrokerOrderId, decimal Quantity, string Status, decimal Filled, decimal? AveragePrice, decimal Fees, decimal Open);

    /// <summary>
    /// An allocation's state: how much has been booked to the account.
    /// </summary>
    /// <param name="AccountId">The account.</param>
    /// <param name="Quantity">The quantity allocated.</param>
    /// <param name="Filled">The quantity filled and booked to the account.</param>
    /// <param name="Remaining">The quantity still to fill.</param>
    /// <param name="Fees">The fees booked to the account.</param>
    public sealed record AllocationState(Guid AccountId, decimal Quantity, decimal Filled, decimal Remaining, decimal Fees);

    /// <summary>
    /// An order's state, as the rules derived it.
    /// </summary>
    /// <param name="OrderId">The order.</param>
    /// <param name="AssetId">The asset.</param>
    /// <param name="Side">Buy or sell.</param>
    /// <param name="Origin">What created it.</param>
    /// <param name="Quantity">The sum of its allocations.</param>
    /// <param name="Placed">The quantity at the broker: working, or filled.</param>
    /// <param name="Unplaced">The quantity not yet at a broker.</param>
    /// <param name="Filled">The quantity filled.</param>
    /// <param name="Unallocated">Filled beyond what the allocations needed.</param>
    /// <param name="Fees">The fees reported, as one amount.</param>
    /// <param name="Status">New, Working, PartiallyFilled or Filled.</param>
    /// <param name="Allocations">Its allocations' states.</param>
    /// <param name="BrokerOrders">Its broker orders' states.</param>
    public sealed record OrderState(
        Guid OrderId,
        Guid AssetId,
        OrderSide Side,
        OrderOrigin Origin,
        decimal Quantity,
        decimal Placed,
        decimal Unplaced,
        decimal Filled,
        decimal Unallocated,
        decimal Fees,
        string Status,
        IReadOnlyList<AllocationState> Allocations,
        IReadOnlyList<BrokerOrderState> BrokerOrders);

    /// <summary>
    /// A tenant's orders as the rules last worked them out.
    /// </summary>
    /// <param name="AsOf">When the rules last ran.</param>
    /// <param name="Orders">Every order's state.</param>
    public sealed record TenantOrders(DateTimeOffset AsOf, IReadOnlyList<OrderState> Orders);
}
