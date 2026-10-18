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
    /// Where the client finds its server and how it signs in, from <c>ledger-client.json</c>: written on the operator's
    /// computer by TangentAtlas.Platform's Deploy-EntraApplications.ps1, so command lines and scripts need no settings of
    /// their own.  Nothing in it is secret: the tool proves who it is with a certificate in the operator's certificate
    /// store, which the file names by thumbprint.
    /// </summary>
    /// <remarks>
    /// The file is <c>%APPDATA%\TangentAtlas\ledger-client.json</c> (<c>~/.config/TangentAtlas/ledger-client.json</c>
    /// elsewhere), or the path in <c>ATLAS_LEDGER_CLIENT_SETTINGS</c>.  Without it, the client talks to a development
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
        /// Gets or sets how the tool signs in to Entra External ID, or null for development sign-in.
        /// </summary>
        public ToolSignInSettings? SignIn { get; set; }

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
