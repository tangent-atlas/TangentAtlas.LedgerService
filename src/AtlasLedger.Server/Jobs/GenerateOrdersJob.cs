// <copyright file="GenerateOrdersJob.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Jobs
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Server.Orders;
    using AtlasLedger.Server.Switches;

    /// <summary>
    /// Turns a tenant's proposed allocations into orders (<see cref="OrderGenerator"/>), on the market days its schedule
    /// names, while the trading switch is on.  The switch is the whole system's stop: off, a scheduled run does nothing.
    /// </summary>
    [Job("GenerateOrders", MarketDaysOnly = true)]
    public sealed class GenerateOrdersJob : ITenantJob
    {
        private readonly OrderGenerator generator;

        private readonly ServiceSwitch trading;

        /// <summary>
        /// Initializes a new instance of the <see cref="GenerateOrdersJob"/> class.
        /// </summary>
        /// <param name="generator">The order generator.</param>
        /// <param name="switchboard">The services' switches.</param>
        public GenerateOrdersJob(OrderGenerator generator, ServiceSwitchboard switchboard)
        {
            ArgumentNullException.ThrowIfNull(switchboard);
            this.generator = generator;
            this.trading = switchboard[ServiceSwitchboard.Trading];
        }

        /// <inheritdoc/>
        public Task<string> RunAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            if (!this.trading.IsOn)
            {
                return Task.FromResult("Skipped: trading is switched off.");
            }

            var result = this.generator.Generate(tenantId);
            return Task.FromResult(result.Reason is { } reason
                ? $"Nothing written: {reason}"
                : $"{result.Orders} order(s), {result.BrokerOrders} broker order(s); {result.Waiting} account(s) waiting for prices.");
        }
    }
}
