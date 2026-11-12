// <copyright file="OrderRouter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Brokers
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using AtlasLedger.Server.Events;
    using AtlasLedger.Server.Orders;
    using AtlasLedger.Server.Switches;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Sends broker orders to their brokers and records what the brokers report, while its switch
    /// (<see cref="ServiceSwitchboard.Broker"/>) is on.  Nothing is sent while it is off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each pass, for every broker order still open (no fill for all of it, and not cancelled, rejected or expired):
    /// it waits until the order is durable (nothing leaves the system before then); asks the broker what it knows of the
    /// order and records anything new; sends it if the broker has never heard of it and it has no reports yet; and sends a
    /// cancel if one was requested.  Asking before sending is the reconciliation: after a restart, or after a send whose
    /// answer was lost, an order the broker already has is never sent twice, and its fills are recorded.
    /// </para>
    /// <para>
    /// Reports are recorded idempotently on the broker's own ID for them, so recording one twice records it once.  The
    /// rules do the rest: they book fills to the accounts and work out every order's state.
    /// </para>
    /// </remarks>
    internal sealed partial class OrderRouter : BackgroundService
    {
        private readonly Ledger ledger;

        private readonly IReadOnlyDictionary<EventSource, IBroker> brokers;

        private readonly ServiceSwitch serviceSwitch;

        private readonly BrokerOptions options;

        private readonly TimeProvider timeProvider;

        private readonly ILogger<OrderRouter> logger;

        private readonly HashSet<EventSource> unknownBrokers = new HashSet<EventSource>();

        // Events never change, so each is read once.
        private readonly ConcurrentDictionary<(Guid TenantId, Guid EventId), LedgerEvent> decoded = new ConcurrentDictionary<(Guid TenantId, Guid EventId), LedgerEvent>();

        /// <summary>
        /// Initializes a new instance of the <see cref="OrderRouter"/> class.
        /// </summary>
        /// <param name="ledger">The ledger.</param>
        /// <param name="brokers">The brokers, one per <see cref="EventSource"/>.</param>
        /// <param name="switchboard">The services' switches.</param>
        /// <param name="options">The settings.</param>
        /// <param name="timeProvider">The clock.</param>
        /// <param name="logger">The log.</param>
        public OrderRouter(Ledger ledger, IEnumerable<IBroker> brokers, ServiceSwitchboard switchboard, IOptions<BrokerOptions> options, TimeProvider timeProvider, ILogger<OrderRouter> logger)
        {
            this.ledger = ledger;
            this.brokers = brokers.ToDictionary(broker => broker.Source);
            this.serviceSwitch = switchboard[ServiceSwitchboard.Broker];
            this.options = options.Value;
            this.timeProvider = timeProvider;
            this.logger = logger;
        }

        /// <summary>
        /// The key an execution report is recorded under: the same broker and report always give the same key (a name-based
        /// UUID, version 8, SHA-256).
        /// </summary>
        /// <param name="source">The broker.</param>
        /// <param name="sourceId">The broker's ID for the report.</param>
        /// <returns>The key.</returns>
        public static Guid ReportId(EventSource source, string sourceId)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"tangent-atlas/broker-answer/{source}/{sourceId}"));
            hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
            return new Guid(hash.AsSpan(0, 16), bigEndian: true);
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (!this.serviceSwitch.IsOn)
                    {
                        await this.serviceSwitch.WaitForChangeAsync(stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    this.LogSwitchedOn(this.brokers.Count);
                    using var timer = new PeriodicTimer(this.options.PollInterval, this.timeProvider);
                    do
                    {
                        foreach (var partition in this.ledger.Partitions.ToArray())
                        {
                            if (!partition.IsFaulted)
                            {
                                await this.RouteAsync(partition, stoppingToken).ConfigureAwait(false);
                            }
                        }
                    }
                    while (this.serviceSwitch.IsOn && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));

                    this.LogSwitchedOff();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task RouteAsync(LedgerPartition partition, CancellationToken cancellationToken)
        {
            foreach (var work in this.OpenOrders(partition))
            {
                if (!this.brokers.TryGetValue(work.Broker, out var broker))
                {
                    if (this.unknownBrokers.Add(work.Broker))
                    {
                        this.LogNoBroker(work.Broker);
                    }

                    continue;
                }

                try
                {
                    await partition.WaitUntilDurableAsync(work.Sequence, cancellationToken).ConfigureAwait(false);
                    var reports = await broker.GetReportsAsync(work.Request, cancellationToken).ConfigureAwait(false);
                    if (reports.Count == 0 && work.IsPending && !work.IsCancelRequested)
                    {
                        reports = await broker.PlaceAsync(work.Request, cancellationToken).ConfigureAwait(false);
                        this.LogSent(partition.TenantId, work.Request.BrokerOrderId, string.Join(", ", work.Request.Legs.Select(leg => $"{leg.Instruction} {leg.Quantity} {leg.Ticker}")));
                    }

                    if (work.IsCancelRequested && reports.Count > 0 && !IsDone(reports, work.Request))
                    {
                        reports = await broker.CancelAsync(work.Request, cancellationToken).ConfigureAwait(false);
                    }

                    this.Record(partition, work, broker.Source, reports);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The broker didn't answer, or answered badly: ask again next pass.  Nothing is lost, since the broker's
                    // account of the order is complete every time.
                    this.LogBrokerFailed(exception, partition.TenantId, work.Request.BrokerOrderId);
                }
            }
        }

        /// <summary>
        /// The tenant's broker orders that need the broker this pass, from its events.  Besides the ones still open:
        /// <list type="bullet">
        /// <item>a broker order of a replaced order, or of one whose cancel was asked for, is cancelled;</item>
        /// <item>one-cancels-other: once any of the linked orders has a fill, the others are cancelled;</item>
        /// <item>one-triggers-other: the triggered orders are held (not sent) until the trigger has filled, and refused if
        /// the trigger finishes without filling.</item>
        /// </list>
        /// A broker order that can't be routed (no ticker, no institution, no account identifier) is refused here, as a
        /// rejection of our own, rather than tried every pass.
        /// </summary>
        private List<Work> OpenOrders(LedgerPartition partition)
        {
            var work = new List<Work>();
            var refused = new List<(BrokerOrderSentEvent Sent, string Text)>();
            lock (partition.SyncRoot)
            {
                var events = partition.Events.Select(row => this.Read(partition.TenantId, row)).OfType<OrderEvent>().ToArray();
                var orders = events.OfType<OrderPlacedEvent>().ToDictionary(order => order.OrderId);
                var byOrder = events.Where(item => item is not OrderPlacedEvent).ToLookup(item => item.OrderId);
                var replacements = events.OfType<OrderReplacedEvent>().GroupBy(item => item.Replaces).ToDictionary(group => group.Key, group => group.OrderBy(OrderBook.ProcessingOrder).First());
                var states = orders.Values.ToDictionary(order => order.OrderId, order => OrderBook.State(order, byOrder[order.OrderId], replacements.GetValueOrDefault(order.OrderId)));
                var triggers = orders.Values
                    .Where(order => order.Link?.Type == OrderLinkType.OneTriggersOther)
                    .SelectMany(order => order.Link!.OrderIds.Select(child => (Child: child, Trigger: order.OrderId)))
                    .ToLookup(item => item.Child, item => item.Trigger);
                var tickers = partition.Symbols.Where(symbol => symbol.Scheme == SymbolScheme.Ticker).GroupBy(symbol => symbol.NodeId).ToDictionary(group => group.Key, group => group.First().Code);
                foreach (var sent in events.OfType<BrokerOrderSentEvent>())
                {
                    var state = states[sent.OrderId];
                    var brokerState = state.BrokerOrders.First(item => item.BrokerOrderId == sent.BrokerOrderId);
                    if (!brokerState.IsOpen)
                    {
                        continue;
                    }

                    var order = orders[sent.OrderId];
                    var answers = byOrder[sent.OrderId].OfType<BrokerAnswerEvent>().Where(answer => answer.BrokerOrderId == sent.BrokerOrderId).ToArray();
                    var cancels = byOrder[sent.OrderId].OfType<CancelRequestedEvent>().Where(cancel => cancel.BrokerOrderId is null || cancel.BrokerOrderId == sent.BrokerOrderId).ToArray();

                    // Linked orders: a held child waits; a child whose trigger finished without filling is refused.
                    var isHeld = false;
                    foreach (var triggerId in triggers[order.OrderId])
                    {
                        var trigger = states.GetValueOrDefault(triggerId);
                        if (trigger is null || trigger.Status == OrderStatus.Filled)
                        {
                            continue;
                        }

                        if (trigger.IsFinished)
                        {
                            refused.Add((sent, $"The order that was to trigger it ({triggerId}) finished without filling."));
                        }

                        isHeld = true;
                    }

                    if (isHeld)
                    {
                        continue;
                    }

                    var isCancelled = cancels.Length > 0
                        || state.Status == OrderStatus.Replaced
                        || OneCancelsOther(order, orders, states);
                    var legs = new List<BrokerLegRequest>();
                    string? problem = null;
                    foreach (var brokerLeg in sent.Legs)
                    {
                        var leg = order.Legs.First(item => item.LegId == brokerLeg.LegId);
                        var asset = partition.FindAsset(leg.AssetId)!;
                        if (!tickers.TryGetValue(leg.AssetId, out var ticker))
                        {
                            problem = "The asset has no ticker to trade it by.";
                            break;
                        }

                        legs.Add(new BrokerLegRequest(leg.LegId, leg.Instruction, ticker, asset.QuoteCurrency, brokerLeg.Quantity));
                    }

                    // The broker is the institution's; the account at the broker is the account's own identifier there.
                    var account = partition.FindAccount(sent.AccountId)!;
                    var institution = account.InstitutionId is { } institutionId ? partition.FindInstitution(institutionId) : null;
                    problem ??= institution is null ? "The account isn't held at an institution, so there is no broker to send it to."
                        : account.ExternalAccountId is null ? $"The account has no identifier at {institution.Name}."
                        : null;
                    if (problem is not null)
                    {
                        if (answers.Length == 0)
                        {
                            refused.Add((sent, problem));
                        }

                        continue;
                    }

                    work.Add(new Work(
                        new BrokerOrderRequest(partition.TenantId, sent.BrokerOrderId, BrokerOrderRequest.ClientOrderIdFor(partition.TenantId, sent.BrokerOrderId), legs, sent.OrderType, sent.LimitPrice, sent.TimeInForce, account.ExternalAccountId!),
                        sent.OrderId,
                        institution!.Source,
                        new[] { sent.Envelope.Sequence }.Concat(cancels.Select(cancel => cancel.Envelope.Sequence)).Concat(replacements.TryGetValue(order.OrderId, out var replacement) ? [replacement.Envelope.Sequence] : []).Max(),
                        answers.Length == 0,
                        isCancelled,
                        answers.Select(answer => (answer.Envelope.Source, answer.Envelope.SourceId)).ToHashSet()));
                }
            }

            if (refused.Count > 0)
            {
                var now = this.timeProvider.GetUtcNow();
                using var transaction = partition.BeginTransaction();
                foreach (var (sent, text) in refused.DistinctBy(item => item.Sent.BrokerOrderId))
                {
                    var sourceId = $"router-{sent.BrokerOrderId:N}";
                    var rejected = new OrderRejectedEvent { OrderId = sent.OrderId, BrokerOrderId = sent.BrokerOrderId, ReportTime = now, Text = text };
                    transaction.InsertEvent(EventRows.Row(rejected, ReportId(EventSource.Rules, sourceId), EventSource.Rules, sourceId, now, sent.Envelope.CorrelationId ?? sent.OrderId, sent.EventId));
                }

                transaction.Commit();
            }

            return work;
        }

        /// <summary>
        /// Whether an order is one of a one-cancels-other group in which another order has filled (any of it).
        /// </summary>
        private static bool OneCancelsOther(OrderPlacedEvent order, IReadOnlyDictionary<Guid, OrderPlacedEvent> orders, IReadOnlyDictionary<Guid, OrderState> states)
        {
            var group = new HashSet<Guid>();
            if (order.Link is { Type: OrderLinkType.OneCancelsOther } link)
            {
                group.UnionWith(link.OrderIds);
            }

            foreach (var other in orders.Values.Where(other => other.Link is { Type: OrderLinkType.OneCancelsOther } otherLink && otherLink.OrderIds.Contains(order.OrderId)))
            {
                group.Add(other.OrderId);
                group.UnionWith(other.Link!.OrderIds);
            }

            group.Remove(order.OrderId);
            return group.Any(id => states.GetValueOrDefault(id)?.Legs.Any(leg => leg.Filled > 0m) == true);
        }

        private LedgerEvent Read(Guid tenantId, Event row) =>
            this.decoded.GetOrAdd((tenantId, row.EventId), _ => EventRows.Read(row));

        private void Record(LedgerPartition partition, Work work, EventSource source, IReadOnlyList<BrokerReport> reports)
        {
            var fresh = reports.Where(report => !work.Recorded.Contains((source, report.SourceId))).ToArray();
            if (fresh.Length == 0)
            {
                return;
            }

            using var transaction = partition.BeginTransaction();
            foreach (var report in fresh)
            {
                var orderId = work.OrderId;
                var brokerOrderId = work.Request.BrokerOrderId;
                BrokerAnswerEvent answer = report.ReportType switch
                {
                    ExecutionReportType.Accepted => new OrderAcceptedEvent { OrderId = orderId, BrokerOrderId = brokerOrderId, ReportTime = report.ReportTime, Text = report.Text },
                    ExecutionReportType.Fill => new ExecutionReportedEvent { OrderId = orderId, BrokerOrderId = brokerOrderId, ReportTime = report.ReportTime, Text = report.Text, ExecutionId = report.SourceId, LegId = report.LegId, Quantity = report.Quantity ?? 0m, Price = report.Price ?? 0m, Fees = report.Fees ?? 0m },
                    ExecutionReportType.Cancelled => new OrderCancelledEvent { OrderId = orderId, BrokerOrderId = brokerOrderId, ReportTime = report.ReportTime, Text = report.Text },
                    ExecutionReportType.Rejected => new OrderRejectedEvent { OrderId = orderId, BrokerOrderId = brokerOrderId, ReportTime = report.ReportTime, Text = report.Text },
                    _ => new OrderExpiredEvent { OrderId = orderId, BrokerOrderId = brokerOrderId, ReportTime = report.ReportTime, Text = report.Text },
                };
                transaction.InsertEvent(EventRows.Row(answer, ReportId(source, report.SourceId), source, report.SourceId, report.ReportTime, null, brokerOrderId, this.timeProvider.GetUtcNow()));
            }

            transaction.Commit();
        }

        private static bool IsDone(IReadOnlyList<BrokerReport> reports, BrokerOrderRequest request) =>
            reports.Any(report => report.ReportType is ExecutionReportType.Cancelled or ExecutionReportType.Rejected or ExecutionReportType.Expired)
            || request.Legs.All(leg => reports.Where(report => report.ReportType == ExecutionReportType.Fill && report.LegId == leg.LegId).Sum(report => report.Quantity ?? 0m) >= leg.Quantity);

        [LoggerMessage(Level = LogLevel.Information, Message = "Order router: switched on, with {BrokerCount} broker(s).")]
        private partial void LogSwitchedOn(int brokerCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "Order router: switched off; nothing more is sent.")]
        private partial void LogSwitchedOff();

        [LoggerMessage(Level = LogLevel.Information, Message = "Order router, tenant {TenantId}: sent broker order {BrokerOrderId}: {Legs}.")]
        private partial void LogSent(Guid tenantId, Guid brokerOrderId, string legs);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Order router: broker orders name {Broker}, which has no connection here; they wait.")]
        private partial void LogNoBroker(EventSource broker);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Order router, tenant {TenantId}: the broker failed on broker order {BrokerOrderId}; asking again next pass.")]
        private partial void LogBrokerFailed(Exception exception, Guid tenantId, Guid brokerOrderId);

        private sealed record Work(BrokerOrderRequest Request, Guid OrderId, EventSource Broker, long Sequence, bool IsPending, bool IsCancelRequested, IReadOnlySet<(EventSource, string)> Recorded);
    }
}
