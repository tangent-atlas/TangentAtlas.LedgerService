// <copyright file="LedgerApiException.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Api
{
    using System;
    using System.Net;

    /// <summary>
    /// The ledger server refused a request.
    /// </summary>
    public sealed class LedgerApiException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="LedgerApiException"/> class.
        /// </summary>
        public LedgerApiException()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LedgerApiException"/> class.
        /// </summary>
        /// <param name="message">What went wrong.</param>
        public LedgerApiException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LedgerApiException"/> class.
        /// </summary>
        /// <param name="message">What went wrong.</param>
        /// <param name="innerException">The cause.</param>
        public LedgerApiException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LedgerApiException"/> class.
        /// </summary>
        /// <param name="statusCode">The response's status code.</param>
        /// <param name="message">What went wrong.</param>
        /// <param name="responseBody">The response's body, if any.</param>
        public LedgerApiException(HttpStatusCode statusCode, string message, string? responseBody)
            : base(message)
        {
            this.StatusCode = statusCode;
            this.ResponseBody = responseBody;
        }

        /// <summary>
        /// Gets the response's status code: 401 not signed in, 403 not allowed, 404 not found (or not a member of the tenant),
        /// 412 a stale row version, 422 a broken constraint, 426 a client built from a different model.
        /// </summary>
        public HttpStatusCode StatusCode { get; }

        /// <summary>
        /// Gets the response's body, if any.
        /// </summary>
        public string? ResponseBody { get; }
    }
}
