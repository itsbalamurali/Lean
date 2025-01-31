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
using NodaTime;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Packets;
using QuantConnect.Interfaces;
using QuantConnect.Data.Market;
using System.Collections.Generic;
using QuantConnect.Lean.Engine.DataFeeds.Enumerators;

namespace QuantConnect.Tests.Engine.DataFeeds.Enumerators
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class LiveSubscriptionEnumeratorTests
    {
        [Test]
        public void HandlesSymbolMapping()
        {
            var canonical = Symbols.Fut_SPY_Feb19_2016.Canonical;
            var dataQueue = new TestDataQueueHandler
            {
                DataPerSymbol = new Dictionary<Symbol, IEnumerator<BaseData>>
                {
                    { Symbols.Fut_SPY_Feb19_2016,
                        new List<BaseData>{ new Tick(Time.BeginningOfTime, Symbols.Fut_SPY_Feb19_2016, 1, 1)}.GetEnumerator() },
                    { Symbols.Fut_SPY_Mar19_2016,
                        new List<BaseData>{ new Tick(Time.BeginningOfTime, Symbols.Fut_SPY_Mar19_2016, 2, 2)}.GetEnumerator() },
                }
            };
            var config = new SubscriptionDataConfig(typeof(Tick), canonical, Resolution.Tick,
                DateTimeZone.Utc, DateTimeZone.Utc, false, false, false)
            {
                MappedSymbol = Symbols.Fut_SPY_Feb19_2016.ID.ToString()
            };

            var data = new LiveSubscriptionEnumerator(config, dataQueue, (_, _) => {});

            Assert.IsTrue(data.MoveNext());
            Assert.AreEqual(1, (data.Current as Tick).AskPrice);
            Assert.AreEqual(canonical, (data.Current as Tick).Symbol);

            Assert.IsFalse(data.MoveNext());
            Assert.IsNull(data.Current);

            Assert.AreEqual(2, dataQueue.DataPerSymbol.Count);
            config.MappedSymbol = Symbols.Fut_SPY_Mar19_2016.ID.ToString();
            Assert.AreEqual(1, dataQueue.DataPerSymbol.Count);

            Assert.IsTrue(data.MoveNext());
            Assert.AreEqual(2, (data.Current as Tick).AskPrice);
            Assert.AreEqual(canonical, (data.Current as Tick).Symbol);

            Assert.IsFalse(data.MoveNext());
            Assert.IsNull(data.Current);

            Assert.AreEqual(1, dataQueue.DataPerSymbol.Count);

            data.Dispose();
            dataQueue.Dispose();
        }

        private class TestDataQueueHandler : IDataQueueHandler
        {
            public bool IsConnected => true;

            public Dictionary<Symbol, IEnumerator<BaseData>> DataPerSymbol;

            public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
            {
                if (DataPerSymbol.TryGetValue(dataConfig.Symbol, out var enumerator))
                {
                    return enumerator;
                }
                throw new Exception($"Failed to find a data enumerator for symbol {dataConfig.Symbol}!");
            }
            public void Unsubscribe(SubscriptionDataConfig dataConfig)
            {
                DataPerSymbol.Remove(dataConfig.Symbol);
            }
            public void SetJob(LiveNodePacket job)
            {
            }
            public void Dispose()
            {
            }
        }
    }
}
