// <copyright file="SimulatedBroker.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Brokers
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// A broker that trades against the tenant's own price store: a market order fills at once, in full, at the latest
    /// price; a limit order is accepted and fills, in full, once the latest price reaches its limit; an order for a ticker
    /// without a price is rejected.  Fees per fill come from the settings.  It remembers what it was sent in memory only,
    /// so after a restart it has never heard of anything: the router then sends again what is still pending, as it would to
    /// a real broker that never received it.  Day orders don't expire.
    /// </summary>
    public sealed class SimulatedBroker : IBroker
    {
        private readonly Ledger ledger;

        private readonly TimeProvider timeProvider;

        private readonly BrokerOptions options;

        private readonly ConcurrentDictionary<Guid, Known> orders = new ConcurrentDictionary<Guid, Known>();

        /// <summary>
        /// Initializes a new instance of the <see cref="SimulatedBroker"/> class.
        /// </summary>
        /// <param name="ledger">The ledger, for the tenants' prices.</param>
        /// <param name="timeProvider">The clock.</param>
        /// <param name="options">The settings.</param>
        public SimulatedBroker(Ledger ledger, TimeProvider timeProvider, IOptions<BrokerOptions> options)
        {
            ArgumentNullException.ThrowIfNull(options);
            this.ledger = ledger;
            this.timeProvider = timeProvider;
            this.options = options.Value;
        }

        /// <inheritdoc/>
        public EventSource Source => EventSource.Simulator;

        /// <inheritdoc/>
        public Task<IReadOnlyList<BrokerReport>> PlaceAsync(BrokerOrderRequest order, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(order);
            var known = this.orders.GetOrAdd(order.BrokerOrderId, _ => new Known(order));
            lock (known)
            {
                if (known.Reports.Count == 0)
                {
                    if (this.LatestPrice(order) is null)
                    {
                        known.Add(ExecutionReportType.Rejected, null, null, null, this.timeProvider.GetUtcNow(), $"No price for {order.Ticker}.");
                    }
                    else
                    {
                        known.Add(ExecutionReportType.Accepted, null, null, null, this.timeProvider.GetUtcNow(), null);
                    }
                }

                this.TryFill(known);
                return Task.FromResult(known.Snapshot());
            }
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<BrokerReport>> CancelAsync(BrokerOrderRequest order, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(order);
            if (!this.orders.TryGetValue(order.BrokerOrderId, out var known))
            {
                return Task.FromResult<IReadOnlyList<BrokerReport>>(Array.Empty<BrokerReport>());
            }

            lock (known)
            {
                this.TryFill(known);
                if (!known.IsDone)
                {
                    known.Add(ExecutionReportType.Cancelled, null, null, null, this.timeProvider.GetUtcNow(), null);
                }

                return Task.FromResult(known.Snapshot());
            }
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<BrokerReport>> GetReportsAsync(BrokerOrderRequest order, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(order);
            if (!this.orders.TryGetValue(order.BrokerOrderId, out var known))
            {
                return Task.FromResult<IReadOnlyList<BrokerReport>>(Array.Empty<BrokerReport>());
            }

            lock (known)
            {
                this.TryFill(known);
                return Task.FromResult(known.Snapshot());
            }
        }

        private void TryFill(Known known)
        {
            var order = known.Order;
            if (known.IsDone || this.LatestPrice(order) is not { } price)
            {
                return;
            }

            var isMarketable = order.OrderType == OrderType.Market
                || (order.LimitPrice is { } limit && (order.Side == OrderSide.Buy ? price <= limit : price >= limit));
            if (isMarketable)
            {
                known.Add(ExecutionReportType.Fill, order.Quantity, price, this.options.SimulatedFees, this.timeProvider.GetUtcNow(), null);
            }
        }

        private decimal? LatestPrice(BrokerOrderRequest order) =>
            this.ledger.FindPartition(order.TenantId)?.FindPrice(SymbolScheme.Ticker, order.Ticker, order.QuoteCurrency) is { Last: > 0m } price ? price.Last : null;

        private sealed class Known
        {
            private readonly List<BrokerReport> reports = new List<BrokerReport>();

            public Known(BrokerOrderRequest order)
            {
                this.Order = order;
            }

            public BrokerOrderRequest Order { get; }

            public IReadOnlyList<BrokerReport> Reports => this.reports;

            public bool IsDone => this.reports.Any(report => report.ReportType is ExecutionReportType.Fill or ExecutionReportType.Cancelled or ExecutionReportType.Rejected or ExecutionReportType.Expired);

            public void Add(ExecutionReportType type, decimal? quantity, decimal? price, decimal? fees, DateTimeOffset time, string? text) =>
                this.reports.Add(new BrokerReport($"sim-{this.Order.BrokerOrderId:N}-{this.reports.Count}", type, quantity, price, fees, time, text));

            public IReadOnlyList<BrokerReport> Snapshot() => this.reports.ToArray();
        }
    }
}
