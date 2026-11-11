// <copyright file="OrdersController.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Orders
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using AtlasLedger.Model.Primary;
    using AtlasLedger.Server.Holdings;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;

    /// <summary>
    /// A tenant's orders as the rules last worked them out.
    /// </summary>
    /// <param name="AsOf">When the rules last ran.</param>
    /// <param name="Orders">Every order's state, by order ID.</param>
    /// <param name="Proposals">Every account's proposed allocations: what its model calls for now, not yet ordered.</param>
    public sealed record OrdersDto(DateTimeOffset AsOf, IReadOnlyList<OrderState> Orders, IReadOnlyList<ProposedAllocations> Proposals);

    /// <summary>
    /// Serves the state of a tenant's orders: the rules' published snapshot, never the ledger's tables (the orders, broker
    /// orders and execution reports themselves are read through their tables' own endpoints).
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("v1/tenants/{tenantId:guid}/orders")]
    public sealed class OrdersController : ControllerBase
    {
        private readonly HoldingsStore store;

        private readonly OrderGenerator generator;

        /// <summary>
        /// Initializes a new instance of the <see cref="OrdersController"/> class.
        /// </summary>
        /// <param name="store">The rules' snapshots.</param>
        /// <param name="generator">The order generator.</param>
        public OrdersController(HoldingsStore store, OrderGenerator generator)
        {
            this.store = store;
            this.generator = generator;
        }

        /// <summary>
        /// Turns the tenant's proposed allocations into orders now, as the scheduled run would.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <returns>What was written, or 409 with the reason nothing could be.</returns>
        [HttpPost("generate")]
        [Authorize(Policy = LedgerAccessPolicies.OrderWrite)]
        public ActionResult<GeneratedOrders> Generate(Guid tenantId)
        {
            var result = this.generator.Generate(tenantId);
            return result.Reason is { } reason
                ? this.Problem(statusCode: StatusCodes.Status409Conflict, title: "No orders written.", detail: reason)
                : this.Ok(result);
        }

        /// <summary>
        /// Gets the state of the tenant's orders.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <returns>The orders, or 409 when the rules engine hasn't run for the tenant.</returns>
        [HttpGet]
        [Authorize(Policy = LedgerAccessPolicies.OrderRead)]
        public ActionResult<OrdersDto> Get(Guid tenantId)
        {
            if (this.store.Find(tenantId) is not { } snapshot)
            {
                return this.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "No orders yet.",
                    detail: "The rules engine hasn't run for this tenant since it was switched on. Switch it on (services on rules) and try again.");
            }

            return this.Ok(new OrdersDto(snapshot.AsOf, snapshot.Orders.OrderBy(order => order.OrderId).ToArray(), snapshot.Proposals.OrderBy(proposal => proposal.AccountId).ToArray()));
        }
    }
}
