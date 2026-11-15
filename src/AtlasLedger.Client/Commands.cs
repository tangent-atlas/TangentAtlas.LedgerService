// <copyright file="Commands.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Client.Api;
    using AtlasLedger.Client.Mirror;
    using AtlasLedger.Client.Scripts;
    using AtlasLedger.Model;

    /// <summary>
    /// The client's commands, each a thin layer over the API.
    /// </summary>
    public static class Commands
    {
        /// <summary>
        /// Lists the tenants the caller may open.
        /// </summary>
        /// <param name="api">The API.</param>
        /// <param name="output">Where to write.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task that completes when the command is done.</returns>
        public static async Task TenantsAsync(LedgerApiClient api, TextWriter output, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(output);
            var tenants = await api.GetTenantsAsync(cancellationToken).ConfigureAwait(false);
            output.WriteLine(tenants.Count == 0 ? $"{api.Caller ?? "An anonymous caller"} may open no tenants." : string.Join(Environment.NewLine, tenants));
        }

        /// <summary>
        /// Loads a script into a tenant.  A table script (<c>{ "operations": [...] }</c>) goes in as one batch (several, if
        /// it is larger than the server's limit); a transaction script (a JSON array of transactions, or JSON Lines: one
        /// transaction per line) goes to the event log, each transaction committed or refused by itself.  Loading a
        /// transaction script again is a no-op.
        /// </summary>
        /// <param name="api">The API.</param>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="path">The script file.</param>
        /// <param name="output">Where to write.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task that completes when the script is applied.</returns>
        /// <exception cref="InvalidOperationException">A transaction was refused (the others were committed).</exception>
        public static async Task LoadAsync(LedgerApiClient api, Guid tenantId, string path, TextWriter output, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(output);
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var isLines = path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase);
            if (isLines || EventMessages.IsTransactionScript(text))
            {
                var transactions = isLines
                    ? text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(EventMessages.ReadLine).ToArray()
                    : EventMessages.ReadScript(text);
                var results = await api.CommitTransactionsAsync(tenantId, transactions, cancellationToken).ConfigureAwait(false);
                var committed = results.Count(result => result.Status == TransactionStatus.Committed);
                output.WriteLine($"Loaded {transactions.Count} transaction(s) ({transactions.Sum(transaction => transaction.Events.Count)} event(s)) from {path} into {tenantId}: {committed} committed, {results.Count - committed} already committed.");
                return;
            }

            var script = await LedgerScript.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            // Large scripts load in several batches, since the server limits a batch's size.
            await api.ApplyInBatchesAsync(tenantId, script, cancellationToken: cancellationToken).ConfigureAwait(false);
            output.WriteLine($"Loaded {script.Operations.Count} operation(s) from {path} into {tenantId}.");
        }

        /// <summary>
        /// Replaces the weights of each model a script names with the script's Weight rows: a weight that changed is
        /// updated, one the script leaves out is removed, and a new one is added, all in one batch (so the rules never see
        /// half a model).  The script is the same one script-loader loads the first time.
        /// </summary>
        /// <param name="api">The API.</param>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="path">The script: Weight inserts (other rows are ignored).</param>
        /// <param name="output">Where to write.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task that completes when the weights are replaced.</returns>
        public static async Task ReplaceWeightsAsync(LedgerApiClient api, Guid tenantId, string path, TextWriter output, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(output);
            var script = await LedgerScript.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            var wanted = script.Operations
                .Where(operation => operation.Action == ScriptAction.Insert && operation.Table == "Weight" && operation.Row is not null)
                .Select(operation => operation.Row!.Value.Deserialize<WeightDto>(JsonSerializerOptions.Web)!)
                .ToArray();
            var change = ReplaceWeights(wanted, await api.ListAsync<WeightDto>(tenantId, "Weight", cancellationToken).ConfigureAwait(false));
            if (change.Operations.Count > 0)
            {
                await api.ApplyAsync(tenantId, change, cancellationToken).ConfigureAwait(false);
            }

            output.WriteLine($"Replaced the weights of {wanted.Select(weight => weight.ModelId).Distinct().Count()} model(s) in {tenantId}: {change.Operations.Count} change(s).");
        }

        /// <summary>
        /// The batch that turns the current weights of the models named into the weights wanted.
        /// </summary>
        /// <param name="wanted">The weights wanted (the models they name are replaced whole).</param>
        /// <param name="current">The tenant's weights now.</param>
        /// <returns>The batch: updates, deletes and inserts.</returns>
        public static LedgerScript ReplaceWeights(IReadOnlyCollection<WeightDto> wanted, IEnumerable<WeightDto> current)
        {
            ArgumentNullException.ThrowIfNull(wanted);
            ArgumentNullException.ThrowIfNull(current);
            var models = wanted.Select(weight => weight.ModelId).ToHashSet();
            var now = current.Where(weight => models.Contains(weight.ModelId)).ToDictionary(weight => (weight.ModelId, weight.AssetId));
            var script = new LedgerScript();
            foreach (var weight in wanted)
            {
                if (!now.TryGetValue((weight.ModelId, weight.AssetId), out var existing))
                {
                    script.Insert("Weight", weight);
                }
                else if (existing.Fraction != weight.Fraction)
                {
                    script.Update("Weight", new { weight.ModelId, weight.AssetId }, new WeightDto { ModelId = weight.ModelId, AssetId = weight.AssetId, Fraction = weight.Fraction, RowVersion = existing.RowVersion });
                }
            }

            var kept = wanted.Select(weight => (weight.ModelId, weight.AssetId)).ToHashSet();
            foreach (var (key, existing) in now.Where(item => !kept.Contains(item.Key)))
            {
                script.Delete("Weight", new { key.ModelId, key.AssetId }, existing.RowVersion);
            }

            return script;
        }

        /// <summary>
        /// Turns a tenant's proposed allocations into orders now, as the scheduled run would.
        /// </summary>
        /// <param name="api">The API.</param>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="output">Where to write.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task that completes when the orders are written.</returns>
        public static async Task TradeAsync(LedgerApiClient api, Guid tenantId, TextWriter output, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(output);
            var result = await api.GenerateOrdersAsync(tenantId, cancellationToken).ConfigureAwait(false);
            output.WriteLine($"Wrote {result.Orders} order(s) and {result.BrokerOrders} broker order(s) for {tenantId}; {result.Waiting} account(s) waiting for prices.");
        }

        /// <summary>
        /// Shows a tenant's event log in the order it was committed, transaction by transaction: each event's sequence,
        /// kind, source, what it belongs to and what caused it, then its payload.  As JSON, one transaction per line, in the
        /// format the events endpoint takes.
        /// </summary>
        /// <param name="api">The API.</param>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="after">Show only what came after this sequence.</param>
        /// <param name="last">Show only the latest this many transactions, or all if null.</param>
        /// <param name="correlationId">Show only the transactions with an event of this order (its replace chain), and only those events, or all if null.</param>
        /// <param name="follow">Keep showing new transactions until cancelled.</param>
        /// <param name="json">Write JSON Lines.</param>
        /// <param name="output">Where to write.</param>
        /// <param name="cancellationToken">Cancels the command (and ends a follow).</param>
        /// <returns>A task that completes when the log is shown.</returns>
        public static async Task EventsAsync(LedgerApiClient api, Guid tenantId, long after, int? last, Guid? correlationId, bool follow, bool json, TextWriter output, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(output);
            static string Short(Guid? id) => id is { } value ? value.ToString()[..8] : "-";
            TransactionMessage? Select(TransactionMessage transaction) =>
                correlationId is null ? transaction
                : transaction.Events.Where(item => item.CorrelationId == correlationId).ToArray() is { Length: > 0 } events ? transaction with { Events = events }
                : null;
            void Write(TransactionMessage transaction)
            {
                if (json)
                {
                    output.WriteLine(EventMessages.WriteLine(transaction));
                    return;
                }

                output.WriteLine();
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Transaction {transaction.TransactionId}  (through #{transaction.Sequence}, {transaction.Events.Count} event(s))"));
                foreach (var item in transaction.Events)
                {
                    output.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  #{item.Sequence}  {item.EventType} v{item.SchemaVersion}  {item.EventId.ToString()[..8]}  from {item.Source} \"{item.SourceId}\"  at {item.EffectiveTime:yyyy-MM-dd HH:mm:ss}  for {Short(item.CorrelationId)}  because of {Short(item.CausationId)}"));
                    output.WriteLine($"    {item.Payload.GetRawText()}");
                }
            }

            var shown = new List<TransactionMessage>();
            var position = after;
            await foreach (var transaction in api.ReadTransactionsAsync(tenantId, after, follow: false, cancellationToken).ConfigureAwait(false))
            {
                position = transaction.Sequence ?? position;
                if (Select(transaction) is { } selected)
                {
                    shown.Add(selected);
                }
            }

            if (!json)
            {
                output.WriteLine($"{shown.Count} transaction(s), {shown.Sum(transaction => transaction.Events.Count)} event(s){(correlationId is { } chain ? $" of order {Short(chain)}" : string.Empty)}");
            }

            foreach (var transaction in last is { } count ? shown.TakeLast(count) : shown)
            {
                Write(transaction);
            }

            if (!follow)
            {
                return;
            }

            try
            {
                await foreach (var transaction in api.ReadTransactionsAsync(tenantId, position, follow: true, cancellationToken).ConfigureAwait(false))
                {
                    if (Select(transaction) is { } selected)
                    {
                        Write(selected);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Stopped.
            }
        }

        /// <summary>
        /// Shows each account's performance at the closes, and each day if asked.
        /// </summary>
        /// <param name="api">The API.</param>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="days">Whether to show each day.</param>
        /// <param name="output">Where to write.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task that completes when they are shown.</returns>
        public static async Task StatisticsAsync(LedgerApiClient api, Guid tenantId, bool days, TextWriter output, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(output);
            var english = CultureInfo.GetCultureInfo("en-US");
            static string Percent(decimal? value) => value is { } number ? number.ToString("P2", CultureInfo.GetCultureInfo("en-US")) : "-";
            var statistics = await api.GetStatisticsAsync(tenantId, days, cancellationToken).ConfigureAwait(false);
            output.WriteLine($"As of {statistics.AsOf.ToString("u", CultureInfo.InvariantCulture)}: {statistics.Accounts.Count} account(s)");
            foreach (var account in statistics.Accounts)
            {
                var item = account.Statistics;
                output.WriteLine();
                output.WriteLine($"Account {item.AccountId}  {item.From:yyyy-MM-dd} to {item.Through:yyyy-MM-dd}, {item.Returns} daily return(s){(item.IsRanked ? string.Empty : " (not ranked yet)")}");
                output.WriteLine(string.Format(english, "  Value {0:C2}   net flows {1:C2}", item.Value, item.NetFlows));
                output.WriteLine($"  Total return {Percent(item.TotalReturn)}   annualized {Percent(item.AnnualizedReturn)}   volatility {Percent(item.Volatility)}   worst drawdown {Percent(item.MaximumDrawdown)}");
                output.WriteLine($"  Sharpe ratio {(item.SharpeRatio is { } sharpe ? sharpe.ToString("0.00", CultureInfo.InvariantCulture) : "-")}");
                foreach (var gap in item.Gaps)
                {
                    output.WriteLine($"  Note: {gap}");
                }

                foreach (var day in account.Days ?? [])
                {
                    output.WriteLine(string.Format(english, "    {0:yyyy-MM-dd}  value {1,16:C2}  flow {2,14:C2}  return {3,9}  risk-free {4,8}", day.Date, day.Value, day.Flow, Percent(day.Return), day.RiskFreeReturn is { } free ? free.ToString("P4", english) : "-"));
                }
            }
        }

        /// <summary>
        /// Shows a tenant's jobs: every job the server has, whether it is running and what its last run did; then each of
        /// the tenant's schedules, what it asks for and when it next starts and stops.
        /// </summary>
        /// <param name="api">The API.</param>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="output">Where to write.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task that completes when the jobs are shown.</returns>
        public static async Task JobsAsync(LedgerApiClient api, Guid tenantId, TextWriter output, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(output);
            var jobs = await api.GetJobsAsync(tenantId, cancellationToken).ConfigureAwait(false);
            static string Time(DateTimeOffset? time) => time is { } value ? value.ToLocalTime().ToString("ddd yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) : "-";
            static string Run(JobRunDto run) =>
                $"{run.Status} {Time(run.EndTime ?? run.StartTime ?? run.RequestedTime)} ({run.Reason}){(run.Outcome is { } outcome ? $": {outcome}" : string.Empty)}";
            output.WriteLine($"Jobs of {tenantId}");
            foreach (var job in jobs.Jobs)
            {
                var doing = job.Running is { } running ? $"running since {Time(running.StartTime)} ({running.Reason})" : "not running";
                output.WriteLine($"  {job.Job,-24} {job.Kind,-8} {doing}");
                if (job.Last is { } last)
                {
                    output.WriteLine($"  {string.Empty,-24} last: {Run(last)}");
                }
            }

            output.WriteLine();
            if (jobs.Schedules.Count == 0)
            {
                output.WriteLine("No schedules: add JobSchedule rows, or turn a service on with jobs set <tenant> <job> on.");
            }

            foreach (var schedule in jobs.Schedules)
            {
                var when = schedule.StopSchedule is { } stop ? $"{schedule.CronSchedule} to {stop}" : schedule.CronSchedule ?? "-";
                output.WriteLine($"{schedule.Job}  {schedule.Status}  {when}  ({schedule.TimeZone ?? "no zone"})  [{schedule.JobScheduleId.ToString()[..8]}]");
                if (schedule.Problem is { } problem)
                {
                    output.WriteLine($"  can't act on it: {problem}");
                }
                else if (schedule.Status == JobStatus.Scheduled)
                {
                    output.WriteLine(schedule.NextStop is { } nextStop
                        ? $"  next start {Time(schedule.NextStart)}, next stop {Time(nextStop)}"
                        : $"  next run {Time(schedule.NextStart)}");
                }
            }

            if (jobs.Runs.Count > 0)
            {
                output.WriteLine();
                output.WriteLine("Latest runs");
                foreach (var run in jobs.Runs)
                {
                    output.WriteLine($"  {run.Job,-24} {Run(run)}");
                }
            }
        }

        /// <summary>
        /// Runs a task now, whatever its schedules say (unless it is off), and shows what it did.  The run is recorded in the
        /// tenant's JobRun log.
        /// </summary>
        /// <param name="api">The API.</param>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="job">The task's key.</param>
        /// <param name="output">Where to write.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task that completes when the task has run, or been refused.</returns>
        public static async Task RunJobAsync(LedgerApiClient api, Guid tenantId, string job, TextWriter output, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(output);
            var run = await api.RunJobAsync(tenantId, job, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
            output.WriteLine($"{run.Job}: {run.Status}{(run.Outcome is { } outcome ? $": {outcome}" : string.Empty)}");
        }

        /// <summary>
        /// Sets the status of every schedule of one of a tenant's jobs: off, on (a service) or scheduled.  Turning a service
        /// on that has no schedule adds one, with no times.
        /// </summary>
        /// <param name="api">The API.</param>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="job">The job's key.</param>
        /// <param name="status">The status.</param>
        /// <param name="output">Where to write.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task that completes when the change is applied.</returns>
        public static async Task SetJobStatusAsync(LedgerApiClient api, Guid tenantId, string job, JobStatus status, TextWriter output, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(output);
            var (script, message) = SetJobStatus(await api.ListAsync<JobScheduleDto>(tenantId, "JobSchedule", cancellationToken).ConfigureAwait(false), job, status, Guid.NewGuid);
            if (script is not null)
            {
                await api.ApplyAsync(tenantId, script, cancellationToken).ConfigureAwait(false);
            }

            output.WriteLine(message);
        }

        /// <summary>
        /// Builds the change that sets the status of every schedule of a job.
        /// </summary>
        /// <param name="current">The tenant's schedules.</param>
        /// <param name="job">The job's key.</param>
        /// <param name="status">The status.</param>
        /// <param name="newId">Makes a key for a new schedule.</param>
        /// <returns>The script, or null if there is nothing to change; and what it does.</returns>
        public static (LedgerScript? Script, string Message) SetJobStatus(IEnumerable<JobScheduleDto> current, string job, JobStatus status, Func<Guid> newId)
        {
            ArgumentNullException.ThrowIfNull(current);
            ArgumentNullException.ThrowIfNull(newId);
            var rows = current.Where(row => row.Job == job).ToArray();
            if (rows.Length == 0)
            {
                return status switch
                {
                    JobStatus.On => (new LedgerScript().Insert("JobSchedule", new JobScheduleDto { JobScheduleId = newId(), Job = job, Status = JobStatus.On }), $"{job}: on (a new schedule, with no times)."),
                    JobStatus.Off => (null, $"{job} has no schedules: it is already off."),
                    _ => (null, $"{job} has no schedules to set to Scheduled: add a JobSchedule row with its CronSchedule and TimeZone."),
                };
            }

            var script = new LedgerScript();
            foreach (var row in rows.Where(row => row.Status != status))
            {
                script.Update("JobSchedule", new { row.JobScheduleId }, new JobScheduleDto
                {
                    JobScheduleId = row.JobScheduleId,
                    Job = row.Job,
                    Status = status,
                    CronSchedule = row.CronSchedule,
                    StopSchedule = row.StopSchedule,
                    TimeZone = row.TimeZone,
                    Description = row.Description,
                    RowVersion = row.RowVersion,
                });
            }

            var changed = script.Operations.Count;
            return changed == 0
                ? (null, $"{job}: already {status}.")
                : (script, $"{job}: {status} ({changed} schedule(s) changed).");
        }

        /// <summary>
        /// Shows the state of a tenant's orders: each order with its allocations and broker orders.
        /// </summary>
        /// <param name="api">The API.</param>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="output">Where to write.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task that completes when the orders are shown.</returns>
        public static async Task OrdersAsync(LedgerApiClient api, Guid tenantId, TextWriter output, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(output);
            var orders = await api.GetOrdersAsync(tenantId, cancellationToken).ConfigureAwait(false);
            var nodes = (await api.ListAsync<NodeDto>(tenantId, "Node", cancellationToken).ConfigureAwait(false)).ToDictionary(node => node.NodeId);
            string Name(Guid id) => nodes.TryGetValue(id, out var node) ? node.DisplayName : id.ToString()[..8];
            var english = CultureInfo.GetCultureInfo("en-US");
            output.WriteLine($"Orders of {tenantId}, as the rules derived them at {orders.AsOf.ToString("u", CultureInfo.InvariantCulture)}");
            foreach (var proposal in orders.Proposals)
            {
                output.WriteLine();
                output.WriteLine(proposal.IsWaiting
                    ? $"Proposed for {Name(proposal.AccountId)}: waiting for current prices"
                    : $"Proposed for {Name(proposal.AccountId)}: {proposal.Trades.Count} trade(s)");
                foreach (var trade in proposal.Trades)
                {
                    output.WriteLine(string.Format(english, "  {0} {1:#,0.######} {2}", trade.Side, trade.Quantity, Name(trade.AssetId)));
                }
            }

            foreach (var order in orders.Orders)
            {
                output.WriteLine();
                var chain = order.Replaces is { } replaces ? $", replaces {replaces.ToString()[..8]}" : string.Empty;
                output.WriteLine($"Order {order.OrderId.ToString()[..8]}  {order.Status}  ({order.Origin}{chain}){(order.Link is { } link ? $"  {link.Type} with {link.OrderIds.Count} other(s)" : string.Empty)}");
                foreach (var leg in order.Legs)
                {
                    output.WriteLine(string.Format(
                        english,
                        "  {0} {1:#,0.######} {2}  filled {3:#,0.######}, placed {4:#,0.######}, unplaced {5:#,0.######}{6}",
                        leg.Instruction,
                        leg.Quantity,
                        Name(leg.AssetId),
                        leg.Filled,
                        leg.Placed,
                        leg.Unplaced,
                        leg.Unallocated > 0m ? string.Format(english, "  unallocated {0:#,0.######}", leg.Unallocated) : string.Empty));
                    foreach (var allocation in leg.Allocations)
                    {
                        output.WriteLine(string.Format(english, "    for {0,-24} {1,14:#,0.######} filled {2,14:#,0.######}", Name(allocation.AccountId), allocation.Quantity, allocation.Filled));
                    }
                }

                foreach (var brokerOrder in order.BrokerOrders)
                {
                    foreach (var leg in brokerOrder.Legs)
                    {
                        output.WriteLine(string.Format(
                            english,
                            "  broker order {0} leg {1}  {2,14:#,0.######} {3,-15} filled {4,14:#,0.######} at {5}, fees {6:#,0.00}",
                            brokerOrder.BrokerOrderId.ToString()[..8],
                            leg.LegId,
                            leg.Quantity,
                            brokerOrder.Status,
                            leg.Filled,
                            leg.AveragePrice is { } price ? price.ToString("#,0.00####", english) : "-",
                            brokerOrder.Fees));
                    }
                }
            }
        }

        /// <summary>
        /// Shows what a tenant's transactions add up to: each account's holdings (cash among them, as an asset like any
        /// other), valued at their effective prices.
        /// </summary>
        /// <param name="api">The API.</param>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="output">Where to write.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task that completes when the holdings are shown.</returns>
        public static async Task HoldingsAsync(LedgerApiClient api, Guid tenantId, TextWriter output, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(output);
            var holdings = await api.GetHoldingsAsync(tenantId, cancellationToken).ConfigureAwait(false);
            var nodes = (await api.ListAsync<NodeDto>(tenantId, "Node", cancellationToken).ConfigureAwait(false)).ToDictionary(node => node.NodeId);
            var english = CultureInfo.GetCultureInfo("en-US");
            output.WriteLine($"Holdings of {tenantId}, as the rules derived them at {holdings.AsOf.ToString("u", CultureInfo.InvariantCulture)}");
            foreach (var account in holdings.Holdings.GroupBy(holding => holding.AccountId))
            {
                var total = account.Sum(holding => holding.MarketValue ?? 0m);
                output.WriteLine();
                var currency = account.First().Currency;
                output.WriteLine($"{(nodes.TryGetValue(account.Key, out var accountNode) ? accountNode.FullName : account.Key.ToString())} (in {currency})");
                output.WriteLine($"  {"Asset",-10} {"Type",-12} {"Quantity",16} {"Price",16} {"Value",16} {"Weight",7} {"Target",7}  Price from");
                foreach (var holding in account.OrderByDescending(holding => holding.MarketValue ?? 0m))
                {
                    var name = nodes.TryGetValue(holding.AssetId, out var assetNode) ? assetNode.DisplayName : holding.AssetId.ToString()[..8];
                    var flag = holding.IsOverridden ? "  (overridden)" : string.Empty;
                    output.WriteLine(string.Format(
                        english,
                        "  {0,-10} {1,-12} {2,16:#,0.######} {3,16} {4,16} {5,7} {8,7}  {6}{7}",
                        name,
                        holding.AssetType,
                        holding.Quantity,
                        holding.Price is { } price ? $"{price.ToString("#,0.00####", english)} {holding.QuoteCurrency}" : "-",
                        holding.MarketValue is { } value ? value.ToString("#,0.00", english) : "-",
                        total == 0m || holding.MarketValue is null ? "-" : (holding.MarketValue.Value / total).ToString("P1", english),
                        holding.PriceSource ?? "none",
                        flag,
                        holding.TargetWeight is { } target ? target.ToString("P1", english) : "-"));
                }

                output.WriteLine(string.Format(english, "  {0,-10} {1,-12} {2,16} {3,16} {4,16:#,0.00}", "Total", string.Empty, string.Empty, string.Empty, total));
                if (holdings.Accounts.FirstOrDefault(summary => summary.AccountId == account.Key) is { } summary)
                {
                    output.WriteLine(string.Format(
                        english,
                        "  Paid in {0:#,0.00}, gain {1}; model {2}, {3} in cash; {4}",
                        summary.NetContributions,
                        summary.Gain is { } gain ? gain.ToString("#,0.00", english) : "-",
                        summary.ModelName ?? "none",
                        summary.TargetCash is { } cash ? cash.ToString("P1", english) : "-",
                        summary.IsWaiting ? "waiting for current prices" : summary.ProposedTrades is { } count ? $"{count} trade(s) proposed" : "not rebalanced"));
                }
            }

            if (holdings.Problems.Count > 0)
            {
                output.WriteLine();
                output.WriteLine($"{holdings.Problems.Count} event(s) left out, each needing a correction:");
                foreach (var problem in holdings.Problems)
                {
                    output.WriteLine($"  {problem.EventId.ToString()[..8]}  {problem.Problem}");
                }
            }
        }

        /// <summary>
        /// Exports every table the caller may read, parents first, as a script of inserts that loads into any tenant.
        /// </summary>
        /// <param name="api">The API.</param>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="cancellationToken">Cancels the export.</param>
        /// <returns>The script.</returns>
        public static async Task<LedgerScript> ExportAsync(LedgerApiClient api, Guid tenantId, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(api);
            var script = new LedgerScript();
            foreach (var table in Ledger.TablesInDependencyOrder)
            {
                try
                {
                    foreach (var row in await api.ListRowsAsync(tenantId, table, cancellationToken).ConfigureAwait(false))
                    {
                        // Rows go back exactly as they came out.
                        script.Operations.Add(new ScriptOperation { Action = ScriptAction.Insert, Table = table, Row = row });
                    }
                }
                catch (LedgerApiException exception) when (exception.StatusCode == HttpStatusCode.Forbidden)
                {
                    // The caller may not read this table; the export leaves it out.
                }
            }

            return script;
        }

        /// <summary>
        /// Exports a tenant to a file, or to the output.
        /// </summary>
        /// <param name="api">The API.</param>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="path">The file, or null for the output.</param>
        /// <param name="output">Where to write.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task that completes when the script is written.</returns>
        public static async Task ExportToAsync(LedgerApiClient api, Guid tenantId, string? path, TextWriter output, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(output);
            var script = await ExportAsync(api, tenantId, cancellationToken).ConfigureAwait(false);
            if (path == null)
            {
                using var memory = new MemoryStream();
                await script.WriteAsync(memory, cancellationToken).ConfigureAwait(false);
                output.WriteLine(System.Text.Encoding.UTF8.GetString(memory.ToArray()));
                return;
            }

            await using (var file = File.Create(path))
            {
                await script.WriteAsync(file, cancellationToken).ConfigureAwait(false);
            }

            output.WriteLine($"Exported {script.Operations.Count} row(s) from {tenantId} to {path}.");
        }

        /// <summary>
        /// Shows a tenant's alerts, once or as they change.
        /// </summary>
        /// <param name="api">The API.</param>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="watch">True to keep watching until cancelled.</param>
        /// <param name="output">Where to write.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task that completes when the command is done.</returns>
        public static async Task AlertsAsync(LedgerApiClient api, Guid tenantId, bool watch, TextWriter output, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(output);
            string? shown = null;
            do
            {
                var alerts = await api.ListAsync<AlertDto>(tenantId, "Alert", cancellationToken).ConfigureAwait(false);
                var text = alerts.Count == 0 ? "No alerts." : string.Join(Environment.NewLine, alerts.OrderBy(alert => alert.Rule, StringComparer.Ordinal).Select(alert => $"{alert.Rule}: {alert.Message}"));
                if (text != shown)
                {
                    output.WriteLine(watch ? $"[{DateTimeOffset.Now:HH:mm:ss}] {text}" : text);
                    shown = text;
                }

                if (watch)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                }
            }
            while (watch);
        }

        /// <summary>
        /// Waits until a tenant has an alert from a rule, or until it has none from it: the compliance engine raises and
        /// clears alerts as the data changes, a little after each commit.
        /// </summary>
        /// <param name="api">The API.</param>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="rule">The rule, or the start of its name (<c>InvalidPrice</c>).</param>
        /// <param name="present">True to wait for such an alert; false to wait until there is none.</param>
        /// <param name="timeout">How long to wait.</param>
        /// <param name="output">Where to write the alerts when the wait ends.</param>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>True if the tenant got there in time.</returns>
        public static async Task<bool> WaitForAlertAsync(LedgerApiClient api, Guid tenantId, string rule, bool present, TimeSpan timeout, TextWriter output, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(output);
            var deadline = DateTimeOffset.UtcNow + timeout;
            IReadOnlyList<AlertDto> alerts;
            bool reached;
            do
            {
                alerts = await api.ListAsync<AlertDto>(tenantId, "Alert", cancellationToken).ConfigureAwait(false);
                reached = alerts.Any(alert => alert.Rule.StartsWith(rule, StringComparison.Ordinal)) == present;
                if (!reached)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                }
            }
            while (!reached && DateTimeOffset.UtcNow < deadline);

            var shown = alerts.Count == 0 ? "no alerts" : string.Join("; ", alerts.Select(alert => $"{alert.Rule}: {alert.Message}"));
            output.WriteLine(reached
                ? $"{(present ? "Raised" : "Cleared")} {rule}: {shown}"
                : $"TIMED OUT after {timeout.TotalSeconds:0} seconds waiting for {rule} to be {(present ? "raised" : "cleared")}: {shown}");
            return reached;
        }

        /// <summary>
        /// Copies a tenant into a local Replica and shows its graph.
        /// </summary>
        /// <param name="api">The API.</param>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="output">Where to write.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task that completes when the command is done.</returns>
        public static async Task MirrorAsync(LedgerApiClient api, Guid tenantId, TextWriter output, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(output);
            var ledger = new Ledger();
            var forbidden = await LedgerMirror.RefreshAsync(api, ledger, tenantId, cancellationToken).ConfigureAwait(false);
            var partition = ledger.GetPartition(tenantId);
            lock (partition.SyncRoot)
            {
                foreach (var node in partition.Nodes.OrderBy(each => each.DisplayName, StringComparer.Ordinal))
                {
                    output.WriteLine($"{node.DisplayName} ({node.FullName}): {node.Account?.AccountType}");
                    foreach (var symbol in node.Symbols.OrderBy(each => each.Code, StringComparer.Ordinal))
                    {
                        output.WriteLine($"  symbol {symbol.Scheme}:{symbol.Code}");
                    }

                    foreach (var alert in node.Alerts)
                    {
                        output.WriteLine($"  alert {alert.Rule}: {alert.Message}");
                    }
                }
            }

            if (forbidden.Count > 0)
            {
                output.WriteLine($"Not copied (not allowed): {string.Join(", ", forbidden)}.");
            }
        }

        /// <summary>
        /// Shows the server's optional services, or turns them on for a while or off: <c>services</c>, <c>services on
        /// [name...] [--for hours]</c>, <c>services off [name...]</c>.  With no names, every service.
        /// </summary>
        /// <param name="api">The API, as an allowed tool.</param>
        /// <param name="action"><c>status</c>, <c>on</c>, or <c>off</c>.</param>
        /// <param name="names">The services; empty for every service.</param>
        /// <param name="duration">How long to stay on; null for the server's default (four hours).</param>
        /// <param name="output">Where to write.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task that completes when the command is done.</returns>
        public static async Task ServicesAsync(LedgerApiClient api, string action, IReadOnlyList<string> names, TimeSpan? duration, TextWriter output, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(names);
            ArgumentNullException.ThrowIfNull(output);
            var services = await api.GetServicesAsync(cancellationToken).ConfigureAwait(false);
            if (action is "on" or "off")
            {
                var chosen = names.Count == 0 ? services.Select(service => service.Name).ToList() : names;
                var changed = new List<ServiceState>();
                foreach (var name in chosen)
                {
                    changed.Add(await api.SetServiceAsync(name, action == "on", duration, cancellationToken).ConfigureAwait(false));
                }

                services = services.Select(service => changed.FirstOrDefault(each => string.Equals(each.Name, service.Name, StringComparison.OrdinalIgnoreCase)) ?? service).ToList();
            }

            var width = services.Count == 0 ? 0 : services.Max(service => service.Name.Length);
            foreach (var service in services)
            {
                var state = service.OnUntil is { } until
                    ? $"on until {until.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} ({Remaining(until - DateTimeOffset.Now)} left)"
                    : service.IsOn ? "on until turned off" : "off";
                output.WriteLine($"{service.Name.PadRight(width)}  {state,-40}  {service.Description}");
            }
        }

        /// <summary>
        /// Reads a duration: hours as a number (<c>4</c>, <c>0.5</c>), or hours:minutes[:seconds] (<c>1:30</c>).
        /// </summary>
        /// <param name="text">The text.</param>
        /// <param name="duration">The duration, if the text is one.</param>
        /// <returns>True if the text is a duration longer than zero.</returns>
        public static bool TryParseDuration(string text, out TimeSpan duration)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) && hours > 0 && hours < TimeSpan.MaxValue.TotalHours)
            {
                duration = TimeSpan.FromHours(hours);
                return true;
            }

            return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out duration) && duration > TimeSpan.Zero;
        }

        private static string Remaining(TimeSpan remaining) =>
            remaining.TotalHours >= 1 ? $"{(int)remaining.TotalHours}h {remaining.Minutes:00}m" : $"{Math.Max(0, remaining.Minutes)}m";
    }
}
