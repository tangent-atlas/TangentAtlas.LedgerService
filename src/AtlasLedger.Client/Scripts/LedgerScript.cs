// <copyright file="LedgerScript.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Scripts
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Client.Api;

    /// <summary>
    /// What a script operation does to its row.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ScriptAction
    {
        /// <summary>Inserts a row.</summary>
        Insert,

        /// <summary>Updates the row with the given key, at the version in the row.</summary>
        Update,

        /// <summary>Deletes the row with the given key, at the given version.</summary>
        Delete,
    }

    /// <summary>
    /// A JSON script of ledger operations: the format for loading and exporting a tenant's data.  The server applies a
    /// script as one batch, in one transaction, so all of it commits or none of it does.
    /// </summary>
    /// <remarks>
    /// Rows go in exactly as written, keys included: nothing is generated or transformed between the file and the ledger,
    /// so an exported script loads into another tenant unchanged.
    /// </remarks>
    public sealed class LedgerScript
    {
        /// <summary>
        /// Gets the operations, in order: parents before children.
        /// </summary>
        public List<ScriptOperation> Operations { get; init; } = new List<ScriptOperation>();

        /// <summary>
        /// Reads a script from a file.
        /// </summary>
        /// <param name="path">The file.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The script.</returns>
        public static async Task<LedgerScript> ReadAsync(string path, CancellationToken cancellationToken)
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<LedgerScript>(stream, LedgerApiClient.SerializerOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"{path} is not a ledger script.");
        }

        /// <summary>
        /// Writes the script.
        /// </summary>
        /// <param name="stream">Where to write it.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <returns>A task that completes when the script is written.</returns>
        public Task WriteAsync(Stream stream, CancellationToken cancellationToken) =>
            JsonSerializer.SerializeAsync(stream, this, new JsonSerializerOptions(LedgerApiClient.SerializerOptions) { WriteIndented = true }, cancellationToken);

        /// <summary>
        /// Adds an insert.
        /// </summary>
        /// <typeparam name="T">The row's type: a DTO or a JSON element.</typeparam>
        /// <param name="table">The table.</param>
        /// <param name="row">The row.</param>
        /// <returns>This script.</returns>
        public LedgerScript Insert<T>(string table, T row)
        {
            this.Operations.Add(new ScriptOperation { Action = ScriptAction.Insert, Table = table, Row = JsonSerializer.SerializeToElement(row, LedgerApiClient.SerializerOptions) });
            return this;
        }

        /// <summary>
        /// Adds an update.
        /// </summary>
        /// <typeparam name="TKey">The key's type: an anonymous object naming the key columns, for example <c>new { AccountId = id }</c>.</typeparam>
        /// <typeparam name="T">The row's type.</typeparam>
        /// <param name="table">The table.</param>
        /// <param name="key">The row's key.</param>
        /// <param name="row">The row's new values, with the version it was read at.</param>
        /// <returns>This script.</returns>
        public LedgerScript Update<TKey, T>(string table, TKey key, T row)
        {
            this.Operations.Add(new ScriptOperation
            {
                Action = ScriptAction.Update,
                Table = table,
                Key = JsonSerializer.SerializeToElement(key, LedgerApiClient.SerializerOptions),
                Row = JsonSerializer.SerializeToElement(row, LedgerApiClient.SerializerOptions),
            });
            return this;
        }

        /// <summary>
        /// Adds a delete.
        /// </summary>
        /// <typeparam name="TKey">The key's type.</typeparam>
        /// <param name="table">The table.</param>
        /// <param name="key">The row's key.</param>
        /// <param name="rowVersion">The version the row was read at.</param>
        /// <returns>This script.</returns>
        public LedgerScript Delete<TKey>(string table, TKey key, long rowVersion)
        {
            this.Operations.Add(new ScriptOperation { Action = ScriptAction.Delete, Table = table, Key = JsonSerializer.SerializeToElement(key, LedgerApiClient.SerializerOptions), RowVersion = rowVersion });
            return this;
        }
    }

    /// <summary>
    /// One operation in a script.
    /// </summary>
    public sealed class ScriptOperation
    {
        /// <summary>
        /// Gets or sets what the operation does.
        /// </summary>
        public ScriptAction Action { get; set; }

        /// <summary>
        /// Gets or sets the table.
        /// </summary>
        public string Table { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the key of the row to update or delete, as an object naming the key columns.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? Key { get; set; }

        /// <summary>
        /// Gets or sets the row to insert, or the new values of the row to update.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? Row { get; set; }

        /// <summary>
        /// Gets or sets the version of the row to delete.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long RowVersion { get; set; }
    }
}
