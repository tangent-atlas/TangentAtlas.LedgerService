// <copyright file="Program.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using AtlasLedger.Server.Persistence;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.Extensions.Configuration;

    /// <summary>
    /// The Atlas Ledger server.
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// The command that prepares the database and exits, instead of running the server.
        /// </summary>
        private const string PrepareDatabaseCommand = "prepare-database";

        /// <summary>
        /// Runs the server until it is stopped (Ctrl+C), or, given <c>prepare-database</c>, prepares the database and exits.
        /// </summary>
        /// <param name="args">Command-line arguments, which override the settings files.</param>
        /// <returns>A task that completes when the server stops.</returns>
        private static async Task Main(string[] args)
        {
            if (args.Length > 0 && args[0] == PrepareDatabaseCommand)
            {
                await PrepareDatabaseAsync(args[1..]).ConfigureAwait(false);
                return;
            }

            await using var application = AtlasLedgerServer.Build(args);
            await application.RunAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Checks that the schema's migrations have been applied and creates the writer's table types.  The release pipeline
        /// runs this after the migrations, as the migration identity, so the server's own identity never needs the right to
        /// create anything: at startup it finds every type already there.
        /// </summary>
        /// <param name="args">Command-line arguments: the connection string may be given as <c>--ConnectionStrings:Ledger</c>.</param>
        /// <returns>A task that completes when the database is ready.</returns>
        private static async Task PrepareDatabaseAsync(string[] args)
        {
            // The same source as the server's: ConnectionStrings__Ledger in the environment, or the command line.
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args).Build();
            var connectionString = configuration.GetConnectionString(PersistenceOptions.ConnectionName);
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new InvalidOperationException(
                    $"No connection string: set ConnectionStrings__{PersistenceOptions.ConnectionName} or pass --ConnectionStrings:{PersistenceOptions.ConnectionName}.");
            }

            await new SqlServerLedgerDatabase(connectionString).PrepareAsync(CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine("The database is prepared: the schema is present and every table type exists.");
        }
    }
}
