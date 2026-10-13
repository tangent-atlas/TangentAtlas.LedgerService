// <copyright file="Program.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Server
{
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Builder;

    /// <summary>
    /// The Atlas Ledger server.
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// Runs the server until it is stopped (Ctrl+C).
        /// </summary>
        /// <param name="args">Command-line arguments, which override the settings files.</param>
        /// <returns>A task that completes when the server stops.</returns>
        private static async Task Main(string[] args)
        {
            await using var application = AtlasLedgerServer.Build(args);
            await application.RunAsync().ConfigureAwait(false);
        }
    }
}
