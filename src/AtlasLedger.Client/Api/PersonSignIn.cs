// <copyright file="PersonSignIn.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Api
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Identity.Client;
    using Microsoft.Identity.Client.Extensions.Msal;

    /// <summary>
    /// Signs a person in to Entra External ID through the browser and gets their access tokens for the Ledger API, with the
    /// delegated permissions they hold (<c>scp</c>).  The sign-in is remembered between runs in an encrypted cache, so the
    /// browser opens only the first time, or when the remembered sign-in expires.
    /// </summary>
    public sealed class PersonSignIn
    {
        private readonly IPublicClientApplication application;

        private readonly string[] scopes;

        private readonly string cacheDirectory;

        private readonly string cacheName;

        private bool cacheRegistered;

        /// <summary>
        /// Initializes a new instance of the <see cref="PersonSignIn"/> class.
        /// </summary>
        /// <param name="authority">The external tenant's sign-in address.</param>
        /// <param name="clientId">The signing-in program's client ID.</param>
        /// <param name="scopes">The delegated permissions to ask for, as full names (<c>api://&lt;client ID&gt;/Ledger.Read</c>).</param>
        /// <param name="cacheName">The name of the file that remembers the sign-in, under <c>%LOCALAPPDATA%\TangentAtlas</c>.</param>
        public PersonSignIn(string authority, string clientId, string[] scopes, string cacheName)
        {
            this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
            this.cacheName = cacheName;
            this.cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TangentAtlas");
            this.application = PublicClientApplicationBuilder.Create(clientId)
                .WithAuthority(authority)
                .WithRedirectUri("http://localhost")
                .Build();
        }

        /// <summary>
        /// Gets an access token: silently from the remembered sign-in when there is one, otherwise by opening the browser.
        /// </summary>
        /// <param name="cancellationToken">Cancels the sign-in.</param>
        /// <returns>The token.</returns>
        public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            await this.RegisterCacheAsync().ConfigureAwait(false);
            var account = (await this.application.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
            if (account is not null)
            {
                try
                {
                    return (await this.application.AcquireTokenSilent(this.scopes, account).ExecuteAsync(cancellationToken).ConfigureAwait(false)).AccessToken;
                }
                catch (MsalUiRequiredException)
                {
                    // The remembered sign-in has expired or lacks a permission: sign in again below.
                }
            }

            var result = await this.application.AcquireTokenInteractive(this.scopes)
                .WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
            return result.AccessToken;
        }

        /// <summary>
        /// Gets who is signed in, as the server identifies them: the token's issuer and object ID (<c>iss</c> and
        /// <c>oid</c>), the pair a tenant's user row binds to.
        /// </summary>
        /// <param name="cancellationToken">Cancels the sign-in.</param>
        /// <returns>The issuer and the subject.</returns>
        public async Task<(string Issuer, string Subject)> GetIdentityAsync(CancellationToken cancellationToken)
        {
            var token = await this.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            return ReadIdentity(token);
        }

        /// <summary>
        /// Forgets the remembered sign-in, so the next run opens the browser.
        /// </summary>
        /// <returns>A task that completes when the sign-in is forgotten.</returns>
        public async Task SignOutAsync()
        {
            await this.RegisterCacheAsync().ConfigureAwait(false);
            foreach (var account in await this.application.GetAccountsAsync().ConfigureAwait(false))
            {
                await this.application.RemoveAsync(account).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Reads the issuer and object ID from an access token's claims.  The token is not validated here: the server
        /// validates every token it receives.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <returns>The issuer and the subject.</returns>
        public static (string Issuer, string Subject) ReadIdentity(string token)
        {
            ArgumentNullException.ThrowIfNull(token);
            var parts = token.Split('.');
            if (parts.Length < 2)
            {
                throw new FormatException("The access token is not a JSON web token.");
            }

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using var claims = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            var issuer = claims.RootElement.TryGetProperty("iss", out var iss) ? iss.GetString() : null;
            var subject = claims.RootElement.TryGetProperty("oid", out var oid) ? oid.GetString() : null;
            if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(subject))
            {
                throw new FormatException("The access token has no issuer (iss) or object ID (oid).");
            }

            return (issuer, subject);
        }

        /// <summary>
        /// Keeps the sign-in in a file encrypted for this user (Windows data protection), once per run.
        /// </summary>
        private async Task RegisterCacheAsync()
        {
            if (this.cacheRegistered)
            {
                return;
            }

            Directory.CreateDirectory(this.cacheDirectory);
            var properties = new StorageCreationPropertiesBuilder(this.cacheName, this.cacheDirectory).Build();
            var helper = await MsalCacheHelper.CreateAsync(properties).ConfigureAwait(false);
            helper.RegisterCache(this.application.UserTokenCache);
            this.cacheRegistered = true;
        }
    }
}
