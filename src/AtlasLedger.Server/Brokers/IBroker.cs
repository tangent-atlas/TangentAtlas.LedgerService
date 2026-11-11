// <copyright file="IBroker.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Brokers
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;

    /// <summary>
    /// A broker order as the router hands it to a broker: what to trade, where, and how.
    /// </summary>
    /// <param name="TenantId">The tenant.</param>
    /// <param name="BrokerOrderId">Our ID for the order, given to the broker so a retried send is the same order.</param>
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
        OrderSide Side,
        string Ticker,
        string QuoteCurrency,
        decimal Quantity,
        OrderType OrderType,
        decimal? LimitPrice,
        TimeInForce TimeInForce,
        string? BrokerAccount);

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
