// <copyright file="HoldingsSnapshot.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Holdings
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using Bellerophon.Rules;
    using Bellerophon.Rules.NRules;

    /// <summary>
    /// What a tenant's events added up to at the end of a rules cycle: immutable, so readers never wait on the rules or the
    /// ledger's lock (architecture §8.3: derived values come from the engine's published snapshot).
    /// </summary>
    /// <param name="TenantId">The tenant.</param>
    /// <param name="AsOf">When the cycle ran.</param>
    /// <param name="Positions">Every holding, by account and security.</param>
    /// <param name="Cash">Every account's cash.</param>
    /// <param name="Prices">Every priced security's effective price.</param>
    public sealed record HoldingsSnapshot(
        Guid TenantId,
        DateTimeOffset AsOf,
        IReadOnlyList<PositionState> Positions,
        IReadOnlyList<CashState> Cash,
        IReadOnlyList<EffectivePrice> Prices);

    /// <summary>
    /// The latest snapshot of each tenant.  In memory only: derived state is never persisted, and it is rebuilt from the
    /// events when the rules start again.
    /// </summary>
    public sealed class HoldingsStore
    {
        private readonly ConcurrentDictionary<Guid, HoldingsSnapshot> snapshots = new ConcurrentDictionary<Guid, HoldingsSnapshot>();

        /// <summary>
        /// Replaces a tenant's snapshot.
        /// </summary>
        /// <param name="snapshot">The new snapshot.</param>
        public void Publish(HoldingsSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            this.snapshots[snapshot.TenantId] = snapshot;
        }

        /// <summary>
        /// Finds a tenant's latest snapshot.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <returns>The snapshot, or null if the rules haven't run for the tenant since the server started.</returns>
        public HoldingsSnapshot? Find(Guid tenantId) => this.snapshots.TryGetValue(tenantId, out var snapshot) ? snapshot : null;
    }

    /// <summary>
    /// Makes each tenant's NRules session, and after every cycle publishes what the holdings rules derived.  It also keeps
    /// the session's clock fact, moved on once a minute, so overrides expire.
    /// </summary>
    public sealed class HoldingsSessionFactory : IRulesSessionFactory
    {
        private readonly NRulesSessionFactory inner;

        private readonly HoldingsStore store;

        private readonly TimeProvider timeProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="HoldingsSessionFactory"/> class.
        /// </summary>
        /// <param name="inner">The factory of the NRules sessions, holding every rule (compliance and holdings).</param>
        /// <param name="store">Where snapshots are published.</param>
        /// <param name="timeProvider">The clock.</param>
        public HoldingsSessionFactory(NRulesSessionFactory inner, HoldingsStore store, TimeProvider timeProvider)
        {
            this.inner = inner;
            this.store = store;
            this.timeProvider = timeProvider;
        }

        /// <inheritdoc/>
        public IRulesSession CreateSession(IRulesTenant tenant)
        {
            ArgumentNullException.ThrowIfNull(tenant);
            return new HoldingsSession((NRulesSession)this.inner.CreateSession(tenant), tenant.TenantId, this.store, this.timeProvider);
        }

        private sealed class HoldingsSession : IRulesSession
        {
            private readonly NRulesSession inner;

            private readonly Guid tenantId;

            private readonly HoldingsStore store;

            private readonly TimeProvider timeProvider;

            private ClockFact? clock;

            public HoldingsSession(NRulesSession inner, Guid tenantId, HoldingsStore store, TimeProvider timeProvider)
            {
                this.inner = inner;
                this.tenantId = tenantId;
                this.store = store;
                this.timeProvider = timeProvider;
            }

            public void Apply(IReadOnlyList<FactChange> changes) => this.inner.Apply(changes);

            public int Fire()
            {
                var now = this.timeProvider.GetUtcNow();
                var minute = new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerMinute), TimeSpan.Zero);
                if (this.clock is null)
                {
                    this.clock = new ClockFact(minute);
                    this.inner.Session.Insert(this.clock);
                }
                else if (this.clock.Now != minute)
                {
                    this.clock = new ClockFact(minute);
                    this.inner.Session.Update(this.clock);
                }

                var fired = this.inner.Fire();
                var session = this.inner.Session;
                this.store.Publish(new HoldingsSnapshot(
                    this.tenantId,
                    now,
                    session.Query<PositionState>().ToArray(),
                    session.Query<CashState>().ToArray(),
                    session.Query<EffectivePrice>().ToArray()));
                return fired;
            }
        }
    }
}
