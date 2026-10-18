// <copyright file="ToolSignInSettings.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Api
{
    /// <summary>
    /// How the tool signs in to Entra External ID as itself: identifiers only, no secret.
    /// </summary>
    public sealed class ToolSignInSettings
    {
        /// <summary>
        /// Gets or sets the external tenant's sign-in address: <c>https://&lt;tenant ID&gt;.ciamlogin.com/&lt;tenant ID&gt;</c>.
        /// </summary>
        public string Authority { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Ledger tool registration's client ID.
        /// </summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the scope to ask for: the Ledger API's application permissions, <c>api://&lt;API client ID&gt;/.default</c>.
        /// </summary>
        public string Scope { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the thumbprint of the tool's certificate, in the current user's personal certificate store.  Its
        /// private key never leaves this computer; Entra holds only the public half.
        /// </summary>
        public string CertificateThumbprint { get; set; } = string.Empty;
    }
}
