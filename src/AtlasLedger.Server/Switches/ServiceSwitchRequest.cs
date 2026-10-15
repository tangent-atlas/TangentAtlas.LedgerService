// <copyright file="ServiceSwitchRequest.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Switches
{
    using System;

    /// <summary>
    /// A request to turn a service on or off: <c>{ "isOn": true, "duration": "04:00:00" }</c>.
    /// </summary>
    public sealed class ServiceSwitchRequest
    {
        /// <summary>
        /// Gets or sets a value indicating whether to turn the service on (true) or off (false).
        /// </summary>
        public bool IsOn { get; set; }

        /// <summary>
        /// Gets or sets how long to stay on, as hours:minutes:seconds; omitted, the server's default (four hours).  Ignored
        /// when turning off.
        /// </summary>
        public TimeSpan? Duration { get; set; }
    }
}
