﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;
using QuantConnect.Orders;

namespace QuantConnect.Securities
{
    /// <summary>
    /// Represents a buying power model for cash accounts
    /// </summary>
    public class CashBuyingPowerModel : IBuyingPowerModel
    {
        /// <summary>
        /// Gets the current leverage of the security
        /// </summary>
        /// <param name="security">The security to get leverage for</param>
        /// <returns>The current leverage in the security</returns>
        public decimal GetLeverage(Security security)
        {
            // Always returns 1. Cash accounts have no leverage.
            return 1m;
        }

        /// <summary>
        /// Sets the leverage for the applicable securities, i.e, equities
        /// </summary>
        /// <remarks>
        /// This is added to maintain backwards compatibility with the old margin/leverage system
        /// </remarks>
        /// <param name="security">The security to set leverage for</param>
        /// <param name="leverage">The new leverage</param>
        public void SetLeverage(Security security, decimal leverage)
        {
            // No action performed. This model always uses a leverage = 1
        }

        /// <summary>
        /// Check if there is sufficient buying power to execute this order.
        /// </summary>
        /// <param name="portfolio">The algorithm's portfolio</param>
        /// <param name="security">The security to be traded</param>
        /// <param name="order">The order to be checked</param>
        /// <returns>Returns true if there is sufficient buying power to execute the order, false otherwise</returns>
        public bool HasSufficientBuyingPowerForOrder(SecurityPortfolioManager portfolio, Security security, Order order)
        {
            var baseCurrency = security as IBaseCurrencySymbol;
            if (baseCurrency == null) return false;

            decimal totalQuantity;
            decimal orderQuantity;
            if (order.Direction == OrderDirection.Buy)
            {
                // quantity available for buying in quote currency
                totalQuantity = portfolio.CashBook[security.QuoteCurrency.Symbol].Amount;
                orderQuantity = order.AbsoluteQuantity * GetOrderPrice(security, order);
            }
            else
            {
                // quantity available for selling in base currency
                totalQuantity = portfolio.CashBook[baseCurrency.BaseCurrencySymbol].Amount;
                orderQuantity = order.AbsoluteQuantity;
            }

            // calculate reserved quantity for open orders (in quote or base currency depending on direction)
            var openOrdersReservedQuantity = GetOpenOrdersReservedQuantity(portfolio, security, order.Direction);

            if (order.Type == OrderType.Market)
            {
                // find a target value in account currency for market orders
                var targetValue = order.Direction == OrderDirection.Buy
                    ? portfolio.CashBook.ConvertToAccountCurrency(totalQuantity - openOrdersReservedQuantity, security.QuoteCurrency.Symbol)
                    : portfolio.CashBook.ConvertToAccountCurrency(openOrdersReservedQuantity, baseCurrency.BaseCurrencySymbol);

                var maximumQuantity = GetMaximumOrderQuantityForTargetValue(portfolio, security, targetValue);
                if (order.Direction == OrderDirection.Buy)
                {
                    maximumQuantity *= GetOrderPrice(security, order);
                }
                return orderQuantity <= Math.Abs(maximumQuantity);
            }

            // for non market orders, add fees to the order cost
            var orderFee = security.FeeModel.GetOrderFee(security, order);
            orderFee = portfolio.CashBook.Convert(orderFee, CashBook.AccountCurrency,
                order.Direction == OrderDirection.Buy
                    ? security.QuoteCurrency.Symbol
                    : baseCurrency.BaseCurrencySymbol);

            return orderQuantity <= totalQuantity - openOrdersReservedQuantity - orderFee;
        }

        /// <summary>
        /// Get the maximum market order quantity to obtain a position with a given value in account currency
        /// </summary>
        /// <param name="portfolio">The algorithm's portfolio</param>
        /// <param name="security">The security to be traded</param>
        /// <param name="targetPortfolioValue">The value in account currency that we want our holding to have</param>
        /// <returns>Returns the maximum allowed market order quantity</returns>
        public decimal GetMaximumOrderQuantityForTargetValue(SecurityPortfolioManager portfolio, Security security, decimal targetPortfolioValue)
        {
            // TODO: still needs cleanup + fix account currency conversions

            var baseCurrency = security as IBaseCurrencySymbol;
            if (baseCurrency == null) return 0;

            var baseCurrencyPosition = portfolio.CashBook[baseCurrency.BaseCurrencySymbol].Amount;
            var quoteCurrencyPosition = portfolio.CashBook[security.QuoteCurrency.Symbol].Amount;

            // determine the unit price in terms of the account currency
            var unitPrice = new MarketOrder(security.Symbol, 1, DateTime.UtcNow).GetValue(security) / security.QuoteCurrency.ConversionRate;
            if (unitPrice == 0) return 0;

            var currentHoldingsValue = baseCurrencyPosition * portfolio.CashBook[baseCurrency.BaseCurrencySymbol].ConversionRate;


            // remove directionality, we'll work in the land of absolutes
            var targetOrderValue = Math.Abs(targetPortfolioValue - currentHoldingsValue);
            var direction = targetPortfolioValue > currentHoldingsValue ? OrderDirection.Buy : OrderDirection.Sell;


            // calculate the total margin available
            var marginRemaining = direction == OrderDirection.Buy ? quoteCurrencyPosition : currentHoldingsValue;
            if (marginRemaining <= 0) return 0;

            // continue iterating while we do not have enough margin for the order
            decimal marginRequired;
            decimal orderValue;
            decimal orderFees;
            var feeToPriceRatio = 0m;

            // compute the initial order quantity
            var orderQuantity = targetOrderValue / unitPrice;

            // rounding off Order Quantity to the nearest multiple of Lot Size
            orderQuantity -= orderQuantity % security.SymbolProperties.LotSize;

            do
            {
                // reduce order quantity by feeToPriceRatio, since it is faster than by lot size
                // if it becomes nonpositive, return zero
                orderQuantity -= feeToPriceRatio;
                if (orderQuantity <= 0) return 0;

                // generate the order
                var order = new MarketOrder(security.Symbol, orderQuantity, DateTime.UtcNow);
                orderValue = order.GetValue(security);
                orderFees = security.FeeModel.GetOrderFee(security, order);

                // find an incremental delta value for the next iteration step
                feeToPriceRatio = orderFees / unitPrice;
                feeToPriceRatio -= feeToPriceRatio % security.SymbolProperties.LotSize;
                if (feeToPriceRatio < security.SymbolProperties.LotSize)
                {
                    feeToPriceRatio = security.SymbolProperties.LotSize;
                }

                // calculate the margin required for the order
                marginRequired = orderValue;

            } while (marginRequired > marginRemaining || orderValue + orderFees > targetOrderValue);

            // add directionality back in
            return (direction == OrderDirection.Sell ? -1 : 1) * orderQuantity;
        }

