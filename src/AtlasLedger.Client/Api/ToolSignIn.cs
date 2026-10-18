// <copyright file="ToolSignIn.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Api
{
    using System;
    using System.Security.Cryptography.X509Certificates;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Identity.Client;

    /// <summary>
    /// Gets the tool's access tokens from Entra External ID with the client credentials flow: the tool signs in as itself,
    /// proving who it is with its certificate, so it runs unattended from command lines and scripts.
    /// </summary>
    /// <remarks>
    /// The token is an application's (<c>idtyp</c> = <c>app</c>) carrying the Ledger API's application permissions; the
    /// server lets it act in any tenant only because its client ID is in <c>Tools:ClientIds</c>.  MSAL keeps the token in
    /// memory and asks for a new one shortly before it expires.
    /// </remarks>
    public sealed class ToolSignIn
    {
        private readonly IConfidentialClientApplication application;

        private readonly string[] scopes;

        /// <summary>
        /// Initializes a new instance of the <see cref="ToolSignIn"/> class.
        /// </summary>
        /// <param name="settings">The tenant, the tool's registration, the API, and the certificate.</param>
        public ToolSignIn(ToolSignInSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            this.scopes = new[] { settings.Scope };
            this.application = ConfidentialClientApplicationBuilder.Create(settings.ClientId)
                .WithAuthority(settings.Authority)
                .WithCertificate(FindCertificate(settings.CertificateThumbprint))
                .Build();
        }

        /// <summary>
        /// Gets an access token for the Ledger API.
        /// </summary>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The token.</returns>
        public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            var result = await this.application.AcquireTokenForClient(this.scopes).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return result.AccessToken;
        }

        /// <summary>
        /// Finds the tool's certificate, with its private key, in the current user's personal store.
        /// </summary>
        private static X509Certificate2 FindCertificate(string thumbprint)
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            if (found.Count == 0 || !found[0].HasPrivateKey)
            {
                throw new InvalidOperationException($"The tool's certificate ({thumbprint}) is not in this user's personal certificate store with its private key. Run TangentAtlas.Platform's Deploy-EntraApplications.ps1 on this computer.");
            }

            return found[0];
        }
    }
}
