// <copyright file="IdentityProviderOptions.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Security
{
    using System;

    /// <summary>
    /// The identity provider whose bearer tokens the server accepts: Microsoft Entra External ID.
    /// </summary>
    /// <remarks>
    /// Set both, or neither.  When the authority is set, every caller signs in with a bearer token from it; when it is not,
    /// the server runs only in the Development environment, with development sign-in.  Both values are identifiers, not
    /// secrets: TangentAtlas.Platform's Deploy-EntraApplications.ps1 writes them into the web app's settings
    /// (<c>IdentityProvider__Authority</c>, <c>IdentityProvider__Audiences__0</c>, ...).
    /// </remarks>
    public sealed class IdentityProviderOptions
    {
        /// <summary>
        /// The settings section.
        /// </summary>
        public const string SectionName = "IdentityProvider";

        /// <summary>
        /// Gets or sets the token issuer, which is also where its signing keys are published:
        /// <c>https://&lt;tenant ID&gt;.ciamlogin.com/&lt;tenant ID&gt;/v2.0</c>.  A token's <c>iss</c> claim must equal it.
        /// </summary>
        public string? Authority { get; set; }

        /// <summary>
        /// Gets or sets the audiences a token may be issued for: the Ledger API registration's client ID, and its
        /// application ID URI (<c>api://&lt;client ID&gt;</c>).
        /// </summary>
        public string[] Audiences { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Gets a value indicating whether an identity provider is configured.
        /// </summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(this.Authority);
    }
}
