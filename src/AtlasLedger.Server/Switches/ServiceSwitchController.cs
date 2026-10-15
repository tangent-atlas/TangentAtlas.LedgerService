// <copyright file="ServiceSwitchController.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Switches
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Bellerophon.Runtime;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Turns the server's optional services on and off: <c>v1/system/services</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one route outside a tenant, because the services serve every tenant at once.  It reads and changes no
    /// tenant's data, and only an allowed tool acting as itself (an application's token whose client ID is in
    /// <c>Tools:ClientIds</c>) may use it.  Anyone else signed in gets 404, as for a tenant they do not belong to, so the
    /// route is not even confirmed to exist; anyone not signed in gets 401.
    /// </para>
    /// <para>
    /// Turning a service on lasts for a while (four hours unless the request says otherwise) and then the service turns
    /// itself off, so a forgotten service cannot run all month.
    /// </para>
    /// </remarks>
    [ApiController]
    [Authorize]
    [Route("v1/system/services")]
    public sealed partial class ServiceSwitchController : ControllerBase
    {
        private readonly ServiceSwitchboard switchboard;

        private readonly ServiceSwitchOptions options;

        private readonly ToolAccessOptions tools;

        private readonly ILogger<ServiceSwitchController> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ServiceSwitchController"/> class.
        /// </summary>
        /// <param name="switchboard">The services' switches.</param>
        /// <param name="options">The switch settings.</param>
        /// <param name="tools">The allowed tools.</param>
        /// <param name="logger">The logger.</param>
        public ServiceSwitchController(ServiceSwitchboard switchboard, IOptions<ServiceSwitchOptions> options, IOptions<ToolAccessOptions> tools, ILogger<ServiceSwitchController> logger)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(tools);
            this.switchboard = switchboard;
            this.options = options.Value;
            this.tools = tools.Value;
            this.logger = logger;
        }

        /// <summary>
        /// Lists every service and whether it is on.
        /// </summary>
        /// <returns>The services.</returns>
        [HttpGet]
        public ActionResult<IEnumerable<ServiceState>> List()
        {
            if (!this.IsAllowedTool(out _))
            {
                return this.NotFound();
            }

            return this.Ok(this.switchboard.All.Select(ServiceState.From).ToList());
        }

        /// <summary>
        /// Gets one service.
        /// </summary>
        /// <param name="name">The service's name.</param>
        /// <returns>The service.</returns>
        [HttpGet("{name}")]
        public ActionResult<ServiceState> Get(string name)
        {
            if (!this.IsAllowedTool(out _) || this.switchboard.Find(name) is not { } found)
            {
                return this.NotFound();
            }

            return this.Ok(ServiceState.From(found));
        }

        /// <summary>
        /// Turns a service on for a while, or off.  Turning on a service that is already on sets its new end time.
        /// </summary>
        /// <param name="name">The service's name.</param>
        /// <param name="request">On or off, and for how long.</param>
        /// <returns>The service, as it is now.</returns>
        [HttpPut("{name}")]
        public ActionResult<ServiceState> Set(string name, [FromBody] ServiceSwitchRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!this.IsAllowedTool(out var clientId) || this.switchboard.Find(name) is not { } found)
            {
                return this.NotFound();
            }

            if (!request.IsOn)
            {
                found.TurnOff();
                this.LogTurnedOff(found.Name, clientId);
                return this.Ok(ServiceState.From(found));
            }

            var duration = request.Duration ?? this.options.DefaultDuration;
            if (duration <= TimeSpan.Zero || duration > this.options.MaximumDuration)
            {
                return this.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "The duration is out of range.",
                    detail: $"A service may be turned on for more than zero and at most {this.options.MaximumDuration}.");
            }

            found.TurnOn(duration);
            this.LogTurnedOn(found.Name, clientId, found.OnUntil);
            return this.Ok(ServiceState.From(found));
        }

        private bool IsAllowedTool(out string clientId)
        {
            if (IdentityClaims.TryGetApplication(this.User, out var application) && this.tools.Allows(application))
            {
                clientId = application;
                return true;
            }

            clientId = string.Empty;
            this.LogRefused(this.HttpContext.Request.Method, this.HttpContext.Request.Path);
            return false;
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Service {Name} turned on by tool {ClientId} until {OnUntil}.")]
        private partial void LogTurnedOn(string name, string clientId, DateTimeOffset? onUntil);

        [LoggerMessage(Level = LogLevel.Information, Message = "Service {Name} turned off by tool {ClientId}.")]
        private partial void LogTurnedOff(string name, string clientId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Refused {Method} {Path}: the caller is not an allowed tool acting as itself.")]
        private partial void LogRefused(string method, string path);
    }
}
