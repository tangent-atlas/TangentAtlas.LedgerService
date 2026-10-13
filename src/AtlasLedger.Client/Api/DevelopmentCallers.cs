// <copyright file="DevelopmentCallers.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Api
{
    /// <summary>
    /// The callers a development server signs in from a request header, in place of bearer tokens from the identity
    /// provider.
    /// </summary>
    public static class DevelopmentCallers
    {
        /// <summary>
        /// The request header that names the caller.
        /// </summary>
        public const string Header = "Development-Caller";

        /// <summary>
        /// The operator tool, acting as itself: reads and writes ledger data and users in any tenant.
        /// </summary>
        public const string Tool = "tool";

        /// <summary>
        /// An advisor, signed in as a person: reads and writes ledger data in the tenants where they are a user.
        /// </summary>
        public const string Advisor = "advisor";

        /// <summary>
        /// The issuer of the development identities.
        /// </summary>
        public const string Issuer = "https://example.ciamlogin.com/example/v2.0";

        /// <summary>
        /// The development advisor's subject (object ID).
        /// </summary>
        public const string AdvisorSubject = "11111111-1111-1111-1111-111111111111";
    }
}
