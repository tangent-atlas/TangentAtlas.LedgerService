// <copyright file="ClientSettings.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client.Api
{
    using System;
    using System.IO;
    using System.Text.Json;

    /// <summary>
    /// Where the client programs find their server and how they sign in, from <c>ledger-client.json</c>: written on the
    /// operator's computer by TangentAtlas.Platform's Deploy-EntraApplications.ps1, so command lines and scripts need no
    /// settings of their own.  Nothing in it is secret: the tools prove who they are with a certificate in the operator's
    /// certificate store, which the file names by thumbprint, and access-test signs a person in through the browser.
    /// </summary>
    /// <remarks>
    /// The file is <c>%APPDATA%\TangentAtlas\ledger-client.json</c> (<c>~/.config/TangentAtlas/ledger-client.json</c>
    /// elsewhere), or the path in <c>ATLAS_LEDGER_CLIENT_SETTINGS</c>.  Without it, the programs talk to a development
    /// server on this computer with development sign-in.
    /// </remarks>
    public sealed class ClientSettings
    {
        /// <summary>
        /// The environment variable naming a settings file other than the default.
        /// </summary>
        public const string PathVariable = "ATLAS_LEDGER_CLIENT_SETTINGS";

        /// <summary>
        /// Gets or sets the server's address.
        /// </summary>
        public string? Server { get; set; }

        /// <summary>
        /// Gets or sets the external tenant's sign-in address, <c>https://&lt;tenant ID&gt;.ciamlogin.com/&lt;tenant ID&gt;</c>;
        /// null for development sign-in.
        /// </summary>
        public string? Authority { get; set; }

        /// <summary>
        /// Gets or sets the Ledger API's application ID URI, <c>api://&lt;client ID&gt;</c>, from which the programs name
        /// the permissions they ask for.
        /// </summary>
        public string? ApiUri { get; set; }

        /// <summary>
        /// Gets or sets the thumbprint of the tools' certificate, in the current user's personal certificate store.  Its
        /// private key never leaves this computer; Entra holds only the public half, on the script loader and the operator
        /// console.
        /// </summary>
        public string? CertificateThumbprint { get; set; }

        /// <summary>
        /// Gets or sets the script loader's client ID.
        /// </summary>
        public string? ScriptLoaderClientId { get; set; }

        /// <summary>
        /// Gets or sets the operator console's client ID.
        /// </summary>
        public string? OperatorClientId { get; set; }

        /// <summary>
        /// Gets or sets access-test's client ID.
        /// </summary>
        public string? AccessTestClientId { get; set; }

        /// <summary>
        /// Gets the settings file's path.
        /// </summary>
        public static string DefaultPath =>
            Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } path
                ? path
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TangentAtlas", "ledger-client.json");

        /// <summary>
        /// Reads the settings file, if there is one.
        /// </summary>
        /// <param name="path">The file.</param>
        /// <returns>The settings, or empty settings if the file does not exist.</returns>
        public static ClientSettings Read(string path)
        {
            if (!File.Exists(path))
            {
                return new ClientSettings();
            }

            try
            {
                return JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(path), LedgerApiClient.SerializerOptions) ?? new ClientSettings();
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"{path} is not valid client settings: {exception.Message}", exception);
            }
        }
    }
}
