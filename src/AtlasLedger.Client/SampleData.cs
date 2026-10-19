// <copyright file="SampleData.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Client
{
    using System;
    using System.IO;

    /// <summary>
    /// The sample scripts, copied beside every program and test that references the client library.
    /// </summary>
    public static class SampleData
    {
        /// <summary>
        /// Gets the folder that holds the sample scripts.
        /// </summary>
        public static string Folder => Path.Combine(AppContext.BaseDirectory, "Data");

        /// <summary>
        /// Gets firm A: two accounts, each with a custodian number.  No users: the walkthrough makes the signed-in person
        /// a user of the firm.
        /// </summary>
        public static string FirmA => Path.Combine(Folder, "firm-a.json");

        /// <summary>
        /// Gets firm B: one account, and no users, so a person who is not a tool sees nothing of it.
        /// </summary>
        public static string FirmB => Path.Combine(Folder, "firm-b.json");

        /// <summary>
        /// Gets the Jones IRA, an account with no custodian number, for the compliance engine to flag.
        /// </summary>
        public static string JonesIra => Path.Combine(Folder, "jones-ira.json");

        /// <summary>
        /// Gets the Jones IRA's custodian number, which clears the compliance engine's alert.
        /// </summary>
        public static string JonesIraCustodian => Path.Combine(Folder, "jones-ira-custodian.json");
    }
}
