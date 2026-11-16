// <copyright file="Valuation.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace AtlasLedger.Model
{
    /// <summary>
    /// What a holding is worth: one formula for the server's rules and every client that shows a value (the website
    /// multiplies the quantities and prices it is sent, so it is not sent values that go stale with every price).
    /// </summary>
    public static class Valuation
    {
        /// <summary>
        /// The market value of a quantity at a quoted price, in the account's currency: quantity × QuantityFactor × price ×
        /// PriceFactor × the price of the quote currency in the account's currency.  The same formula for every asset.
        /// </summary>
        /// <param name="quantity">The quantity.</param>
        /// <param name="quantityFactor">The units one quantity stands for.</param>
        /// <param name="price">The quoted price, in the quote currency.</param>
        /// <param name="priceFactor">The units a quoted price stands for.</param>
        /// <param name="currencyRate">The price of one unit of the quote currency in the account's currency.</param>
        /// <returns>The value, in the account's currency.</returns>
        public static decimal MarketValue(decimal quantity, decimal quantityFactor, decimal price, decimal priceFactor, decimal currencyRate) =>
            quantity * quantityFactor * price * priceFactor * currencyRate;
    }
}