        /// <summary>
        /// Gets the amount of buying power reserved to maintain the specified position
        /// </summary>
        /// <param name="security">The security for the position</param>
        /// <returns>The reserved buying power in account currency</returns>
        public decimal GetReservedBuyingPowerForPosition(Security security)
        {
            // Always returns 0. Since we're purchasing currencies outright, the position doesn't consume buying power
            return 0;
        }

        /// <summary>
        /// Gets the buying power available for a trade
        /// </summary>
        /// <param name="portfolio">The algorithm's portfolio</param>
        /// <param name="security">The security to be traded</param>
        /// <param name="direction">The direction of the trade</param>
        /// <returns>The buying power available for the trade</returns>
        public decimal GetBuyingPower(SecurityPortfolioManager portfolio, Security security, OrderDirection direction)
        {
            var baseCurrency = security as IBaseCurrencySymbol;
            if (baseCurrency == null) return 0;

            var baseCurrencyPosition = portfolio.CashBook[baseCurrency.BaseCurrencySymbol].Amount;
            var quoteCurrencyPosition = portfolio.CashBook[security.QuoteCurrency.Symbol].Amount;

            // determine the unit price in terms of the quote currency
            var unitPrice = new MarketOrder(security.Symbol, 1, DateTime.UtcNow).GetValue(security) / security.QuoteCurrency.ConversionRate;
            if (unitPrice == 0) return 0;

            if (direction == OrderDirection.Buy)
                return quoteCurrencyPosition / unitPrice;

            if (direction == OrderDirection.Sell)
                return baseCurrencyPosition;

            return 0;
        }

        private static decimal GetOrderPrice(Security security, Order order)
        {
            var orderPrice = 0m;
            switch (order.Type)
            {
                case OrderType.Market:
                    orderPrice = security.Price;
                    break;

                case OrderType.Limit:
                    orderPrice = ((LimitOrder)order).LimitPrice;
                    break;

                case OrderType.StopMarket:
                    orderPrice = ((StopMarketOrder)order).StopPrice;
                    break;

                case OrderType.StopLimit:
                    orderPrice = ((StopLimitOrder)order).LimitPrice;
                    break;
            }

            return orderPrice;
        }

        private static decimal GetOpenOrdersReservedQuantity(SecurityPortfolioManager portfolio, Security security, OrderDirection direction)
        {
            var baseCurrency = security as IBaseCurrencySymbol;
            if (baseCurrency == null) return 0;

            // find the target currency for the requested direction and the securities potentially involved
            var targetCurrency = direction == OrderDirection.Buy
                ? security.QuoteCurrency.Symbol
                : baseCurrency.BaseCurrencySymbol;

            var symbolDirectionPairs = new Dictionary<Symbol, OrderDirection>();
            foreach (var portfolioSecurity in portfolio.Securities.Values)
            {
                var basePortfolioSecurity = portfolioSecurity as IBaseCurrencySymbol;
                if (basePortfolioSecurity == null) continue;

                if (basePortfolioSecurity.BaseCurrencySymbol == targetCurrency)
                {
                    symbolDirectionPairs.Add(portfolioSecurity.Symbol, OrderDirection.Sell);
                }
                else if (portfolioSecurity.QuoteCurrency.Symbol == targetCurrency)
                {
                    symbolDirectionPairs.Add(portfolioSecurity.Symbol, OrderDirection.Buy);
                }
            }

            // fetch open orders with matching symbol/side
            var openOrders = portfolio.Transactions.GetOpenOrders(x =>
                {
                    OrderDirection dir;
                    return symbolDirectionPairs.TryGetValue(x.Symbol, out dir) && dir == x.Direction;
                }
            );

            // calculate reserved quantity for selected orders
            var openOrdersReservedQuantity = 0m;
            foreach (var openOrder in openOrders)
            {
                var orderSecurity = portfolio.Securities[openOrder.Symbol];
                var orderBaseCurrency = orderSecurity as IBaseCurrencySymbol;

                if (orderBaseCurrency != null)
                {
                    // convert order value to target currency
                    var quantityInTargetCurrency = openOrder.AbsoluteQuantity;
                    if (orderSecurity.QuoteCurrency.Symbol == targetCurrency)
                    {
                        quantityInTargetCurrency *= GetOrderPrice(security, openOrder);
                    }

                    openOrdersReservedQuantity += quantityInTargetCurrency;
                }
            }

            return openOrdersReservedQuantity;
        }
    }
}
