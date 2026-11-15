// <copyright file="InitialPrices.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Market
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using Microsoft.Extensions.Hosting;

    /// <summary>
    /// A price the market feed doesn't supply, published from the server's initial prices: the price of one unit of the
    /// symbol, in units of the currency (a currency's price in itself is 1).
    /// </summary>
    /// <param name="Scheme">The symbol's scheme.</param>
    /// <param name="Code">The symbol's code.</param>
    /// <param name="Currency">The currency the price is in (ISO 4217).</param>
    /// <param name="Last">The price.</param>
    public sealed record InitialPrice(string Scheme, string Code, string Currency, decimal Last);

    /// <summary>
    /// Publishes the initial prices (<c>Market/initial-prices.json</c>, embedded in the server) into every tenant's price
    /// store: the tenants loaded at startup, and each tenant as it is created.  Prices are never persisted, so they are
    /// published again after every restart.  Registered after recovery, so the loaded tenants are there when it starts.
    /// </summary>
    public sealed class InitialPriceService : IHostedService
    {
        private const string ResourceName = "AtlasLedger.Server.Market.initial-prices.json";

        private readonly Ledger ledger;

        private readonly TimeProvider timeProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="InitialPriceService"/> class.
        /// </summary>
        /// <param name="ledger">The ledger.</param>
        /// <param name="timeProvider">The clock, for each price's time.</param>
        public InitialPriceService(Ledger ledger, TimeProvider timeProvider)
        {
            this.ledger = ledger;
            this.timeProvider = timeProvider;
            this.Prices = Load();
        }

        /// <summary>
        /// Gets the initial prices.
        /// </summary>
        public IReadOnlyList<InitialPrice> Prices { get; }

        /// <summary>
        /// Reads the initial prices embedded in the server.
        /// </summary>
        /// <returns>The prices.</returns>
        public static IReadOnlyList<InitialPrice> Load()
        {
            using var stream = typeof(InitialPriceService).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException($"The server has no {ResourceName}.");
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.GetProperty("prices").EnumerateArray()
                .Select(price => new InitialPrice(
                    price.GetProperty("scheme").GetString()!,
                    price.GetProperty("code").GetString()!,
                    price.GetProperty("currency").GetString()!,
                    price.GetProperty("last").GetDecimal()))
                .ToArray();
        }

        /// <inheritdoc/>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            this.ledger.PartitionCreated += this.OnPartitionCreated;
            foreach (var partition in this.ledger.Partitions)
            {
                this.Publish(partition);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            this.ledger.PartitionCreated -= this.OnPartitionCreated;
            return Task.CompletedTask;
        }

        private void OnPartitionCreated(object? sender, LedgerPartition partition) => this.Publish(partition);

        // Publishing to the latest-value store takes no lock, so it is safe from the partition-created event.
        private void Publish(LedgerPartition partition)
        {
            var now = this.timeProvider.GetUtcNow();
            foreach (var price in this.Prices)
            {
                partition.PublishPrice(new Price { Scheme = price.Scheme, Code = price.Code, Currency = price.Currency, Last = price.Last, AsOf = now });
            }
        }
    }
}
