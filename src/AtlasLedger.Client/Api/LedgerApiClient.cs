// <copyright file="LedgerApiClient.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Api
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Net.Http.Json;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Client.Scripts;
    using AtlasLedger.Model;

    /// <summary>
    /// Calls the ledger server's API with the generated DTOs.  Every request carries the model's version and fingerprint,
    /// so a server built from a different model refuses it (426) instead of misreading it.
    /// </summary>
    /// <remarks>
    /// Written by hand for now; Bellerophon will generate the typed client from the model, and this class shows its shape.
    /// Routes follow the generated controllers: <c>v1/tenants/{tenantId}/{table}</c>, then the key's columns in order.
    /// </remarks>
    public sealed class LedgerApiClient
    {
        /// <summary>
        /// The server's default limit on a batch's size (<c>Batch:MaximumOperations</c>).
        /// </summary>
        public const int DefaultMaximumBatchOperations = 10_000;

        private const string ServicesPath = "v1/system/services";

        private readonly HttpClient httpClient;

        private readonly string? caller;

        private readonly Func<CancellationToken, Task<string>>? accessToken;

        /// <summary>
        /// Initializes a new instance of the <see cref="LedgerApiClient"/> class.
        /// </summary>
        /// <param name="httpClient">The HTTP client, with the server's address as its base address.</param>
        /// <param name="caller">The development caller to sign in as (see <see cref="DevelopmentCallers"/>), or null.</param>
        /// <param name="accessToken">Gets a bearer token for each request (see <see cref="ToolSignIn"/> and <see cref="PersonSignIn"/>), or null.  With neither
        /// a caller nor a token source, the client calls anonymously.</param>
        public LedgerApiClient(HttpClient httpClient, string? caller, Func<CancellationToken, Task<string>>? accessToken = null)
        {
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            this.caller = caller;
            this.accessToken = accessToken;
        }

        /// <summary>
        /// Gets the JSON settings the server uses: camel-case names, enums as strings.
        /// </summary>
        public static JsonSerializerOptions SerializerOptions { get; } = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

        /// <summary>
        /// Gets the caller this client signs in as.
        /// </summary>
        public string? Caller => this.caller;

        /// <summary>
        /// Waits until the server answers its health check, so the client can start before the server.
        /// </summary>
        /// <param name="timeout">How long to wait.</param>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>A task that completes when the server is ready.</returns>
        /// <exception cref="TimeoutException">The server did not answer in time.</exception>
        public async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (true)
            {
                try
                {
                    using var response = await this.httpClient.GetAsync("health", cancellationToken).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (HttpRequestException) when (DateTimeOffset.UtcNow < deadline)
                {
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException($"The ledger server at {this.httpClient.BaseAddress} did not answer within {timeout.TotalSeconds:0} seconds.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Lists the tenants the caller may open: the tenant picker.
        /// </summary>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The tenants' IDs.</returns>
        public async Task<IReadOnlyList<Guid>> GetTenantsAsync(CancellationToken cancellationToken = default) =>
            await this.SendAsync<List<Guid>>(HttpMethod.Get, "v1/me/tenants", null, cancellationToken).ConfigureAwait(false) ?? new List<Guid>();

        /// <summary>
        /// Lists a table's rows in a tenant.
        /// </summary>
        /// <typeparam name="T">The table's DTO.</typeparam>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="table">The table.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The rows.</returns>
        public async Task<IReadOnlyList<T>> ListAsync<T>(Guid tenantId, string table, CancellationToken cancellationToken = default) =>
            await this.SendAsync<List<T>>(HttpMethod.Get, TablePath(tenantId, table), null, cancellationToken).ConfigureAwait(false) ?? new List<T>();

        /// <summary>
        /// Lists a table's rows in a tenant as raw JSON, for export.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="table">The table.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The rows.</returns>
        public Task<IReadOnlyList<JsonElement>> ListRowsAsync(Guid tenantId, string table, CancellationToken cancellationToken = default) =>
            this.ListAsync<JsonElement>(tenantId, table, cancellationToken);

        /// <summary>
        /// Inserts a row.
        /// </summary>
        /// <typeparam name="T">The table's DTO.</typeparam>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="table">The table.</param>
        /// <param name="row">The row, with its key: the client always supplies keys.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The row as stored, with its version.</returns>
        public async Task<T> InsertAsync<T>(Guid tenantId, string table, T row, CancellationToken cancellationToken = default) =>
            (await this.SendAsync<T>(HttpMethod.Post, TablePath(tenantId, table), row, cancellationToken).ConfigureAwait(false))!;

        /// <summary>
        /// Updates a row at the version it was read at.
        /// </summary>
        /// <typeparam name="T">The table's DTO.</typeparam>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="table">The table.</param>
        /// <param name="key">The row's key columns, in order.</param>
        /// <param name="row">The row's new values, with the version it was read at.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The row as stored, with its new version.</returns>
        public async Task<T> UpdateAsync<T>(Guid tenantId, string table, object[] key, T row, CancellationToken cancellationToken = default) =>
            (await this.SendAsync<T>(HttpMethod.Put, RowPath(tenantId, table, key), row, cancellationToken).ConfigureAwait(false))!;

        /// <summary>
        /// Deletes a row at the version it was read at.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="table">The table.</param>
        /// <param name="key">The row's key columns, in order.</param>
        /// <param name="rowVersion">The version it was read at.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>A task that completes when the row is deleted.</returns>
        public Task DeleteAsync(Guid tenantId, string table, object[] key, long rowVersion, CancellationToken cancellationToken = default) =>
            this.SendAsync<JsonElement?>(HttpMethod.Delete, $"{RowPath(tenantId, table, key)}?rowVersion={rowVersion.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken);

        /// <summary>
        /// Applies a script as one batch: every operation commits, or none does.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="script">The script.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>Each operation's result, in order: the stored row for an insert or update, null for a delete.</returns>
        public async Task<IReadOnlyList<JsonElement>> ApplyAsync(Guid tenantId, LedgerScript script, CancellationToken cancellationToken = default) =>
            await this.SendAsync<List<JsonElement>>(HttpMethod.Post, $"v1/tenants/{tenantId}/batch", script, cancellationToken).ConfigureAwait(false) ?? new List<JsonElement>();

        /// <summary>
        /// Applies a script in order, in batches of at most <paramref name="maximumOperations"/> operations, since the server
        /// limits a batch's size (10,000 operations by default).  Each batch commits as a unit, but the script as a whole does
        /// not: if one batch fails, the batches before it stay applied.  Running a load script again is safe, since inserting
        /// a row identical to a stored one returns the stored row.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="script">The script.  Parents come before their children, as in an exported script.</param>
        /// <param name="maximumOperations">The most operations per batch: the server's limit or less.</param>
        /// <param name="cancellationToken">Cancels the requests.</param>
        /// <returns>Each operation's result, in order: the stored row for an insert or update, null for a delete.</returns>
        public async Task<IReadOnlyList<JsonElement>> ApplyInBatchesAsync(Guid tenantId, LedgerScript script, int maximumOperations = DefaultMaximumBatchOperations, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(script);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOperations);
            if (script.Operations.Count <= maximumOperations)
            {
                return await this.ApplyAsync(tenantId, script, cancellationToken).ConfigureAwait(false);
            }

            var results = new List<JsonElement>(script.Operations.Count);
            for (var start = 0; start < script.Operations.Count; start += maximumOperations)
            {
                var part = new LedgerScript();
                part.Operations.AddRange(script.Operations.Skip(start).Take(maximumOperations));
                results.AddRange(await this.ApplyAsync(tenantId, part, cancellationToken).ConfigureAwait(false));
            }

            return results;
        }

        /// <summary>
        /// Lists the server's optional services and whether each is on.  Only an allowed tool may.
        /// </summary>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The services.</returns>
        public async Task<IReadOnlyList<ServiceState>> GetServicesAsync(CancellationToken cancellationToken = default) =>
            await this.SendAsync<List<ServiceState>>(HttpMethod.Get, ServicesPath, null, cancellationToken).ConfigureAwait(false) ?? new List<ServiceState>();

        /// <summary>
        /// Turns one of the server's optional services on for a while, or off.  Only an allowed tool may.
        /// </summary>
        /// <param name="name">The service, for example <c>market-feed</c>.</param>
        /// <param name="isOn">True to turn it on; false to turn it off.</param>
        /// <param name="duration">How long to stay on; null for the server's default (four hours).</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The service, as it is now.</returns>
        public async Task<ServiceState> SetServiceAsync(string name, bool isOn, TimeSpan? duration = null, CancellationToken cancellationToken = default) =>
            (await this.SendAsync<ServiceState>(HttpMethod.Put, $"{ServicesPath}/{Uri.EscapeDataString(name)}", new { isOn, duration }, cancellationToken).ConfigureAwait(false))!;

        private static string TablePath(Guid tenantId, string table) => $"v1/tenants/{tenantId}/{RouteSegment(table)}";

        /// <summary>
        /// Converts a table name to its route segment, as the generated controllers do: <c>OrderIntent</c> to <c>order-intent</c>.
        /// </summary>
        private static string RouteSegment(string table)
        {
            var builder = new StringBuilder(table.Length + 4);
            for (var index = 0; index < table.Length; index++)
            {
                if (index > 0 && char.IsUpper(table[index]))
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(table[index]));
            }

            return builder.ToString();
        }

        private static string RowPath(Guid tenantId, string table, object[] key) =>
            $"{TablePath(tenantId, table)}/{string.Join("/", key.Select(column => Uri.EscapeDataString(Convert.ToString(column, CultureInfo.InvariantCulture) ?? string.Empty)))}";

        private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(method, path);
            Ledger.AddContractHeaders(request.Headers);
            if (this.caller != null)
            {
                request.Headers.Add(DevelopmentCallers.Header, this.caller);
            }

            if (this.accessToken != null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await this.accessToken(cancellationToken).ConfigureAwait(false));
            }

            if (body != null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: SerializerOptions);
            }

            using var response = await this.httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new LedgerApiException(response.StatusCode, Describe(method, path, response, text), text.Length == 0 ? null : text);
            }

            return text.Length == 0 ? default : JsonSerializer.Deserialize<T>(text, SerializerOptions);
        }

        private static string Describe(HttpMethod method, string path, HttpResponseMessage response, string text)
        {
            if (response.StatusCode == HttpStatusCode.UpgradeRequired)
            {
                var version = response.Headers.TryGetValues(Ledger.VersionHeader, out var versions) ? versions.FirstOrDefault() : "unknown";
                var fingerprint = response.Headers.TryGetValues(Ledger.FingerprintHeader, out var fingerprints) ? fingerprints.FirstOrDefault() : "unknown";
                return $"The server runs model {version} ({fingerprint}), but this client was built from model {Ledger.Version} ({Ledger.Fingerprint}). Rebuild the client from the server's model.";
            }

            var reason = text.Length == 0 ? string.Empty : $": {text}";
            return $"{method} {path} answered {(int)response.StatusCode} {response.StatusCode}{reason}";
        }
    }
}
