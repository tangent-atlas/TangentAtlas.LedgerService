// <copyright file="Model.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias server;

namespace AtlasLedger.Persistence.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text.Json;
    using AtlasLedger.Model;
    using server::AtlasLedger.Model.Primary;
    using MemberDto = server::AtlasLedger.Model.MemberDto;
    using NodeDto = server::AtlasLedger.Model.NodeDto;

    /// <summary>
    /// Builds, changes, and compares tenants of the sample model for the database tests: every durable table, nullable
    /// columns both set and empty, and every enumeration.
    /// </summary>
    internal static class Model
    {
        private const string Issuer = "https://example.ciamlogin.com/example/v2.0";

        /// <summary>
        /// Creates a node.
        /// </summary>
        /// <param name="name">Its display name.</param>
        /// <param name="type">Its type.</param>
        /// <returns>The node, not yet inserted.</returns>
        public static Node NewNode(string name, NodeType type = NodeType.Account) =>
            new Node { NodeId = Guid.NewGuid(), DisplayName = name, FullName = name + " (full)", NodeStatus = NodeStatus.Active, NodeType = type };

        /// <summary>
        /// Fills a tenant with a few rows of every durable table.
        /// </summary>
        /// <param name="partition">The empty partition.</param>
        /// <returns>The keys the other operations work with.</returns>
        public static Keys Populate(LedgerPartition partition)
        {
            var keys = new Keys();
            using var transaction = partition.BeginTransaction();
            for (var index = 0; index < 4; index++)
            {
                var node = NewNode("Account" + index);
                node.Description = index % 2 == 0 ? null : "Described " + index;
                keys.Accounts.Add(transaction.InsertNode(node).NodeId);
                transaction.InsertAccount(new Account { AccountId = node.NodeId, AccountType = (AccountType)(index % 3) });
                transaction.InsertSymbol(new Symbol { Scheme = "Custodian", Code = "C" + index.ToString(CultureInfo.InvariantCulture), NodeId = node.NodeId });
            }

            for (var index = 0; index < 2; index++)
            {
                var node = transaction.InsertNode(NewNode("Person" + index, NodeType.Person));
                keys.Persons.Add(node.NodeId);
                transaction.InsertPerson(new Person { PersonId = node.NodeId, Email = $"person{index}@example.com" });
                var user = transaction.InsertUser(new User { UserId = Guid.NewGuid(), IdentityIssuer = Issuer, IdentitySubject = Guid.NewGuid().ToString() });
                keys.Users.Add(user.UserId);
                transaction.InsertMember(new Member { UserId = user.UserId, PersonId = index == 0 ? node.NodeId : null, UserType = (UserType)index });
            }

            transaction.InsertDossier(new Dossier { PersonId = keys.Persons[0] });
            transaction.InsertEdge(new Edge { ParentNodeId = keys.Accounts[0], ChildNodeId = keys.Accounts[1] });
            transaction.InsertEdge(new Edge { ParentNodeId = keys.Accounts[0], ChildNodeId = keys.Persons[0] });
            transaction.InsertAlert(new Alert { AlertId = Guid.NewGuid(), NodeId = keys.Accounts[2], Rule = "Cap", Message = "Over the cap." });
            transaction.Commit();
            return keys;
        }

        /// <summary>
        /// Changes a populated tenant in one transaction: updates, deletes, a nullable column cleared and another set, an
        /// alert cleared and raised again on the same node and rule, and a symbol key deleted and inserted again.
        /// </summary>
        /// <param name="partition">The partition.</param>
        /// <param name="keys">Its keys.</param>
        public static void Change(LedgerPartition partition, Keys keys)
        {
            using var transaction = partition.BeginTransaction();
            var described = partition.FindNode(keys.Accounts[1])!;
            transaction.UpdateNode(Changed(described, dto => { dto.DisplayName = "Renamed"; dto.Description = null; }));
            var plain = partition.FindNode(keys.Accounts[0])!;
            transaction.UpdateNode(Changed(plain, dto => dto.Description = "Now described"));
            var member = partition.FindMember(keys.Users[1])!;
            transaction.UpdateMember(Changed(member, dto => { dto.PersonId = keys.Persons[1]; dto.UserType = UserType.Group; }));

            var alert = partition.Alerts.Single();
            transaction.DeleteAlert(alert.AlertId, alert.RowVersion);
            transaction.InsertAlert(new Alert { AlertId = Guid.NewGuid(), NodeId = alert.NodeId, Rule = alert.Rule, Message = "Over the cap again." });

            var symbol = partition.FindSymbol("Custodian", "C3")!;
            transaction.DeleteSymbol(symbol.Scheme, symbol.Code, symbol.RowVersion);
            transaction.InsertSymbol(new Symbol { Scheme = "Custodian", Code = "C3", NodeId = keys.Accounts[2] });
            var gone = partition.FindSymbol("Custodian", "C2")!;
            transaction.DeleteSymbol(gone.Scheme, gone.Code, gone.RowVersion);
            var edge = partition.FindEdge(keys.Accounts[0], keys.Accounts[1])!;
            transaction.DeleteEdge(edge.ParentNodeId, edge.ChildNodeId, edge.RowVersion);
            transaction.InsertSymbol(new Symbol { Scheme = "Exchange", Code = "NEW", NodeId = keys.Accounts[3] });
            transaction.Commit();
        }

        /// <summary>
        /// Makes one random change, as the recovery drill does: alerts raised and cleared on the same node and rule, symbol
        /// keys deleted and inserted again (sometimes under another node), renamed nodes, membership changes, and
        /// transactions that roll back.
        /// </summary>
        /// <param name="partition">The partition.</param>
        /// <param name="keys">Its keys.</param>
        /// <param name="random">The random source.</param>
        /// <param name="step">The step number.</param>
        public static void RandomStep(LedgerPartition partition, Keys keys, Random random, int step)
        {
            var node = keys.Accounts[random.Next(keys.Accounts.Count)];
            var rule = random.Next(2) == 0 ? "Cap" : "Wash";
            var code = "R" + random.Next(4).ToString(CultureInfo.InvariantCulture);
            switch (random.Next(6))
            {
                case 0:
                    partition.InsertAlert(new Alert { AlertId = Guid.NewGuid(), NodeId = node, Rule = rule, Message = "Step " + step.ToString(CultureInfo.InvariantCulture) });
                    break;
                case 1:
                    if (partition.FindAlertBySubject(node, rule) is { } alert)
                    {
                        partition.DeleteAlert(alert.AlertId, alert.RowVersion);
                    }

                    break;
                case 2:
                    if (partition.FindSymbol("Random", code) is { } symbol)
                    {
                        partition.DeleteSymbol(symbol.Scheme, symbol.Code, symbol.RowVersion);
                    }
                    else
                    {
                        partition.InsertSymbol(new Symbol { Scheme = "Random", Code = code, NodeId = node });
                    }

                    break;
                case 3:
                    var current = partition.FindNode(node)!;
                    var described = random.Next(2) == 0;
                    partition.UpdateNode(Changed(current, dto => { dto.DisplayName = "S" + step.ToString(CultureInfo.InvariantCulture); dto.Description = described ? "D" + step.ToString(CultureInfo.InvariantCulture) : null; }));
                    break;
                case 4:
                    var member = partition.FindMember(keys.Users[random.Next(keys.Users.Count)])!;
                    var person = random.Next(2) == 0 ? (Guid?)null : keys.Persons[random.Next(keys.Persons.Count)];
                    var type = (UserType)random.Next(3);
                    partition.UpdateMember(Changed(member, dto => { dto.PersonId = person; dto.UserType = type; }));
                    break;
                default:
                    using (var transaction = partition.BeginTransaction())
                    {
                        transaction.InsertSymbol(new Symbol { Scheme = "RolledBack", Code = code, NodeId = node });
                    }

                    break;
            }
        }

        /// <summary>
        /// Describes every durable row of a tenant, versions included, in a fixed order.
        /// </summary>
        /// <param name="partition">The partition.</param>
        /// <returns>One line per row.</returns>
        public static IReadOnlyList<string> Snapshot(LedgerPartition partition)
        {
            lock (partition.SyncRoot)
            {
                return partition.Nodes.Select(row => Line("Node", row.ToDto()))
                    .Concat(partition.Accounts.Select(row => Line("Account", row.ToDto())))
                    .Concat(partition.Alerts.Select(row => Line("Alert", row.ToDto())))
                    .Concat(partition.Edges.Select(row => Line("Edge", row.ToDto())))
                    .Concat(partition.Persons.Select(row => Line("Person", row.ToDto())))
                    .Concat(partition.Dossiers.Select(row => Line("Dossier", row.ToDto())))
                    .Concat(partition.Symbols.Select(row => Line("Symbol", row.ToDto())))
                    .Concat(partition.Users.Select(row => Line("User", row.ToDto())))
                    .Concat(partition.Members.Select(row => Line("Member", row.ToDto())))
                    .OrderBy(line => line, StringComparer.Ordinal)
                    .ToList();
            }
        }

        /// <summary>
        /// Builds the changes for an update, the way code outside the generated assembly does: the row's data transfer
        /// object, changed, carrying the version it was read at.
        /// </summary>
        /// <param name="row">The stored row.</param>
        /// <param name="change">The change.</param>
        /// <returns>The changes.</returns>
        public static Node Changed(Node row, Action<NodeDto> change)
        {
            var dto = row.ToDto();
            change(dto);
            return Node.FromDto(dto);
        }

        /// <summary>
        /// Builds the changes for an update of a member.
        /// </summary>
        /// <param name="row">The stored row.</param>
        /// <param name="change">The change.</param>
        /// <returns>The changes.</returns>
        public static Member Changed(Member row, Action<MemberDto> change)
        {
            var dto = row.ToDto();
            change(dto);
            return Member.FromDto(dto);
        }

        private static string Line<T>(string table, T dto) => table + " " + JsonSerializer.Serialize(dto);

        /// <summary>
        /// The keys of a populated tenant.
        /// </summary>
        internal sealed class Keys
        {
            /// <summary>
            /// Gets the account nodes.
            /// </summary>
            public List<Guid> Accounts { get; } = new List<Guid>();

            /// <summary>
            /// Gets the person nodes.
            /// </summary>
            public List<Guid> Persons { get; } = new List<Guid>();

            /// <summary>
            /// Gets the users.
            /// </summary>
            public List<Guid> Users { get; } = new List<Guid>();
        }
    }
}
