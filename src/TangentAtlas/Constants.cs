// <copyright file="Constants.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Model
{
    /// <summary>
    /// The schemes of the Symbol table: the systems of names a node can be known by.
    /// </summary>
    public static class SymbolScheme
    {
        /// <summary>A custodian's account number, on an account.</summary>
        public const string Custodian = "Custodian";

        /// <summary>An exchange ticker, on an asset; the market feed prices assets by it.</summary>
        public const string Ticker = "Ticker";

        /// <summary>A CUSIP, the nine-character identifier of a US or Canadian security.</summary>
        public const string Cusip = "Cusip";

        /// <summary>An ISO 4217 currency code, on a cash asset.</summary>
        public const string Currency = "Currency";
    }

    /// <summary>
    /// ISO 4217 currency codes.
    /// </summary>
    public static class CurrencyCode
    {
        /// <summary>The US dollar.</summary>
        public const string UnitedStatesDollar = "USD";

        /// <summary>The currency every tenant values its holdings in, for now.</summary>
        public const string Valuation = UnitedStatesDollar;
    }

    /// <summary>
    /// The usual factors of market value (quantity × QuantityFactor × price × PriceFactor).
    /// </summary>
    public static class ValueFactor
    {
        /// <summary>One: a share, a fund unit, a unit of currency, a coin.</summary>
        public const decimal One = 1m;

        /// <summary>The shares one US equity option contract stands for.</summary>
        public const decimal EquityOptionContract = 100m;

        /// <summary>A price quoted per 100 of face value (bonds), or in cents.</summary>
        public const decimal PerHundred = 0.01m;
    }
}
