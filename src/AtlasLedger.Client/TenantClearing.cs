// <copyright file="TenantClearing.cs" company="Tangent Atlas">
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
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Client.Api;
    using AtlasLedger.Client.Scripts;
    using AtlasLedger.Model;

    /// <summary>
    /// A tenant's rows that name accounts: what deleting an account has to delete with it.
    /// </summary>
    /// <param name="Accounts">The accounts.</param>
    /// <param name="Brokerages">The brokerage extension rows.</param>
    /// <param name="Symbols">The symbols (an account's custodian number is one).</param>
    /// <param name="Edges">The links between nodes (a group account and its members).</param>
    /// <param name="Nodes">The nodes (an account's identity is one).</param>
    public sealed record AccountRows(
        IReadOnlyList<AccountDto> Accounts,
        IReadOnlyList<BrokerageDto> Brokerages,
        IReadOnlyList<SymbolDto> Symbols,
        IReadOnlyList<EdgeDto> Edges,
        IReadOnlyList<NodeDto> Nodes);

    /// <summary>
    /// Clears a tenant, in two stages a page that is open on it shows as they happen: first the holdings leave each account,
    /// then each account is deleted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Holdings are not rows anyone deletes: the rules fold them from the event log, which is never changed or deleted.  So
    /// the holdings leave the way a real account is closed: its securities are sold at their current prices and all of its
    /// cash is withdrawn, one transaction per account.  The log keeps the whole story; the account then holds nothing, and
    /// what it was paid in (deposits less withdrawals) stays true.
    /// </para>
    /// <para>
    /// An account is deleted with what names it (its brokerage row, its custodian number, its links to other nodes, its
    /// node), one batch per account; what the server derives for it (positions, daily values, statistics) goes with it.  Its
    /// institution, model and assets stay: they are reference data the account shared, and a script that loads the account
    /// again inserts them again unchanged.  The account's events stay in the log; the rules report them as events for an
    /// account that doesn't exist, and loading the account again (same key) brings it back holding what the log adds up to.
    /// </para>
    /// <para>
    /// Every step is idempotent: clearing a tenant that is already clear changes nothing.
    /// </para>
    /// </remarks>
    public static class TenantClearing
    {
        /// <summary>
        /// How long to wait for the rules to show the holdings (they run in cycles, and answer 409 until their first).
        /// </summary>
        public static readonly TimeSpan RulesTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Builds the transactions that take every holding out of its account, the way an account is closed: per account, one
        /// transaction that sells its securities at their current prices into its cash (one trade, which balances), then
        /// withdraws all of its cash.  So the deposits less the withdrawals (what the account was paid in) stay true, and an
        /// account loaded again later starts from them.  A security with no price, or an account with no cash in its own
        /// currency to sell into, is transferred out in kind instead.
        /// </summary>
        /// <param name="holdings">The tenant's holdings.</param>
        /// <param name="now">When the holdings leave.</param>
        /// <param name="newId">Makes each new key (the caller supplies every key).</param>
        /// <returns>Each account and its transaction, accounts with nothing to take out left out.</returns>
        public static IReadOnlyList<(Guid AccountId, TransactionMessage Transaction)> HoldingsOut(TenantHoldings holdings, DateTimeOffset now, Func<Guid> newId)
        {
            ArgumentNullException.ThrowIfNull(holdings);
            ArgumentNullException.ThrowIfNull(newId);
            var day = DateOnly.FromDateTime(now.UtcDateTime);
            var result = new List<(Guid, TransactionMessage)>();
            foreach (var account in holdings.Holdings.Where(holding => holding.Quantity != 0m).GroupBy(holding => holding.AccountId).OrderBy(group => group.Key))
            {
                var events = new List<EventMessage>();
                void Add(LedgerEvent value)
                {
                    var eventId = newId();
                    events.Add(EventMessages.From(value, eventId, EventSource.Operator, $"clear/{eventId}", now, sourceSequence: events.Count + 1));
                }

                var cash = account.Where(holding => holding.AssetType == AssetType.Cash).OrderBy(holding => holding.AssetId).ToArray();
                var proceedsInto = cash.FirstOrDefault(holding => holding.QuoteCurrency == holding.Currency);
                var securities = account.Where(holding => holding.AssetType != AssetType.Cash).OrderBy(holding => holding.AssetId).ToArray();
                var sold = proceedsInto is null ? Array.Empty<Holding>() : securities.Where(holding => holding.MarketValue is not null).ToArray();
                var proceeds = sold.Sum(holding => holding.MarketValue!.Value);
                if (sold.Length > 0)
                {
                    Add(new TradeEvent
                    {
                        AccountId = account.Key,
                        TradeDate = day,
                        Items = sold
                            .Select(holding => new ActivityItem(holding.AssetId, -holding.Quantity, -holding.MarketValue, holding.Price, PositionEffect.Closing))
                            .Append(new ActivityItem(proceedsInto!.AssetId, proceeds, proceeds))
                            .ToArray(),
                    });
                }

                var unsold = securities.Except(sold).ToArray();
                if (unsold.Length > 0)
                {
                    Add(new ReceiveAndDeliverEvent
                    {
                        AccountId = account.Key,
                        TradeDate = day,
                        Reason = DeliveryReason.TransferOut,
                        Items = unsold.Select(holding => new ActivityItem(holding.AssetId, -holding.Quantity, -holding.MarketValue, holding.Price, PositionEffect.Closing)).ToArray(),
                    });
                }

                var withdrawn = cash
                    .Select(holding => (holding.AssetId, Quantity: holding.Quantity + (holding == proceedsInto ? proceeds : 0m), Amount: (holding.MarketValue ?? holding.Quantity) + (holding == proceedsInto ? proceeds : 0m)))
                    .Where(item => item.Quantity != 0m)
                    .ToArray();
                if (withdrawn.Length > 0)
                {
                    Add(new CashMovementEvent
                    {
                        AccountId = account.Key,
                        TradeDate = day,
                        Items = withdrawn.Select(item => new ActivityItem(item.AssetId, -item.Quantity, -item.Amount)).ToArray(),
                    });
                }

                result.Add((account.Key, new TransactionMessage { TransactionId = newId(), Events = events }));
            }

            return result;
        }

        /// <summary>
        /// Builds the batch that deletes one account and what names it: its links to other nodes, its symbols, its brokerage
        /// row, the account, and its node, children before parents.
        /// </summary>
        /// <param name="accountId">The account.</param>
        /// <param name="rows">The tenant's rows, as read.</param>
        /// <returns>The batch (empty if the account is already gone).</returns>
        public static LedgerScript DeleteAccount(Guid accountId, AccountRows rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            var script = new LedgerScript();
            if (rows.Accounts.FirstOrDefault(account => account.AccountId == accountId) is not { } found)
            {
                return script;
            }

            foreach (var edge in rows.Edges.Where(edge => edge.ParentNodeId == accountId || edge.ChildNodeId == accountId))
            {
                script.Delete("Edge", new { edge.ParentNodeId, edge.ChildNodeId }, edge.RowVersion);
            }

            foreach (var symbol in rows.Symbols.Where(symbol => symbol.NodeId == accountId))
            {
                script.Delete("Symbol", new { symbol.Scheme, symbol.Code }, symbol.RowVersion);
            }

            foreach (var brokerage in rows.Brokerages.Where(brokerage => brokerage.AccountId == accountId))
            {
                script.Delete("Brokerage", new { brokerage.AccountId }, brokerage.RowVersion);
            }

            script.Delete("Account", new { found.AccountId }, found.RowVersion);
            if (rows.Nodes.FirstOrDefault(node => node.NodeId == accountId) is { } node)
            {
                script.Delete("Node", new { node.NodeId }, node.RowVersion);
            }

            return script;
        }

        /// <summary>
        /// Builds the batch that removes every user from a tenant (their member rows first), so no person sees it any more.
        /// </summary>
        /// <param name="members">The tenant's member rows.</param>
        /// <param name="users">The tenant's users.</param>
        /// <returns>The batch.</returns>
        public static LedgerScript RemoveUsers(IEnumerable<MemberDto> members, IEnumerable<UserDto> users)
        {
            ArgumentNullException.ThrowIfNull(members);
            ArgumentNullException.ThrowIfNull(users);
            var script = new LedgerScript();
            foreach (var member in members.OrderBy(member => member.UserId))
            {
                script.Delete("Member", new { member.UserId }, member.RowVersion);
            }

            foreach (var user in users.OrderBy(user => user.UserId))
            {
                script.Delete("User", new { user.UserId }, user.RowVersion);
            }

            return script;
        }

        /// <summary>
        /// Clears a tenant: takes every holding out, account by account, and waits until the rules show each account empty;
        /// then deletes each account; then, if asked, removes the tenant's users.
        /// </summary>
        /// <param name="reader">The API as a caller that may read the tenant and its users (in production, the operator).</param>
        /// <param name="writer">The API as a caller that may write them (in production, the script loader).  Each tool has only
        /// its own half: the operator never writes, and the script loader never reads.</param>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="holdingsOnly">True to take the holdings out and keep the accounts.</param>
        /// <param name="removeUsers">True to remove the tenant's users too.</param>
        /// <param name="pause">How long to wait between accounts, so a page open on the tenant can be watched.</param>
        /// <param name="output">Where to report each step.</param>
        /// <param name="cancellationToken">Cancels the clearing (steps already committed stay committed).</param>
        /// <returns>A task that completes when the tenant is clear.</returns>
        /// <exception cref="InvalidOperationException">The rules didn't show the holdings, or a transaction was refused.</exception>
        public static async Task ClearAsync(LedgerApiClient reader, LedgerApiClient writer, Guid tenantId, bool holdingsOnly, bool removeUsers, TimeSpan pause, TextWriter output, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(reader);
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentNullException.ThrowIfNull(output);
            var names = (await reader.ListAsync<NodeDto>(tenantId, "Node", cancellationToken).ConfigureAwait(false)).ToDictionary(node => node.NodeId, node => node.DisplayName);
            string NameOf(Guid accountId) => names.TryGetValue(accountId, out var name) && !string.IsNullOrWhiteSpace(name) ? $"{name} ({accountId})" : accountId.ToString();

            var accounts = await reader.ListAsync<AccountDto>(tenantId, "Account", cancellationToken).ConfigureAwait(false);
            if (accounts.Count > 0)
            {
                var holdings = await WaitForHoldingsAsync(reader, tenantId, _ => true, cancellationToken).ConfigureAwait(false);
                var transactions = HoldingsOut(holdings, DateTimeOffset.UtcNow, Guid.CreateVersion7);
                output.WriteLine(transactions.Count == 0 ? "No account holds anything." : $"Taking the holdings out of {transactions.Count} account(s).");
                foreach (var (accountId, transaction) in transactions)
                {
                    var result = await writer.SendTransactionAsync(tenantId, transaction, cancellationToken).ConfigureAwait(false);
                    if (result.Status is not (TransactionStatus.Committed or TransactionStatus.AlreadyCommitted))
                    {
                        throw new InvalidOperationException($"The transaction emptying {NameOf(accountId)} was refused: {string.Join(" ", result.Problems)}");
                    }

                    await WaitForHoldingsAsync(reader, tenantId, found => found.Holdings.All(holding => holding.AccountId != accountId || holding.Quantity == 0m), cancellationToken).ConfigureAwait(false);
                    output.WriteLine($"  {NameOf(accountId)}: holds nothing.");
                    await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!holdingsOnly)
            {
                var rows = await ReadAccountRowsAsync(reader, tenantId, cancellationToken).ConfigureAwait(false);
                output.WriteLine(rows.Accounts.Count == 0 ? "There are no accounts." : $"Deleting {rows.Accounts.Count} account(s).");
                foreach (var account in rows.Accounts.OrderBy(account => account.AccountId))
                {
                    // Read again before each: a version read before the pause may have moved on.
                    var current = await ReadAccountRowsAsync(reader, tenantId, cancellationToken).ConfigureAwait(false);
                    var script = DeleteAccount(account.AccountId, current);
                    if (script.Operations.Count > 0)
                    {
                        await writer.ApplyAsync(tenantId, script, cancellationToken).ConfigureAwait(false);
                    }

                    output.WriteLine($"  {NameOf(account.AccountId)}: deleted.");
                    await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
                }
            }

            if (removeUsers)
            {
                var script = RemoveUsers(
                    await reader.ListAsync<MemberDto>(tenantId, "Member", cancellationToken).ConfigureAwait(false),
                    await reader.ListAsync<UserDto>(tenantId, "User", cancellationToken).ConfigureAwait(false));
                if (script.Operations.Count > 0)
                {
                    await writer.ApplyAsync(tenantId, script, cancellationToken).ConfigureAwait(false);
                }

                output.WriteLine($"Removed {script.Operations.Count(operation => operation.Table == "User")} user(s): no person sees the tenant now.");
            }

            output.WriteLine($"{tenantId} is clear.");
        }

        private static async Task<AccountRows> ReadAccountRowsAsync(LedgerApiClient api, Guid tenantId, CancellationToken cancellationToken) =>
            new AccountRows(
                await api.ListAsync<AccountDto>(tenantId, "Account", cancellationToken).ConfigureAwait(false),
                await api.ListAsync<BrokerageDto>(tenantId, "Brokerage", cancellationToken).ConfigureAwait(false),
                await api.ListAsync<SymbolDto>(tenantId, "Symbol", cancellationToken).ConfigureAwait(false),
                await api.ListAsync<EdgeDto>(tenantId, "Edge", cancellationToken).ConfigureAwait(false),
                await api.ListAsync<NodeDto>(tenantId, "Node", cancellationToken).ConfigureAwait(false));

        // The holdings, once the rules show what is wanted; the rules answer 409 until their first cycle for the tenant.
        private static async Task<TenantHoldings> WaitForHoldingsAsync(LedgerApiClient api, Guid tenantId, Func<TenantHoldings, bool> isWanted, CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + RulesTimeout;
            while (true)
            {
                try
                {
                    var holdings = await api.GetHoldingsAsync(tenantId, cancellationToken).ConfigureAwait(false);
                    if (isWanted(holdings))
                    {
                        return holdings;
                    }
                }
                catch (LedgerApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict && DateTimeOffset.UtcNow < deadline)
                {
                    // The rules haven't run for the tenant yet.
                }
                catch (LedgerApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
                {
                    throw new InvalidOperationException($"The rules haven't shown {tenantId}'s holdings within {RulesTimeout.TotalSeconds:0} seconds: is the rules service on? (ledger-operator services)", exception);
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new InvalidOperationException($"The rules didn't show the change within {RulesTimeout.TotalSeconds:0} seconds.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
