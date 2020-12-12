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
using System.Threading;
using NUnit.Framework;
using QuantConnect.Brokerages.Upstox;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Tests.Brokerages.Upstox
{
    [TestFixture, Ignore("This test requires a configured and active Upstox account")]
    public class UpstoxBrokerageTests : BrokerageTests
    {
            /// <summary>
            /// Provides the data required to test each order type in various cases
            /// </summary>
            private static TestCaseData[] OrderParameters()
            {
                return new[]
                {
                new TestCaseData(new MarketOrderTestParameters(Symbols.IDEA)).SetName("MarketOrder"),
                new TestCaseData(new LimitOrderTestParameters(Symbols.IDEA, 10m, 9.20m)).SetName("LimitOrder"),
                new TestCaseData(new StopMarketOrderTestParameters(Symbols.IDEA, 10m, 9.20m)).SetName("StopMarketOrder"),
                new TestCaseData(new StopLimitOrderTestParameters(Symbols.IDEA, 10m, 9.20m)).SetName("StopLimitOrder")
            };
            }

            /// <summary>
            /// Creates the brokerage under test
            /// </summary>
            /// <returns>A connected brokerage instance</returns>
            protected override IBrokerage CreateBrokerage(IOrderProvider orderProvider, ISecurityProvider securityProvider)
            {

            var accessToken = Config.Get("upstox-access-token");
            var apiKey = Config.Get("upstox-api-key");
            var upstox = new UpstoxBrokerage(apiKey,accessToken, null, new AggregationManager());

                return upstox;
            }

            /// <summary>
            /// Gets the symbol to be traded, must be shortable
            /// </summary>
            protected override Symbol Symbol => Symbols.IDEA;

            /// <summary>
            /// Gets the security type associated with the <see cref="BrokerageTests.Symbol"/>
            /// </summary>
            protected override SecurityType SecurityType => SecurityType.Equity;

            /// <summary>
            /// Returns wether or not the brokers order methods implementation are async
            /// </summary>
            protected override bool IsAsync()
            {
                return true;
            }

            /// <summary>
            /// Gets the current market price of the specified security
            /// </summary>
            protected override decimal GetAskPrice(Symbol symbol)
            {
                var upstox = (UpstoxBrokerage)Brokerage;
                var quotes = upstox.GetQuote(symbol);
                return quotes.LastPrice;
            }

            [Test, Ignore("This test exists to manually verify how rejected orders are handled when we don't receive an order ID back from Upstox.")]
            public void ShortIdea()
            {
                PlaceOrderWaitForStatus(new MarketOrder(Symbols.IDEA, -1, DateTime.Now), OrderStatus.Invalid, allowFailedSubmission: true);

                // wait for output to be generated
                Thread.Sleep(20 * 1000);
            }

            [Test, TestCaseSource(nameof(OrderParameters))]
            public override void CancelOrders(OrderTestParameters parameters)
            {
                base.CancelOrders(parameters);
            }

            [Test, TestCaseSource(nameof(OrderParameters))]
            public override void LongFromZero(OrderTestParameters parameters)
            {
                base.LongFromZero(parameters);
            }

            [Test, TestCaseSource(nameof(OrderParameters))]
            public override void CloseFromLong(OrderTestParameters parameters)
            {
                base.CloseFromLong(parameters);
            }

            [Test, TestCaseSource(nameof(OrderParameters))]
            public override void ShortFromZero(OrderTestParameters parameters)
            {
                base.ShortFromZero(parameters);
            }

            [Test, TestCaseSource(nameof(OrderParameters))]
            public override void CloseFromShort(OrderTestParameters parameters)
            {
                base.CloseFromShort(parameters);
            }

            [Test, TestCaseSource(nameof(OrderParameters))]
            public override void ShortFromLong(OrderTestParameters parameters)
            {
                base.ShortFromLong(parameters);
            }

            [Test, TestCaseSource(nameof(OrderParameters))]
            public override void LongFromShort(OrderTestParameters parameters)
            {
                base.LongFromShort(parameters);
            }
        
    }
}
