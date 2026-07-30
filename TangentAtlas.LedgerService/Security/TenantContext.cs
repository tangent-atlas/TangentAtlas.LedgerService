// <copyright file="TenantContext.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
using System.Security.Claims;
using AtlasLedger.Model;
using Microsoft.AspNetCore.Http;

namespace TangentAtlas.LedgerService.Security;

/// <summary>
/// Resolves the calling principal's tenant scope for the current request by reading the live
/// membership map. The validated token supplies identity (authentication); authorization comes from
/// the map, not from claims, so tenant grants and revocations take effect on the next request rather
/// than at token expiry.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    /// <summary>
    /// Claim types that may carry the B2C subject (object) identifier, in order of preference.
    /// </summary>
    private static readonly string[] SubjectClaimTypes =
    [
        "oid",
        "http://schemas.microsoft.com/identity/claims/objectidentifier",
        "sub",
        ClaimTypes.NameIdentifier,
    ];

    /// <summary>
    /// A shared empty set used for operators and for callers with no grants (fail closed).
    /// </summary>
    private static readonly IReadOnlySet<Guid> EmptySet = new HashSet<Guid>();

    /// <summary>
    /// Initializes a new instance of the <see cref="TenantContext"/> class.
    /// </summary>
    /// <param name="httpContextAccessor">Accessor for the current HTTP context.</param>
    /// <param name="tenantMembership">The live user-to-tenant membership map.</param>
    public TenantContext(IHttpContextAccessor httpContextAccessor, ITenantMembership tenantMembership)
    {
        // The only claim we read: the caller's identity, which is the token's proper job.
        ClaimsPrincipal? claimsPrincipal = httpContextAccessor.HttpContext?.User;
        Guid userId = ResolveUserId(claimsPrincipal);

        // Pin the scope once, here, for the life of the request. The membership map publishes
        // immutable sets, so the captured reference stays a consistent snapshot even if the map
        // changes mid-request.
        if (userId != Guid.Empty && tenantMembership.IsOperator(userId))
        {
            this.IsAllTenants = true;
            this.AllowedTenants = EmptySet;
        }
        else
        {
            if (userId != Guid.Empty && tenantMembership.TryGetTenants(userId, out IReadOnlySet<Guid> tenants))
            {
                this.IsAllTenants = false;
                this.AllowedTenants = tenants;
            }
            else
            {
                // Unauthenticated, unparseable subject, or unknown user: see nothing.
                this.IsAllTenants = false;
                this.AllowedTenants = EmptySet;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsAllTenants { get; }

    /// <inheritdoc/>
    public IReadOnlySet<Guid> AllowedTenants { get; }

    /// <inheritdoc/>
    public bool Allows(Guid tenantId) => this.IsAllTenants || this.AllowedTenants.Contains(tenantId);

    /// <summary>
    /// Resolves the caller's user identifier from the validated principal.
    /// </summary>
    /// <param name="principal">The current principal, or <see langword="null"/> if unauthenticated.</param>
    /// <returns>The user identifier, or <see cref="Guid.Empty"/> if none could be resolved.</returns>
    private static Guid ResolveUserId(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return Guid.Empty;
        }

        // TODO: this always returns Guid.Empty regardless of principal, which means
        // every request currently resolves to "unknown user" - tenant scoping fails
        // closed (nobody sees any tenant's data) rather than leaking, but it also
        // means no authenticated user can see their OWN data yet. SubjectClaimTypes
        // above is defined but never consulted here - wire it up, e.g.:
        //   foreach (var claimType in SubjectClaimTypes)
        //   {
        //       var claim = principal.FindFirst(claimType);
        //       if (claim is not null && Guid.TryParse(claim.Value, out var userId))
        //       {
        //           return userId;
        //       }
        //   }
        return Guid.Empty;
    }
}
