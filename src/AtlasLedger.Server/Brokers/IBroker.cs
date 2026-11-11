// <copyright file="IBroker.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Brokers
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;

    /// <summary>
    /// A broker order as the router hands it to a broker: what to trade, where, and how.
    /// </summary>
    /// <param name="TenantId">The tenant.</param>
    /// <param name="BrokerOrderId">The broker order's key in the tenant's ledger.</param>
    /// <param name="ClientOrderId">The ID the broker is given, so a retried send is the same order: derived from the tenant
    /// and the broker order, so it is unique across tenants (two tenants may use the same keys; the broker sees one account
    /// of orders for all of them).  A broker adapter keys everything it knows about an order on this.</param>
    /// <param name="Side">Buy or sell.</param>
    /// <param name="Ticker">The asset's ticker.</param>
    /// <param name="QuoteCurrency">The currency it is quoted in.</param>
    /// <param name="Quantity">How many units.</param>
    /// <param name="OrderType">Market or limit.</param>
    /// <param name="LimitPrice">The limit, for a limit order.</param>
    /// <param name="TimeInForce">How long it stays working.</param>
    /// <param name="BrokerAccount">The account at the broker (its custodian account number), when the order is for one account.</param>
    public sealed record BrokerOrderRequest(
        Guid TenantId,
        Guid BrokerOrderId,
        Guid ClientOrderId,
        OrderSide Side,
        string Ticker,
        string QuoteCurrency,
        decimal Quantity,
        OrderType OrderType,
        decimal? LimitPrice,
        TimeInForce TimeInForce,
        string? BrokerAccount)
    {
        /// <summary>
        /// The ID a broker is given for a broker order: unique across tenants, since tenants share nothing and may use the
        /// same keys, while a broker sees the orders of all of them (a name-based UUID, version 8, SHA-256).
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="brokerOrderId">The broker order's key in the tenant.</param>
        /// <returns>The ID.</returns>
        public static Guid ClientOrderIdFor(Guid tenantId, Guid brokerOrderId)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"tangent-atlas/client-order/{tenantId:N}/{brokerOrderId:N}"));
            hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
            return new Guid(hash.AsSpan(0, 16), bigEndian: true);
        }
    }

    /// <summary>
    /// Something a broker reported about an order, before it is recorded as an execution report.
    /// </summary>
    /// <param name="SourceId">The broker's own ID for the report: the same report always has the same ID, so recording it twice records it once.</param>
    /// <param name="ReportType">Accepted, fill, cancelled, rejected or expired.</param>
    /// <param name="Quantity">A fill's quantity.</param>
    /// <param name="Price">A fill's price.</param>
    /// <param name="Fees">A fill's fees, as one amount, whatever they are made of.</param>
    /// <param name="ReportTime">When it happened, by the broker's clock.</param>
    /// <param name="Text">The broker's words, if any (a rejection's reason).</param>
    public sealed record BrokerReport(string SourceId, ExecutionReportType ReportType, decimal? Quantity, decimal? Price, decimal? Fees, DateTimeOffset ReportTime, string? Text);

    /// <summary>
    /// A broker, as the router sees it: a place to send orders and cancels, and to ask what happened.  Every answer is the
    /// broker's whole account of an order so far (the router records only what it hasn't), so a lost answer costs nothing:
    /// the next question gets it again.  Wiring a real broker is implementing this.
    /// </summary>
    public interface IBroker
    {
        /// <summary>
        /// Gets which broker this is: the <see cref="EventSource"/> broker orders name and reports are recorded under.
        /// </summary>
        EventSource Source { get; }

        /// <summary>
        /// Sends an order.
        /// </summary>
        /// <param name="order">The order.</param>
        /// <param name="cancellationToken">Cancels the call.</param>
        /// <returns>Everything the broker has reported about the order.</returns>
        Task<IReadOnlyList<BrokerReport>> PlaceAsync(BrokerOrderRequest order, CancellationToken cancellationToken);

        /// <summary>
        /// Asks to cancel an order.  The answer says whether it was (a cancelled report) or fills beat it.
        /// </summary>
        /// <param name="order">The order.</param>
        /// <param name="cancellationToken">Cancels the call.</param>
        /// <returns>Everything the broker has reported about the order.</returns>
        Task<IReadOnlyList<BrokerReport>> CancelAsync(BrokerOrderRequest order, CancellationToken cancellationToken);

        /// <summary>
        /// Asks what has happened to an order.
        /// </summary>
        /// <param name="order">The order.</param>
        /// <param name="cancellationToken">Cancels the call.</param>
        /// <returns>Everything the broker has reported about the order; empty if the broker has never heard of it.</returns>
        Task<IReadOnlyList<BrokerReport>> GetReportsAsync(BrokerOrderRequest order, CancellationToken cancellationToken);
    }
}
