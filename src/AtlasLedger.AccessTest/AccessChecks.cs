// <copyright file="AccessChecks.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.AccessTest
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Client.Api;
    using AtlasLedger.Client.Scripts;
    using AtlasLedger.Model;

    /// <summary>
    /// What a person may and may not do, checked against a running server.  Each check states the status it expects; the
    /// run counts the checks that got something else.
    /// </summary>
    /// <remarks>
    /// Expects the member firm loaded from <c>firm-a.json</c>, with the signed-in person as one of its users, and the other
    /// firm loaded without them (the walkthrough, Test-LedgerDeployment.ps1, sets both up).  It changes the member firm:
    /// one account's type and one new symbol.
    /// </remarks>
    public sealed class AccessChecks
    {
        private readonly LedgerApiClient person;

        private readonly LedgerApiClient anonymous;

        private readonly TextWriter output;

        private int failures;

        /// <summary>
        /// Initializes a new instance of the <see cref="AccessChecks"/> class.
        /// </summary>
        /// <param name="person">The API, signed in as the person.</param>
        /// <param name="anonymous">The API with no sign-in.</param>
        /// <param name="output">Where to report each check.</param>
        public AccessChecks(LedgerApiClient person, LedgerApiClient anonymous, TextWriter output)
        {
            this.person = person ?? throw new ArgumentNullException(nameof(person));
            this.anonymous = anonymous ?? throw new ArgumentNullException(nameof(anonymous));
            this.output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>
        /// Runs every check.
        /// </summary>
        /// <param name="memberFirm">A firm the person is a user of.</param>
        /// <param name="otherFirm">A firm they are not a user of.</param>
        /// <param name="cancellationToken">Cancels the run.</param>
        /// <returns>The number of checks that did not get what they expected; zero means the server behaves.</returns>
        public async Task<int> RunAsync(Guid memberFirm, Guid otherFirm, CancellationToken cancellationToken)
        {
            this.failures = 0;
            await this.ReachAsync(memberFirm, otherFirm, cancellationToken).ConfigureAwait(false);
            await this.DataRulesAsync(memberFirm, cancellationToken).ConfigureAwait(false);
            await this.BatchesAsync(memberFirm, otherFirm, cancellationToken).ConfigureAwait(false);
            this.output.WriteLine();
            this.output.WriteLine(this.failures == 0 ? "Every check got what it expected." : $"{this.failures} check(s) did not get what they expected.");
            return this.failures;
        }

        private async Task ReachAsync(Guid memberFirm, Guid otherFirm, CancellationToken cancellationToken)
        {
            this.Section("Who can reach what:");
            await this.CheckAsync("Without signing in, lists accounts", HttpStatusCode.Unauthorized, () => this.anonymous.ListAsync<AccountDto>(memberFirm, "Account", cancellationToken)).ConfigureAwait(false);
            var tenants = await this.CheckAsync("Opens the tenant picker", HttpStatusCode.OK, () => this.person.GetTenantsAsync(cancellationToken)).ConfigureAwait(false);
            this.Expect(tenants?.Contains(memberFirm) == true && !tenants.Contains(otherFirm), "the picker offers their firm and not the other");
            var accounts = await this.CheckAsync("Lists accounts in their firm", HttpStatusCode.OK, () => this.person.ListAsync<AccountDto>(memberFirm, "Account", cancellationToken)).ConfigureAwait(false);
            this.Expect(accounts?.Count == 2, "their firm has the two accounts firm-a.json loads");
            await this.CheckAsync("Lists accounts in a firm they are not a user of", HttpStatusCode.NotFound, () => this.person.ListAsync<AccountDto>(otherFirm, "Account", cancellationToken)).ConfigureAwait(false);
            await this.CheckAsync("Reads dossiers, without Dossier.Read", HttpStatusCode.Forbidden, () => this.person.ListRowsAsync(memberFirm, "Dossier", cancellationToken)).ConfigureAwait(false);
            await this.CheckAsync("Asks for the server's services (tools only: hidden)", HttpStatusCode.NotFound, () => this.person.GetServicesAsync(cancellationToken)).ConfigureAwait(false);
        }

        private async Task DataRulesAsync(Guid firm, CancellationToken cancellationToken)
        {
            this.Section("The data rules a person meets:");
            await this.CheckAsync("Adds an account for a node that does not exist", HttpStatusCode.UnprocessableEntity, () => this.person.InsertAsync(firm, "Account", new AccountDto { AccountId = Guid.NewGuid(), AccountType = AccountType.GroupAccount }, cancellationToken)).ConfigureAwait(false);
            var node = (await this.person.ListAsync<NodeDto>(firm, "Node", cancellationToken).ConfigureAwait(false)).Single(each => each.DisplayName == "Smith Trust");
            await this.CheckAsync("Deletes the Smith Trust at a stale version", HttpStatusCode.PreconditionFailed, () => this.person.DeleteAsync(firm, "Node", new object[] { node.NodeId }, node.RowVersion - 1, cancellationToken)).ConfigureAwait(false);
            await this.CheckAsync("Deletes the Smith Trust while its account exists", HttpStatusCode.UnprocessableEntity, () => this.person.DeleteAsync(firm, "Node", new object[] { node.NodeId }, node.RowVersion, cancellationToken)).ConfigureAwait(false);
            var account = (await this.person.ListAsync<AccountDto>(firm, "Account", cancellationToken).ConfigureAwait(false)).Single(each => each.AccountId == node.NodeId);
            await this.CheckAsync("Changes the account at a stale version", HttpStatusCode.PreconditionFailed, () => this.person.UpdateAsync(firm, "Account", new object[] { account.AccountId }, new AccountDto { AccountId = account.AccountId, AccountType = AccountType.WeightedAccount, RowVersion = account.RowVersion - 1 }, cancellationToken)).ConfigureAwait(false);
            await this.CheckAsync("Changes the account at the current version", HttpStatusCode.OK, () => this.person.UpdateAsync(firm, "Account", new object[] { account.AccountId }, new AccountDto { AccountId = account.AccountId, AccountType = AccountType.WeightedAccount, RowVersion = account.RowVersion }, cancellationToken)).ConfigureAwait(false);
        }

        private async Task BatchesAsync(Guid firm, Guid otherFirm, CancellationToken cancellationToken)
        {
            this.Section("Batches commit or roll back as a unit:");
            var node = (await this.person.ListAsync<NodeDto>(firm, "Node", cancellationToken).ConfigureAwait(false)).Single(each => each.DisplayName == "Smith Trust");
            var account = (await this.person.ListAsync<AccountDto>(firm, "Account", cancellationToken).ConfigureAwait(false)).Single(each => each.AccountId == node.NodeId);
            var good = new LedgerScript()
                .Insert("Symbol", new SymbolDto { Scheme = "Custodian", Code = "2222", NodeId = node.NodeId })
                .Update("Account", new { account.AccountId }, new AccountDto { AccountId = account.AccountId, AccountType = AccountType.GroupAccount, RowVersion = account.RowVersion });
            await this.CheckAsync("Adds a symbol and changes the account in one batch", HttpStatusCode.OK, () => this.person.ApplyAsync(firm, good, cancellationToken)).ConfigureAwait(false);
            var bad = new LedgerScript()
                .Insert("Symbol", new SymbolDto { Scheme = "Custodian", Code = "3333", NodeId = node.NodeId })
                .Insert("Account", new AccountDto { AccountId = Guid.NewGuid(), AccountType = AccountType.GroupAccount });
            await this.CheckAsync("Adds a symbol and an account for a missing node in one batch", HttpStatusCode.UnprocessableEntity, () => this.person.ApplyAsync(firm, bad, cancellationToken)).ConfigureAwait(false);
            await this.CheckAsync("Sends a batch to a firm they are not a user of", HttpStatusCode.NotFound, () => this.person.ApplyAsync(otherFirm, good, cancellationToken)).ConfigureAwait(false);
            var nonsense = new LedgerScript();
            nonsense.Operations.Add(new ScriptOperation { Action = ScriptAction.Insert, Table = "Nonsense" });
            await this.CheckAsync("Sends a batch naming a table that does not exist", HttpStatusCode.BadRequest, () => this.person.ApplyAsync(firm, nonsense, cancellationToken)).ConfigureAwait(false);
            var codes = (await this.person.ListAsync<SymbolDto>(firm, "Symbol", cancellationToken).ConfigureAwait(false)).Select(symbol => symbol.Code).OrderBy(code => code, StringComparer.Ordinal).ToList();
            this.output.WriteLine($"  Symbols now: {string.Join(", ", codes)}");
            this.Expect(codes.Contains("2222") && !codes.Contains("3333"), "the good batch committed and the failed one left nothing behind");
        }

        private void Section(string title)
        {
            this.output.WriteLine();
            this.output.WriteLine(title);
        }

        /// <summary>
        /// Runs one request, reports its status, and counts it as a failure if the status is not the one expected.
        /// </summary>
        private async Task<T?> CheckAsync<T>(string description, HttpStatusCode expected, Func<Task<T>> request)
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

            this.output.WriteLine($"  {(int)status} {status,-20} {description}");
            if (status != expected)
            {
                this.output.WriteLine($"      UNEXPECTED: wanted {(int)expected} {expected}");
                this.failures++;
            }

            return result;
        }

        private async Task CheckAsync(string description, HttpStatusCode expected, Func<Task> request) =>
            await this.CheckAsync<bool>(description, expected, async () =>
            {
                await request().ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);

        private void Expect(bool condition, string description)
        {
            if (!condition)
            {
                this.output.WriteLine($"      UNEXPECTED: {description} does not hold");
                this.failures++;
            }
        }
    }
}
