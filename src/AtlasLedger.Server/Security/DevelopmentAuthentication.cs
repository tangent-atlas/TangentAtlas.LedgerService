// <copyright file="DevelopmentAuthentication.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Security
{
    using System.Security.Claims;
    using System.Text.Encodings.Web;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Authentication;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Signs callers in during development, from a header naming who they are, with the same claims the identity provider
    /// issues in production.
    /// </summary>
    /// <remarks>
    /// Production replaces this with bearer tokens from the identity provider; the generated access policies and tenant
    /// membership read the same claims either way, so nothing else changes.  The server refuses to start outside the
    /// Development environment rather than fall back to this.
    /// </remarks>
    internal sealed class DevelopmentAuthentication : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        /// <summary>
        /// The scheme's name.
        /// </summary>
        public const string SchemeName = "Development";

        /// <summary>
        /// The request header that names the caller.
        /// </summary>
        public const string CallerHeader = "Development-Caller";

        /// <summary>
        /// The issuer of the development identities.
        /// </summary>
        public const string Issuer = "https://example.ciamlogin.com/example/v2.0";

        /// <summary>
        /// The object ID of the development advisor.
        /// </summary>
        public const string AdvisorSubject = "11111111-1111-1111-1111-111111111111";

        /// <summary>
        /// The client ID of the development tool.  The server allows it as a tool in Development only.
        /// </summary>
        public const string ToolClientId = "22222222-2222-2222-2222-222222222222";

        /// <summary>
        /// Initializes a new instance of the <see cref="DevelopmentAuthentication"/> class.
        /// </summary>
        /// <param name="options">The scheme's options.</param>
        /// <param name="logger">The logger factory.</param>
        /// <param name="encoder">The URL encoder.</param>
        public DevelopmentAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        /// <inheritdoc/>
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Claim[]? claims = this.Request.Headers[CallerHeader].ToString() switch
            {
                // A person signed in through the client application: delegated scopes and an identity.
                "advisor" => new[] { new Claim("scp", "Ledger.Read Ledger.Write"), new Claim("iss", Issuer), new Claim("oid", AdvisorSubject) },

                // The operator tool acting as itself: an application's token (idtyp and azp, as Entra issues them) with
                // application permissions only, no person.
                "tool" => new[] { new Claim("idtyp", "app"), new Claim("azp", ToolClientId), new Claim("roles", "Ledger.Read"), new Claim("roles", "Ledger.Write"), new Claim("roles", "Users.Read"), new Claim("roles", "Users.Write") },
                _ => null,
            };

            if (claims == null)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
