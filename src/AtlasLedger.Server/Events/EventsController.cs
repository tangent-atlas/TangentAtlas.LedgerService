// <copyright file="EventsController.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server.Events
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Model;
    using AtlasLedger.Model.Primary;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;

    /// <summary>
    /// The event log as a stream of transactions, both ways.  Send a transaction (JSON), or many (JSON Lines: one transaction
    /// per line, each committed or refused by itself, one answer line each); read the log the same way, from any sequence,
    /// and keep following it.  Every answer waits until what it reports is durable.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("v1/tenants/{tenantId:guid}/events")]
    public sealed class EventsController : ControllerBase
    {
        private readonly Ledger ledger;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventsController"/> class.
        /// </summary>
        /// <param name="ledger">The ledger.</param>
        public EventsController(Ledger ledger)
        {
            this.ledger = ledger;
        }

        /// <summary>
        /// Sends transactions.  As <c>application/json</c>, one transaction: 200 when committed (or already committed), 409
        /// when its key or an event was already used for something else, 422 listing every problem.  As
        /// <c>application/x-ndjson</c>, one transaction per line: 200, and one answer line per transaction, in order.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The answer.</returns>
        [HttpPost]
        [DisableRequestSizeLimit]
        [Authorize(Policy = LedgerAccessPolicies.EventWrite)]
        public async Task<IActionResult> PostAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            if (this.ledger.FindPartition(tenantId) is not { } partition)
            {
                return this.NotFound();
            }

            if (IsStream(this.Request.ContentType))
            {
                await this.PostStreamAsync(partition, cancellationToken).ConfigureAwait(false);
                return new EmptyResult();
            }

            TransactionMessage? transaction;
            try
            {
                transaction = await JsonSerializer.DeserializeAsync<TransactionMessage>(this.Request.Body, EventMessages.Options, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                return this.UnprocessableEntity(new TransactionResult(null, TransactionStatus.Refused, null, [$"The transaction can't be read: {exception.Message}"]));
            }

            if (transaction is null)
            {
                return this.UnprocessableEntity(new TransactionResult(null, TransactionStatus.Refused, null, ["The transaction is empty."]));
            }

            var outcome = await WriteAsync(partition, transaction, cancellationToken).ConfigureAwait(false);
            return outcome.Result.Status switch
            {
                TransactionStatus.Refused when outcome.IsConflict => this.Conflict(outcome.Result),
                TransactionStatus.Refused => this.UnprocessableEntity(outcome.Result),
                _ => this.Ok(outcome.Result),
            };
        }

        /// <summary>
        /// Reads the log as transactions, as JSON Lines: one transaction per line, each with its sequence.  Only durable
        /// transactions are read.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="after">The sequence already read (0, or omitted: from the start).</param>
        /// <param name="follow">Whether to keep the response open and send each transaction as it becomes durable.</param>
        /// <param name="cancellationToken">Cancels the request (the reader going away ends a follow).</param>
        /// <returns>Nothing: the lines are written to the response.</returns>
        [HttpGet]
        [Authorize(Policy = LedgerAccessPolicies.EventRead)]
        public async Task<IActionResult> GetAsync(Guid tenantId, [FromQuery] long after = 0, [FromQuery] bool follow = false, CancellationToken cancellationToken = default)
        {
            if (this.ledger.FindPartition(tenantId) is not { } partition)
            {
                return this.NotFound();
            }

            this.Response.ContentType = EventMessages.StreamMediaType;
            var writer = new StreamWriter(this.Response.Body, new UTF8Encoding(false));
            await using (writer.ConfigureAwait(false))
            {
                var position = after;
                try
                {
                    while (true)
                    {
                        var through = partition.DurableSequence;
                        foreach (var transaction in EventTransactions.Read(partition, position, through))
                        {
                            await writer.WriteAsync(EventMessages.WriteLine(transaction).AsMemory(), cancellationToken).ConfigureAwait(false);
                            await writer.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
                        }

                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                        position = Math.Max(position, through);
                        if (!follow)
                        {
                            break;
                        }

                        await partition.WaitForCommitAsync(position, cancellationToken).ConfigureAwait(false);
                        await partition.WaitUntilDurableAsync(partition.CommittedSequence, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The reader went away.
                }
            }

            return new EmptyResult();
        }

        private static bool IsStream(string? contentType) =>
            contentType is not null
            && (contentType.StartsWith(EventMessages.StreamMediaType, StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("application/jsonl", StringComparison.OrdinalIgnoreCase));

        private static async Task<TransactionOutcome> WriteAsync(LedgerPartition partition, TransactionMessage transaction, CancellationToken cancellationToken)
        {
            var outcome = EventTransactions.Write(partition, transaction);
            if (outcome.Sequence is { } sequence)
            {
                await partition.WaitUntilDurableAsync(sequence, cancellationToken).ConfigureAwait(false);
            }

            return outcome;
        }

        private async Task PostStreamAsync(LedgerPartition partition, CancellationToken cancellationToken)
        {
            // Lines are committed as they arrive; their answers go out in batches, each once its commits are durable, so a
            // long script waits for the flusher once per batch rather than once per line.
            const int Batch = 256;
            this.Response.ContentType = EventMessages.StreamMediaType;
            using var reader = new StreamReader(this.Request.Body, Encoding.UTF8);
            var writer = new StreamWriter(this.Response.Body, new UTF8Encoding(false));
            await using (writer.ConfigureAwait(false))
            {
                var pending = new List<TransactionResult>();
                var number = 0;
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    number++;
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        pending.Add(EventTransactions.Write(partition, EventMessages.ReadLine(line)).Result with { Line = number });
                    }
                    catch (Exception exception) when (exception is JsonException or FormatException)
                    {
                        pending.Add(new TransactionResult(null, TransactionStatus.Refused, null, [$"The transaction can't be read: {exception.Message}"], number));
                    }

                    if (pending.Count >= Batch)
                    {
                        await AnswerAsync(partition, writer, pending, cancellationToken).ConfigureAwait(false);
                    }
                }

                await AnswerAsync(partition, writer, pending, cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task AnswerAsync(LedgerPartition partition, StreamWriter writer, List<TransactionResult> pending, CancellationToken cancellationToken)
        {
            if (pending.Count == 0)
            {
                return;
            }

            if (pending.Max(result => result.Sequence) is { } sequence)
            {
                await partition.WaitUntilDurableAsync(sequence, cancellationToken).ConfigureAwait(false);
            }

            foreach (var result in pending)
            {
                await writer.WriteAsync(JsonSerializer.Serialize(result, EventMessages.Options).AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            pending.Clear();
        }
    }
}
