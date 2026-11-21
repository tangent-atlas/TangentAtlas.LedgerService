// <copyright file="MeController.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.People
{
    using System;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using Bellerophon.Runtime;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;

    /// <summary>
    /// Who the caller is in a tenant.
    /// </summary>
    /// <param name="UserId">The caller's user row's key: what their own rows (their favorites) are keyed by.</param>
    public sealed record MeDto(Guid UserId);

    /// <summary>
    /// Tells a person their own user key in a tenant, and nothing about anyone else, so a client can find the rows that are
    /// theirs (favorites) without reading the tenant's users.  The ledger already matches the caller to their user row by
    /// their identity (issuer and object ID) for membership; this returns that row's key.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("v1/tenants/{tenantId:guid}/me")]
    public sealed class MeController : ControllerBase
    {
        private readonly Ledger ledger;

        /// <summary>
        /// Initializes a new instance of the <see cref="MeController"/> class.
        /// </summary>
        /// <param name="ledger">The ledger.</param>
        public MeController(Ledger ledger)
        {
            this.ledger = ledger;
        }

        /// <summary>
        /// Gets the caller's user key.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <returns>The key; 404 for a caller with no user row there (a tool, or not a member).</returns>
        [HttpGet]
        [Authorize(Policy = LedgerAccessPolicies.NodeRead)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<MeDto> Get(Guid tenantId)
        {
            if (this.ledger.FindPartition(tenantId) is not { } partition || !IdentityClaims.TryGetIdentity(this.User, out var issuer, out var subject))
            {
                return this.NotFound();
            }

            lock (partition.SyncRoot)
            {
                return partition.FindUserByIdentity(issuer, subject) is { } user ? new MeDto(user.UserId) : this.NotFound();
            }
        }
    }
}
