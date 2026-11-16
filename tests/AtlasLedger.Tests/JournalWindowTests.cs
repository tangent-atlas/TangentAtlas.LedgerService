// <copyright file="JournalWindowTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias server;

namespace AtlasLedger.Tests
{
    using System;
    using System.Collections.Generic;
    using server::AtlasLedger.Server.Compliance;
    using Xunit;

    /// <summary>
    /// The journal keeps a window of changes for sync clients: it is trimmed only through what was committed at least the
    /// window's length ago.
    /// </summary>
    public sealed class JournalWindowTests
    {
        private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

        private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 11, 16, 15, 0, 0, TimeSpan.Zero);

        /// <summary>
        /// Nothing is trimmed until a mark is the window's length old; then through the newest such mark.
        /// </summary>
        [Fact]
        public void TrimsOnlyThroughWhatTheWindowHasPassed()
        {
            var window = new JournalWindow(TimeSpan.FromMinutes(10));

            Assert.Equal(0, window.Mark(Tenant, 100, Start));
            Assert.Equal(0, window.Mark(Tenant, 150, Start.AddMinutes(5)));
            Assert.Equal(100, window.Mark(Tenant, 200, Start.AddMinutes(10)));
            Assert.Equal(150, window.Mark(Tenant, 260, Start.AddMinutes(16)));
            Assert.Equal(0, window.Mark(Tenant, 270, Start.AddMinutes(17)));   // nothing newer has passed: no further trim
        }

        /// <summary>
        /// A window of zero keeps nothing extra: the journal may be trimmed through what is committed now.
        /// </summary>
        [Fact]
        public void AZeroWindowKeepsNothingExtra()
        {
            var window = new JournalWindow(TimeSpan.Zero);

            Assert.Equal(100, window.Mark(Tenant, 100, Start));
        }

        /// <summary>
        /// Each tenant has its own marks, and a tenant that is gone is forgotten.
        /// </summary>
        [Fact]
        public void TenantsAreMarkedApart()
        {
            var other = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
            var window = new JournalWindow(TimeSpan.FromMinutes(1));
            window.Mark(Tenant, 10, Start);
            window.Mark(other, 900, Start);

            Assert.Equal(10, window.Mark(Tenant, 20, Start.AddMinutes(1)));
            window.Keep(new HashSet<Guid> { Tenant });
            Assert.Equal(0, window.Mark(other, 950, Start.AddMinutes(2)));      // its old marks are gone
        }
    }
}
