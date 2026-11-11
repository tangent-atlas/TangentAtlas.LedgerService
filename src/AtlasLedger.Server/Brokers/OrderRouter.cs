// <copyright file="OrderRouter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Brokers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
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
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"tangent-atlas/execution-report/{source}/{sourceId}"));
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
                    if (reports.Count == 0 && work.IsPending)
                    {
                        reports = await broker.PlaceAsync(work.Request, cancellationToken).ConfigureAwait(false);
                        this.LogSent(partition.TenantId, work.Request.BrokerOrderId, work.Request.Side, work.Request.Quantity, work.Request.Ticker);
                    }

                    if (work.IsCancelRequested && reports.Count > 0 && !IsDone(reports, work.Request.Quantity))
                    {
                        reports = await broker.CancelAsync(work.Request, cancellationToken).ConfigureAwait(false);
                    }

                    Record(partition, work.Request.BrokerOrderId, broker.Source, reports, work.Recorded);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The broker didn't answer, or answered badly: ask again next pass.  Nothing is lost, since the broker's
                    // account of the order is complete every time.
                    this.LogBrokerFailed(exception, partition.TenantId, work.Request.BrokerOrderId);
                }
            }
        }

        private List<Work> OpenOrders(LedgerPartition partition)
        {
            var work = new List<Work>();
            var refused = new List<(Guid BrokerOrderId, string Text)>();
            lock (partition.SyncRoot)
            {
                var reportsByOrder = partition.ExecutionReports.ToLookup(report => report.BrokerOrderId);
                var cancels = partition.CancelRequests.ToLookup(cancel => cancel.BrokerOrderId);
                var tickers = partition.Symbols.Where(symbol => symbol.Scheme == SymbolScheme.Ticker).GroupBy(symbol => symbol.NodeId).ToDictionary(group => group.Key, group => group.First().Code);
                foreach (var brokerOrder in partition.BrokerOrders)
                {
                    var reports = reportsByOrder[brokerOrder.BrokerOrderId].ToArray();
                    var filled = reports.Where(report => report.ReportType == ExecutionReportType.Fill).Sum(report => report.Quantity ?? 0m);
                    var isEnded = reports.Any(report => report.ReportType is ExecutionReportType.Cancelled or ExecutionReportType.Rejected or ExecutionReportType.Expired);
                    if (isEnded || filled >= brokerOrder.Quantity)
                    {
                        continue;
                    }

                    var order = partition.FindOrder(brokerOrder.OrderId)!;
                    var asset = partition.FindAsset(order.AssetId)!;
                    if (!tickers.TryGetValue(order.AssetId, out var ticker))
                    {
                        refused.Add((brokerOrder.BrokerOrderId, "The asset has no ticker to trade it by."));
                        continue;
                    }

                    // The broker is the institution's; the account at the broker is the account's own identifier there.
                    var account = partition.FindAccount(brokerOrder.AccountId)!;
                    if (account.InstitutionId is not { } institutionId || partition.FindInstitution(institutionId) is not { } institution)
                    {
                        refused.Add((brokerOrder.BrokerOrderId, "The account isn't held at an institution, so there is no broker to send it to."));
                        continue;
                    }

                    if (account.ExternalAccountId is not { } brokerAccount)
                    {
                        refused.Add((brokerOrder.BrokerOrderId, $"The account has no identifier at {institution.Name}."));
                        continue;
                    }

                    var cancelRequests = cancels[brokerOrder.BrokerOrderId].ToArray();
                    work.Add(new Work(
                        new BrokerOrderRequest(partition.TenantId, brokerOrder.BrokerOrderId, BrokerOrderRequest.ClientOrderIdFor(partition.TenantId, brokerOrder.BrokerOrderId), order.Side, ticker, asset.QuoteCurrency, brokerOrder.Quantity, brokerOrder.OrderType, brokerOrder.LimitPrice, brokerOrder.TimeInForce, brokerAccount),
                        institution.Source,
                        Math.Max(brokerOrder.RowVersion, cancelRequests.Select(cancel => cancel.RowVersion).DefaultIfEmpty().Max()),
                        reports.Length == 0,
                        cancelRequests.Length > 0,
                        reports.Select(report => (report.Source, report.SourceId)).ToHashSet()));
                }
            }

            // An order that can't be routed is refused here, as a rejection of our own, rather than tried every pass.
            if (refused.Count > 0)
            {
                using var transaction = partition.BeginTransaction();
                foreach (var (brokerOrderId, text) in refused)
                {
                    var sourceId = $"router-{brokerOrderId:N}";
                    transaction.InsertExecutionReport(new ExecutionReport
                    {
                        ExecutionReportId = ReportId(EventSource.Rules, sourceId),
                        BrokerOrderId = brokerOrderId,
                        ReportType = ExecutionReportType.Rejected,
                        ReportTime = this.timeProvider.GetUtcNow(),
                        Source = EventSource.Rules,
                        SourceId = sourceId,
                        Text = text,
                    });
                }

                transaction.Commit();
            }

            return work;
        }

        private static void Record(LedgerPartition partition, Guid brokerOrderId, EventSource source, IReadOnlyList<BrokerReport> reports, IReadOnlySet<(EventSource, string)> recorded)
        {
            var fresh = reports.Where(report => !recorded.Contains((source, report.SourceId))).ToArray();
            if (fresh.Length == 0)
            {
                return;
            }

            using var transaction = partition.BeginTransaction();
            foreach (var report in fresh)
            {
                transaction.InsertExecutionReport(new ExecutionReport
                {
                    ExecutionReportId = ReportId(source, report.SourceId),
                    BrokerOrderId = brokerOrderId,
                    ReportType = report.ReportType,
                    Quantity = report.Quantity,
                    Price = report.Price,
                    Fees = report.Fees,
                    ReportTime = report.ReportTime,
                    Source = source,
                    SourceId = report.SourceId,
                    Text = report.Text,
                });
            }

            transaction.Commit();
        }

        private static bool IsDone(IReadOnlyList<BrokerReport> reports, decimal quantity) =>
            reports.Any(report => report.ReportType is ExecutionReportType.Cancelled or ExecutionReportType.Rejected or ExecutionReportType.Expired)
            || reports.Where(report => report.ReportType == ExecutionReportType.Fill).Sum(report => report.Quantity ?? 0m) >= quantity;

        [LoggerMessage(Level = LogLevel.Information, Message = "Order router: switched on, with {BrokerCount} broker(s).")]
        private partial void LogSwitchedOn(int brokerCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "Order router: switched off; nothing more is sent.")]
        private partial void LogSwitchedOff();

        [LoggerMessage(Level = LogLevel.Information, Message = "Order router, tenant {TenantId}: sent broker order {BrokerOrderId}: {Side} {Quantity} {Ticker}.")]
        private partial void LogSent(Guid tenantId, Guid brokerOrderId, OrderSide side, decimal quantity, string ticker);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Order router: broker orders name {Broker}, which has no connection here; they wait.")]
        private partial void LogNoBroker(EventSource broker);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Order router, tenant {TenantId}: the broker failed on broker order {BrokerOrderId}; asking again next pass.")]
        private partial void LogBrokerFailed(Exception exception, Guid tenantId, Guid brokerOrderId);

        private sealed record Work(BrokerOrderRequest Request, EventSource Broker, long Sequence, bool IsPending, bool IsCancelRequested, IReadOnlySet<(EventSource, string)> Recorded);
    }
}
