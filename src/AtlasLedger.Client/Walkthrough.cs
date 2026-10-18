// <copyright file="Walkthrough.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Client.Api;
    using AtlasLedger.Client.Mirror;
    using AtlasLedger.Client.Scripts;
    using AtlasLedger.Model;

    /// <summary>
    /// Exercises a running server end to end, as its callers would: the operator tool loads a firm, an advisor works in it,
    /// the server refuses what it should, a firm is exported and loaded into another, and the compliance engine raises and
    /// clears alerts as the data and the market move.
    /// </summary>
    /// <remarks>
    /// Every step states the status it expects, and the walkthrough counts the steps that got something else, so it doubles
    /// as a smoke test of a deployed server.  Each run uses new tenants, so it can run against the same server any number
    /// of times.
    /// </remarks>
    public sealed class Walkthrough
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

        private readonly LedgerApiClient tool;

        /// <summary>
        /// The advisor, a person signed in to firm A; null with real sign-in, where the walkthrough runs as the tool alone.
        /// </summary>
        private readonly LedgerApiClient? advisor;

        private readonly LedgerApiClient anonymous;

        private readonly TextWriter output;

        /// <summary>
        /// How long the walkthrough turns services on for: long enough to finish, short enough to expire soon if it is
        /// interrupted before it can turn them off.
        /// </summary>
        private static readonly TimeSpan ServiceDuration = TimeSpan.FromMinutes(15);

        private readonly TimeSpan marketWait;

        private readonly Guid firmA = Guid.NewGuid();

        private readonly Guid firmB = Guid.NewGuid();

        private readonly Guid firmC = Guid.NewGuid();

        private int failures;

        /// <summary>
        /// Initializes a new instance of the <see cref="Walkthrough"/> class.
        /// </summary>
        /// <param name="httpClient">The HTTP client, with the server's address as its base address.</param>
        /// <param name="output">Where to report each step.</param>
        /// <param name="marketWait">How long to watch the market for a bad print; zero skips that step.</param>
        public Walkthrough(HttpClient httpClient, TextWriter output, TimeSpan marketWait)
            : this(new LedgerApiClient(httpClient, DevelopmentCallers.Tool), new LedgerApiClient(httpClient, DevelopmentCallers.Advisor), new LedgerApiClient(httpClient, null), output, marketWait)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Walkthrough"/> class, for any sign-in.
        /// </summary>
        /// <param name="tool">The tool, acting as itself.</param>
        /// <param name="advisor">The advisor, a person who is a user of firm A; null when only the tool can sign in (a server
        /// with real sign-in, from a command line or script).  Then the tool does the advisor's data work, and the steps only
        /// a person can test (the tenant picker, 404 for a firm they do not belong to, the services hidden from them) are
        /// listed as skipped: the server's tests cover them with person tokens.</param>
        /// <param name="anonymous">A caller with no sign-in.</param>
        /// <param name="output">Where to report each step.</param>
        /// <param name="marketWait">How long to watch the market for a bad print; zero skips that step.</param>
        public Walkthrough(LedgerApiClient tool, LedgerApiClient? advisor, LedgerApiClient anonymous, TextWriter output, TimeSpan marketWait)
        {
            this.tool = tool ?? throw new ArgumentNullException(nameof(tool));
            this.advisor = advisor;
            this.anonymous = anonymous ?? throw new ArgumentNullException(nameof(anonymous));
            this.output = output;
            this.marketWait = marketWait;
        }

        /// <summary>
        /// Gets who does the data work: the advisor, or the tool when no person can sign in.
        /// </summary>
        private LedgerApiClient Worker => this.advisor ?? this.tool;

        /// <summary>
        /// Gets how the worker is named in the steps.
        /// </summary>
        private string WorkerName => this.advisor is null ? "Tool" : "Advisor";

        /// <summary>
        /// Gets the path of the script that loads firm A.
        /// </summary>
        public static string FirmAScript => Path.Combine(AppContext.BaseDirectory, "Data", "firm-a.json");

        /// <summary>
        /// Runs every step.
        /// </summary>
        /// <param name="cancellationToken">Cancels the walkthrough.</param>
        /// <returns>The number of steps that did not get what they expected; zero means the server behaves.</returns>
        public async Task<int> RunAsync(CancellationToken cancellationToken)
        {
            this.failures = 0;
            await this.tool.WaitUntilReadyAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            this.output.WriteLine($"Model {Ledger.ModelName} {Ledger.Version} ({Ledger.Fingerprint}). Firm A {this.firmA}, firm B {this.firmB}, firm C {this.firmC}.");
            var switchedOn = await this.ServicesOnAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await this.LoadAsync(cancellationToken).ConfigureAwait(false);
                await this.AdvisorAsync(cancellationToken).ConfigureAwait(false);
                await this.BatchesAsync(cancellationToken).ConfigureAwait(false);
                await this.ExportAndImportAsync(cancellationToken).ConfigureAwait(false);
                await this.ComplianceAsync(cancellationToken).ConfigureAwait(false);
                await this.MarketAsync(cancellationToken).ConfigureAwait(false);
                await this.MirrorAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await this.ServicesOffAsync(switchedOn, CancellationToken.None).ConfigureAwait(false);
            }

            this.output.WriteLine();
            this.output.WriteLine(this.failures == 0 ? "Every step got what it expected." : $"{this.failures} step(s) did not get what they expected.");
            return this.failures;
        }

        /// <summary>
        /// Turns on, for the walkthrough, the services that are off: compliance and the market feed.
        /// </summary>
        /// <returns>The services this walkthrough turned on, to turn off again at the end.</returns>
        private async Task<IReadOnlyList<string>> ServicesOnAsync(CancellationToken cancellationToken)
        {
            this.Section("Services: the tool turns on the market feed and compliance for the walkthrough:");
            await this.PersonStepAsync("Advisor asks for the services (only tools may: hidden)", HttpStatusCode.NotFound, advisor => advisor.GetServicesAsync(cancellationToken)).ConfigureAwait(false);
            var services = await this.StepAsync("Tool lists the services", HttpStatusCode.OK, () => this.tool.GetServicesAsync(cancellationToken)).ConfigureAwait(false);
            var switchedOn = new List<string>();
            foreach (var service in services ?? Array.Empty<ServiceState>())
            {
                if (service.IsOn)
                {
                    this.output.WriteLine($"  {service.Name} is already on; left as it is.");
                    continue;
                }

                var state = await this.StepAsync($"Tool turns on {service.Name} for {ServiceDuration.TotalMinutes:0} minutes", HttpStatusCode.OK, () => this.tool.SetServiceAsync(service.Name, true, ServiceDuration, cancellationToken)).ConfigureAwait(false);
                this.Expect(state?.IsOn == true, $"{service.Name} is on");
                switchedOn.Add(service.Name);
            }

            return switchedOn;
        }

        /// <summary>
        /// Turns off the services the walkthrough turned on, so it leaves the server as it found it.
        /// </summary>
        private async Task ServicesOffAsync(IReadOnlyList<string> switchedOn, CancellationToken cancellationToken)
        {
            if (switchedOn.Count == 0)
            {
                return;
            }

            this.Section("Services: the tool turns off what the walkthrough turned on:");
            foreach (var name in switchedOn)
            {
                var state = await this.StepAsync($"Tool turns off {name}", HttpStatusCode.OK, () => this.tool.SetServiceAsync(name, false, null, cancellationToken)).ConfigureAwait(false);
                this.Expect(state?.IsOn == false, $"{name} is off");
            }
        }

        private async Task LoadAsync(CancellationToken cancellationToken)
        {
            this.Section("The operator tool loads firm A from a script, in one batch:");
            var script = await LedgerScript.ReadAsync(FirmAScript, cancellationToken).ConfigureAwait(false);
            await this.StepAsync($"Tool loads {Path.GetFileName(FirmAScript)} ({script.Operations.Count} operations)", HttpStatusCode.OK, () => this.tool.ApplyAsync(this.firmA, script, cancellationToken)).ConfigureAwait(false);
        }

        private async Task AdvisorAsync(CancellationToken cancellationToken)
        {
            this.Section(this.advisor is null ? "The tool works in firm A (no person can sign in from here):" : "The advisor, a user of firm A, works in it:");
            var tenants = await this.PersonStepAsync("Opens the tenant picker", HttpStatusCode.OK, advisor => advisor.GetTenantsAsync(cancellationToken)).ConfigureAwait(false);
            this.Expect(this.advisor is null || tenants?.Contains(this.firmA) == true, "the picker offers firm A");
            var accounts = await this.StepAsync("Lists accounts in firm A", HttpStatusCode.OK, () => this.Worker.ListAsync<AccountDto>(this.firmA, "Account", cancellationToken)).ConfigureAwait(false);
            this.Expect(accounts?.Count == 2, "firm A has the two accounts the script loaded");
            await this.PersonStepAsync("Lists accounts in firm B, where they are not a user", HttpStatusCode.NotFound, advisor => advisor.ListAsync<AccountDto>(this.firmB, "Account", cancellationToken)).ConfigureAwait(false);
            await this.StepAsync("Adds an account for a node that does not exist", HttpStatusCode.UnprocessableEntity, () => this.Worker.InsertAsync(this.firmA, "Account", new AccountDto { AccountId = Guid.NewGuid(), AccountType = AccountType.GroupAccount }, cancellationToken)).ConfigureAwait(false);

            var node = (await this.Worker.ListAsync<NodeDto>(this.firmA, "Node", cancellationToken).ConfigureAwait(false)).Single(each => each.DisplayName == "Smith Trust");
            await this.StepAsync("Deletes the Smith Trust at a stale version", HttpStatusCode.PreconditionFailed, () => this.Worker.DeleteAsync(this.firmA, "Node", new object[] { node.NodeId }, node.RowVersion - 1, cancellationToken)).ConfigureAwait(false);
            await this.StepAsync("Deletes the Smith Trust while its account exists", HttpStatusCode.UnprocessableEntity, () => this.Worker.DeleteAsync(this.firmA, "Node", new object[] { node.NodeId }, node.RowVersion, cancellationToken)).ConfigureAwait(false);

            var account = accounts!.Single(each => each.AccountId == node.NodeId);
            await this.StepAsync("Changes the account at a stale version", HttpStatusCode.PreconditionFailed, () => this.Worker.UpdateAsync(this.firmA, "Account", new object[] { account.AccountId }, new AccountDto { AccountId = account.AccountId, AccountType = AccountType.WeightedAccount, RowVersion = account.RowVersion - 1 }, cancellationToken)).ConfigureAwait(false);
            await this.StepAsync("Changes the account at the current version", HttpStatusCode.OK, () => this.Worker.UpdateAsync(this.firmA, "Account", new object[] { account.AccountId }, new AccountDto { AccountId = account.AccountId, AccountType = AccountType.WeightedAccount, RowVersion = account.RowVersion }, cancellationToken)).ConfigureAwait(false);
            await this.StepAsync("Reads dossiers without Dossier.Read", HttpStatusCode.Forbidden, () => this.Worker.ListAsync<DossierPlaceholder>(this.firmA, "Dossier", cancellationToken)).ConfigureAwait(false);
            await this.StepAsync("An anonymous caller lists accounts", HttpStatusCode.Unauthorized, () => this.anonymous.ListAsync<AccountDto>(this.firmA, "Account", cancellationToken)).ConfigureAwait(false);
        }

        private async Task BatchesAsync(CancellationToken cancellationToken)
        {
            this.Section("Batches commit or roll back as a unit:");
            var node = (await this.Worker.ListAsync<NodeDto>(this.firmA, "Node", cancellationToken).ConfigureAwait(false)).Single(each => each.DisplayName == "Smith Trust");
            var account = (await this.Worker.ListAsync<AccountDto>(this.firmA, "Account", cancellationToken).ConfigureAwait(false)).Single(each => each.AccountId == node.NodeId);
            var good = new LedgerScript()
                .Insert("Symbol", new SymbolDto { Scheme = "Custodian", Code = "2222", NodeId = node.NodeId })
                .Update("Account", new { account.AccountId }, new AccountDto { AccountId = account.AccountId, AccountType = AccountType.GroupAccount, RowVersion = account.RowVersion });
            await this.StepAsync("Adds a symbol and changes the account in one batch", HttpStatusCode.OK, () => this.Worker.ApplyAsync(this.firmA, good, cancellationToken)).ConfigureAwait(false);

            var bad = new LedgerScript()
                .Insert("Symbol", new SymbolDto { Scheme = "Custodian", Code = "3333", NodeId = node.NodeId })
                .Insert("Account", new AccountDto { AccountId = Guid.NewGuid(), AccountType = AccountType.GroupAccount });
            await this.StepAsync("Adds a symbol and an account for a missing node in one batch", HttpStatusCode.UnprocessableEntity, () => this.Worker.ApplyAsync(this.firmA, bad, cancellationToken)).ConfigureAwait(false);
            await this.PersonStepAsync("Sends a batch to firm B, where they are not a user", HttpStatusCode.NotFound, advisor => advisor.ApplyAsync(this.firmB, good, cancellationToken)).ConfigureAwait(false);
            var nonsense = new LedgerScript();
            nonsense.Operations.Add(new ScriptOperation { Action = ScriptAction.Insert, Table = "Nonsense" });
            await this.StepAsync("Sends a batch naming a table that does not exist", HttpStatusCode.BadRequest, () => this.Worker.ApplyAsync(this.firmA, nonsense, cancellationToken)).ConfigureAwait(false);

            var symbols = await this.Worker.ListAsync<SymbolDto>(this.firmA, "Symbol", cancellationToken).ConfigureAwait(false);
            var codes = symbols.Select(symbol => symbol.Code).OrderBy(code => code, StringComparer.Ordinal).ToList();
            this.output.WriteLine($"  Symbols now: {string.Join(", ", codes)}");
            this.Expect(codes.Contains("2222") && !codes.Contains("3333"), "the good batch committed and the failed one left nothing behind");
        }

        private async Task ExportAndImportAsync(CancellationToken cancellationToken)
        {
            this.Section("The tool exports firm A as a script and loads exactly that script into firm C:");
            var script = await Commands.ExportAsync(this.tool, this.firmA, cancellationToken).ConfigureAwait(false);
            this.output.WriteLine($"  Exported {script.Operations.Count} rows, parents first.");
            await this.StepAsync("Tool loads the export into firm C as one batch", HttpStatusCode.OK, () => this.tool.ApplyAsync(this.firmC, script, cancellationToken)).ConfigureAwait(false);
            var tenants = await this.PersonStepAsync("The advisor's tenant picker", HttpStatusCode.OK, advisor => advisor.GetTenantsAsync(cancellationToken)).ConfigureAwait(false);
            this.Expect(this.advisor is null || (tenants?.Contains(this.firmA) == true && tenants.Contains(this.firmC)), "the advisor can now open firms A and C");
            var accounts = await this.StepAsync("Lists accounts in firm C", HttpStatusCode.OK, () => this.Worker.ListAsync<AccountDto>(this.firmC, "Account", cancellationToken)).ConfigureAwait(false);
            this.Expect(accounts?.Count == 2, "firm C has firm A's accounts");
        }

        private async Task ComplianceAsync(CancellationToken cancellationToken)
        {
            this.Section("Compliance: the server's rules watch the data and raise and clear alerts:");
            await this.WaitForAlertsAsync("Firm A as loaded: every account has a custodian number", alerts => alerts.Count == 0, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var nodeId = Guid.NewGuid();
            var jones = new LedgerScript()
                .Insert("Node", new NodeDto { NodeId = nodeId, DisplayName = "Jones IRA", FullName = "Jones Individual Retirement Account", NodeStatus = NodeStatus.Active, NodeType = NodeType.Account })
                .Insert("Account", new AccountDto { AccountId = nodeId, AccountType = AccountType.ManagedAccount });
            await this.StepAsync("Tool adds the Jones IRA, with no custodian number", HttpStatusCode.OK, () => this.tool.ApplyAsync(this.firmA, jones, cancellationToken)).ConfigureAwait(false);
            await this.WaitForAlertsAsync("The rules raise an alert", alerts => alerts.Any(alert => alert.NodeId == nodeId && alert.Rule == "MissingCustodian"), TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            await this.StepAsync("Tool adds the Jones IRA's custodian number", HttpStatusCode.OK, () => this.tool.InsertAsync(this.firmA, "Symbol", new SymbolDto { Scheme = "Custodian", Code = "9999-0001", NodeId = nodeId }, cancellationToken)).ConfigureAwait(false);
            await this.WaitForAlertsAsync("The rules clear it", alerts => !alerts.Any(alert => alert.Rule == "MissingCustodian"), TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }

        private async Task MarketAsync(CancellationToken cancellationToken)
        {
            if (this.marketWait <= TimeSpan.Zero)
            {
                return;
            }

            this.Section($"The market: the server's feed sends an occasional bad print (waiting up to {this.marketWait.TotalSeconds:0} seconds):");
            var seen = await this.WaitForAlertsAsync("A bad print raises an invalid price alert", alerts => alerts.Any(alert => alert.Rule.StartsWith("InvalidPrice", StringComparison.Ordinal)), this.marketWait, cancellationToken).ConfigureAwait(false);
            if (seen)
            {
                await this.WaitForAlertsAsync("The next tick corrects the price and the rules clear it", alerts => !alerts.Any(alert => alert.Rule.StartsWith("InvalidPrice", StringComparison.Ordinal)), TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task MirrorAsync(CancellationToken cancellationToken)
        {
            this.Section($"The {this.WorkerName.ToLowerInvariant()}'s local copy of firm A (the Replica role):");
            var ledger = new Ledger();
            var forbidden = await LedgerMirror.RefreshAsync(this.Worker, ledger, this.firmA, cancellationToken).ConfigureAwait(false);
            this.output.WriteLine(forbidden.Count == 0 ? "  Copied every table." : $"  Copied every table the {this.WorkerName.ToLowerInvariant()} may read (not {string.Join(", ", forbidden)}).");
            var partition = ledger.GetPartition(this.firmA);
            lock (partition.SyncRoot)
            {
                foreach (var node in partition.Nodes.OrderBy(each => each.DisplayName, StringComparer.Ordinal))
                {
                    var symbols = string.Join(", ", node.Symbols.Select(symbol => symbol.Code).OrderBy(code => code, StringComparer.Ordinal));
                    this.output.WriteLine($"  {node.DisplayName}: {node.Account?.AccountType}, symbols [{symbols}], {node.Alerts.Count} alert(s)");
                }

                this.Expect(partition.Nodes.Count == 3 && partition.Symbols.All(symbol => symbol.Node.Symbols.Contains(symbol)), "the copy has firm A's three nodes, linked to their symbols");
            }
        }

        private void Section(string title)
        {
            this.output.WriteLine();
            this.output.WriteLine(title);
        }

        /// <summary>
        /// Runs one request, reports its status, and counts it as a failure if the status is not the one expected.
        /// </summary>
        private async Task<T?> StepAsync<T>(string description, HttpStatusCode expected, Func<Task<T>> request)
        {
            HttpStatusCode status;
            var result = default(T);
            try
            {
                result = await request().ConfigureAwait(false);
                status = HttpStatusCode.OK;
            }
            catch (LedgerApiException exception)
            {
                status = exception.StatusCode;
            }

            this.Report(status, expected, description);
            return result;
        }

        private async Task StepAsync(string description, HttpStatusCode expected, Func<Task> request) =>
            await this.StepAsync<bool>(description, expected, async () =>
            {
                await request().ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);

        /// <summary>
        /// Runs a step that only a person can test, as the advisor; lists it as skipped when only the tool can sign in.
        /// </summary>
        private async Task<T?> PersonStepAsync<T>(string description, HttpStatusCode expected, Func<LedgerApiClient, Task<T>> request)
        {
            if (this.advisor is null)
            {
                this.output.WriteLine($"  --- {"skipped",-20} {description} (needs a person's sign-in; the server's tests cover it)");
                return default;
            }

            var advisor = this.advisor;
            return await this.StepAsync(description, expected, () => request(advisor)).ConfigureAwait(false);
        }

        private async Task PersonStepAsync(string description, HttpStatusCode expected, Func<LedgerApiClient, Task> request) =>
            await this.PersonStepAsync<bool>(description, expected, async advisor =>
            {
                await request(advisor).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);

        private void Report(HttpStatusCode status, HttpStatusCode expected, string description)
        {
            this.output.WriteLine($"  {(int)status} {status,-20} {description}");
            if (status != expected)
            {
                this.output.WriteLine($"      UNEXPECTED: wanted {(int)expected} {expected}");
                this.failures++;
            }
        }

        private void Expect(bool condition, string description)
        {
            if (!condition)
            {
                this.output.WriteLine($"      UNEXPECTED: {description} does not hold");
                this.failures++;
            }
        }

        /// <summary>
        /// Polls firm A's alerts until they satisfy a condition.
        /// </summary>
        /// <returns>True if they did before the timeout.</returns>
        private async Task<bool> WaitForAlertsAsync(string description, Func<IReadOnlyList<AlertDto>, bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            IReadOnlyList<AlertDto> alerts;
            do
            {
                alerts = await this.Worker.ListAsync<AlertDto>(this.firmA, "Alert", cancellationToken).ConfigureAwait(false);
                if (condition(alerts))
                {
                    var shown = alerts.Count == 0 ? "no alerts" : string.Join("; ", alerts.Select(alert => $"{alert.Rule}: {alert.Message}"));
                    this.output.WriteLine($"  {description}: {shown}");
                    return true;
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            while (DateTimeOffset.UtcNow < deadline);

            this.output.WriteLine($"  {description}: TIMED OUT with {alerts.Count} alert(s)");
            this.output.WriteLine("      UNEXPECTED: the server did not get there in time");
            this.failures++;
            return false;
        }

        /// <summary>
        /// Dossiers are not synced, so the client has no DTO for them; this stands in for the body it never receives.
        /// </summary>
        private sealed class DossierPlaceholder
        {
        }
    }
}
