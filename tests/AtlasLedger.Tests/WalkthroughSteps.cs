// <copyright file="WalkthroughSteps.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias client;

namespace AtlasLedger.Tests
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using client::AtlasLedger.AccessTest;
    using client::AtlasLedger.Client;
    using client::AtlasLedger.Client.Api;
    using client::AtlasLedger.Client.Scripts;
    using client::AtlasLedger.Model;

    /// <summary>
    /// The walkthrough's steps, in one process: what Test-LedgerDeployment.ps1 does against a deployed server with the
    /// three programs, here with their API clients directly.  The script loader loads, the operator reads and switches,
    /// and access-test acts as a person.
    /// </summary>
    internal static class WalkthroughSteps
    {
        /// <summary>
        /// Runs every step, as the PowerShell walkthrough does.
        /// </summary>
        /// <param name="loader">The script loader's API.</param>
        /// <param name="operatorApi">The operator console's API.</param>
        /// <param name="person">access-test's API, signed in as a person.</param>
        /// <param name="anonymous">The API with no sign-in.</param>
        /// <param name="identity">The person's issuer and subject (what access-test whoami prints).</param>
        /// <param name="marketWait">How long to wait for the market feed's bad print.</param>
        /// <param name="cancellationToken">Cancels the walkthrough.</param>
        /// <returns>The report, and whether every step got what it expected.</returns>
        public static async Task<(bool Passed, string Report)> RunAsync(LedgerApiClient loader, LedgerApiClient operatorApi, LedgerApiClient person, LedgerApiClient anonymous, (string Issuer, string Subject) identity, TimeSpan marketWait, CancellationToken cancellationToken)
        {
            using var output = new StringWriter();
            var passed = true;
            var (firmA, firmB, firmC) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            await Commands.ServicesAsync(operatorApi, "on", Array.Empty<string>(), TimeSpan.FromMinutes(15), output, cancellationToken);
            try
            {
                await Commands.LoadAsync(loader, firmA, SampleData.FirmA, output, cancellationToken);
                await Commands.LoadAsync(loader, firmB, SampleData.FirmB, output, cancellationToken);
                await loader.ApplyAsync(firmA, new LedgerScript().Insert("User", new UserDto { UserId = Guid.NewGuid(), IdentityIssuer = identity.Issuer, IdentitySubject = identity.Subject }), cancellationToken);

                passed &= await new AccessChecks(person, anonymous, output).RunAsync(firmA, firmB, cancellationToken) == 0;

                var exported = await Commands.ExportAsync(operatorApi, firmA, cancellationToken);
                await loader.ApplyAsync(firmC, exported, cancellationToken);
                passed &= Counts(await Commands.ExportAsync(operatorApi, firmC, cancellationToken)) == Counts(exported);

                await Commands.LoadAsync(loader, firmA, SampleData.JonesIra, output, cancellationToken);
                passed &= await Commands.WaitForAlertAsync(operatorApi, firmA, "MissingCustodian", true, TimeSpan.FromSeconds(10), output, cancellationToken);
                await Commands.LoadAsync(loader, firmA, SampleData.JonesIraCustodian, output, cancellationToken);
                passed &= await Commands.WaitForAlertAsync(operatorApi, firmA, "MissingCustodian", false, TimeSpan.FromSeconds(10), output, cancellationToken);

                passed &= await Commands.WaitForAlertAsync(operatorApi, firmA, "InvalidPrice", true, marketWait, output, cancellationToken);
                passed &= await Commands.WaitForAlertAsync(operatorApi, firmA, "InvalidPrice", false, TimeSpan.FromSeconds(10), output, cancellationToken);

                await Commands.MirrorAsync(operatorApi, firmA, output, cancellationToken);
            }
            finally
            {
                await Commands.ServicesAsync(operatorApi, "off", Array.Empty<string>(), null, output, CancellationToken.None);
            }

            return (passed, output.ToString());
        }

        /// <summary>
        /// Counts an export's rows per table, leaving out alerts, which the rules raise and clear on their own.
        /// </summary>
        private static string Counts(LedgerScript script) => string.Join(", ", script.Operations
            .Where(operation => operation.Table != "Alert")
            .GroupBy(operation => operation.Table, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key} {group.Count()}"));
    }
}
