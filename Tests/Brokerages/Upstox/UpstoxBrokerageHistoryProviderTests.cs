﻿using System;
using NUnit.Framework;
using QuantConnect.Brokerages.Upstox;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Logging;
using QuantConnect.Securities;

namespace QuantConnect.Tests.Brokerages.Upstox
{
    [TestFixture, Ignore("This test requires a configured and active Upstox account")]
    public class UpstoxBrokerageHistoryProviderTests
    {
            private static TestCaseData[] TestParameters
            {
                get
                {
                    return new[]
                    {
                    // valid parameters
                    new TestCaseData(Symbols.SBIN, Resolution.Tick, Time.OneMinute, false),
                    new TestCaseData(Symbols.SBIN, Resolution.Second, Time.OneMinute, false),
                    new TestCaseData(Symbols.SBIN, Resolution.Minute, Time.OneHour, false),
                    new TestCaseData(Symbols.SBIN, Resolution.Hour, Time.OneDay, false),
                    new TestCaseData(Symbols.SBIN, Resolution.Daily, TimeSpan.FromDays(15), false),

                    // invalid period, throws "System.ArgumentException : Invalid date range specified"
                    new TestCaseData(Symbols.SBIN, Resolution.Daily, TimeSpan.FromDays(-15), true),

                    // invalid security type, throws "System.ArgumentException : Invalid security type: Forex"
                    new TestCaseData(Symbols.EURUSD, Resolution.Daily, TimeSpan.FromDays(15), true)
                };
                }
            }

            [Test, TestCaseSource(nameof(TestParameters))]
            public void GetsHistory(Symbol symbol, Resolution resolution, TimeSpan period, bool throwsException)
            {
                TestDelegate test = () =>
                {
                    var accessToken = Config.Get("upstox-access-token");
                    var apiKey = Config.Get("upstox-api-key");
                    var brokerage = new UpstoxBrokerage(apiKey, accessToken,null,null);

                    var now = DateTime.UtcNow;

                    var request = new HistoryRequest(now.Add(-period),
                        now,
                        typeof(QuoteBar),
                        symbol,
                        resolution,
                        SecurityExchangeHours.AlwaysOpen(TimeZones.Kolkata),
                        TimeZones.Kolkata,
                        Resolution.Minute,
                        false,
                        false,
                        DataNormalizationMode.Adjusted,
                        TickType.Quote)
                    { };


                    var history = brokerage.GetHistory(request);

                    foreach (var slice in history)
                    {
                        Log.Trace("{0}: {1} - {2} / {3}", slice.Time, slice.Symbol, slice.Price, slice.IsFillForward);
                    }

                    Log.Trace("Base currency: " + brokerage.AccountBaseCurrency);
                };

                if (throwsException)
                {
                    Assert.Throws<ArgumentException>(test);
                }
                else
                {
                    Assert.DoesNotThrow(test);
                }
            }
            }
}
