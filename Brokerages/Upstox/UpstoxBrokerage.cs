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

using Newtonsoft.Json;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Securities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QuantConnect.Orders.Fees;
using System.Threading;
using QuantConnect.Orders;
using QuantConnect.Brokerages.Upstox.Messages;
using Tick = QuantConnect.Data.Market.Tick;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json.Linq;
using NodaTime;
using QuantConnect.Data.Market;

using Order = QuantConnect.Orders.Order;
using OrderType = QuantConnect.Orders.OrderType;

namespace QuantConnect.Brokerages.Upstox
{
    /// <summary>
    /// Upstox Brokerage implementation
    /// </summary>
    [BrokerageFactory(typeof(UpstoxBrokerageFactory))]
    public partial class UpstoxBrokerage : Brokerage, IDataQueueHandler, IDataQueueUniverseProvider
    {
        #region Declarations
        private const int ConnectionTimeout = 30000;
        /// <summary>
        /// The websockets client instance
        /// </summary>
        protected UpstoxWebSocketClientWrapper WebSocket;
        /// <summary>
        /// standard json parsing settings
        /// </summary>
        protected JsonSerializerSettings JsonSettings = new JsonSerializerSettings { FloatParseHandling = FloatParseHandling.Decimal };
        /// <summary>
        /// A list of currently active orders
        /// </summary>
        public ConcurrentDictionary<int, Orders.Order> CachedOrderIDs = new ConcurrentDictionary<int, Orders.Order>();
        /// <summary>
        /// A list of currently subscribed channels
        /// </summary>
        protected Dictionary<string, Channel> ChannelList = new Dictionary<string, Channel>();
        private string _market { get; set; }
        /// <summary>
        /// The api secret
        /// </summary>
        protected string ApiSecret;
        /// <summary>
        /// The api key
        /// </summary>
        protected string ApiKey;
        /// <summary>
        /// Timestamp of most recent heartbeat message
        /// </summary>
        protected DateTime LastHeartbeatUtcTime = DateTime.UtcNow;
        private const int _heartbeatTimeout = 90;
        private Thread _connectionMonitorThread;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly object _lockerConnectionMonitor = new object();
        private volatile bool _connectionLost;
        private const int _connectionTimeout = 30000;
        private readonly IAlgorithm _algorithm;
        private volatile bool _streamLocked;
        private readonly ConcurrentDictionary<int, decimal> _fills = new ConcurrentDictionary<int, decimal>();
        //private UpstoxSubscriptionManager _subscriptionManager;
        private readonly DataQueueHandlerSubscriptionManager SubscriptionManager;

        private ConcurrentDictionary<string, Symbol> _subscriptionsById = new ConcurrentDictionary<string, Symbol>();
        private readonly ConcurrentQueue<MessageData> _messageBuffer = new ConcurrentQueue<MessageData>();

        private readonly IDataAggregator _aggregator;
        private readonly SymbolPropertiesDatabase _symbolPropertiesDatabase;
        /// <summary>
        /// Locking object for the Ticks list in the data queue handler
        /// </summary>
        public readonly object TickLocker = new object();

        private readonly UpstoxSymbolMapper _symbolMapper;


        private readonly List<string> subscribeInstrumentTokens = new List<string>();
        private readonly List<string> unSubscribeInstrumentTokens = new List<string>();

        private UpstoxAPI _kite;
        private readonly IOrderProvider _orderProvider;
        private readonly ISecurityProvider _securityProvider;
        private readonly string _apiKey;
        private readonly string _accessToken;
        private readonly string _wssUrl = "wss://ws.kite.trade/";
        private readonly string RestApiUrl = "https://api.kite.trade";
        #endregion



        /// <summary>
        /// Constructor for brokerage
        /// </summary>
        /// <param name="apiKey">api key</param>
        /// <param name="apiSecret">api secret</param>
        /// <param name="algorithm">the algorithm instance is required to retrieve account type</param>
        public UpstoxBrokerage(string apiKey, string accessToken, IAlgorithm algorithm, IDataAggregator aggregator)
            : base("Upstox")
        {
            _algorithm = algorithm;
            _aggregator = aggregator;
            _kite = new UpstoxAPI(apiKey, accessToken);
            _apiKey = apiKey;
            _accessToken = accessToken;
            WebSocket = new UpstoxWebSocketClientWrapper();
            _wssUrl += string.Format(CultureInfo.InvariantCulture, "?api_key={0}&access_token={1}", _apiKey, _accessToken);
            WebSocket.Initialize(_wssUrl);
            WebSocket.Message += OnMessage;
            WebSocket.Open += (sender, args) =>
            {
                Log.Trace($"UpstoxBrokerage(): WebSocket.Open. Subscribing");
                Subscribe(GetSubscribed());
            };
            WebSocket.Error += OnError;
            _symbolMapper = new UpstoxSymbolMapper(_kite);

            var subscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager();
            subscriptionManager.SubscribeImpl += (s, t) =>
            {
                Subscribe(s);
                return true;
            };
            subscriptionManager.UnsubscribeImpl += (s, t) => Unsubscribe(s);
            SubscriptionManager = subscriptionManager;

            _symbolPropertiesDatabase = SymbolPropertiesDatabase.FromDataFolder();
            Log.Trace("Start Upstox Brokerage");
        }

        /// <summary>
        /// Subscribes to the requested symbols (using an individual streaming channel)
        /// </summary>
        /// <param name="symbols">The list of symbols to subscribe</param>
        public void Subscribe(IEnumerable<Symbol> symbols)
        {
            if (symbols.Count()<=0)
                return;
            foreach (var symbol in symbols)
            {
                var instrumentToken = _symbolMapper.GetUpstoxInstrumentToken(symbol.ID.Symbol, symbol.ID.Market);
                if (instrumentToken == 0)
                {
                    Log.Error("Invalid Upstox Instrument token");
                }
                if (!subscribeInstrumentTokens.Contains(instrumentToken.ToStringInvariant()))
                {
                    subscribeInstrumentTokens.Add(instrumentToken.ToStringInvariant());
                    unSubscribeInstrumentTokens.Remove(instrumentToken.ToStringInvariant());
                    _subscriptionsById[instrumentToken.ToStringInvariant()] = symbol;
                }
            }
            string request = "{\"a\":\"subscribe\",\"v\":[" + String.Join(",", subscribeInstrumentTokens.ToArray()) + "]}";

            string requestFullMode = "{\"a\":\"mode\",\"v\":[\"full\",[" + String.Join(",", subscribeInstrumentTokens.ToArray()) + "]]}";


            Log.Debug("Websocket Subscribe Request: " + request.ToStringInvariant());
            Log.Debug("Websocket Subscribe Full Mode Request: " + requestFullMode.ToStringInvariant());


            WebSocket.Send(request);
            WebSocket.Send(requestFullMode);


        }

        private IEnumerable<Symbol> GetSubscribed()
        {
            return SubscriptionManager.GetSubscribedSymbols() ?? Enumerable.Empty<Symbol>();
        }

        /// <summary>
        /// Ends current subscriptions
        /// </summary>
        private bool Unsubscribe(IEnumerable<Symbol> symbols)
        {
            if (WebSocket.IsOpen)
            {
                foreach (var symbol in symbols)
                {
                    var instrumentToken = _symbolMapper.GetUpstoxInstrumentToken(symbol.ID.Symbol, symbol.ID.Market);
                    if (instrumentToken == 0)
                    {
                        Log.Error("Invalid Upstox Instrument token");
                    }
                    if (!unSubscribeInstrumentTokens.Contains(instrumentToken.ToStringInvariant()))
                    {
                        unSubscribeInstrumentTokens.Add(instrumentToken.ToStringInvariant());
                        subscribeInstrumentTokens.Remove(instrumentToken.ToStringInvariant());
                        Symbol unSubscribeSymbol;
                        _subscriptionsById.TryRemove(instrumentToken.ToStringInvariant(), out unSubscribeSymbol);
                    }
                }
                string request = "{\"a\":\"unsubscribe\",\"v\":[" + String.Join(",", unSubscribeInstrumentTokens.ToArray()) + "]}";

                Log.Trace("Websocket UnSubscribe Request: " + request.ToStringInvariant());
                WebSocket.Send(request);
                return true;
            }
            return false;

        }


        /// <summary>
        /// Gets Quote using Upstox API
        /// </summary>
        /// <returns> Quote</returns>
        public Quote GetQuote(Symbol symbol)
        {
            var instrument = _symbolMapper.GetUpstoxInstrumentToken(symbol.ID.Symbol, symbol.ID.Market);
            var instrumentIds = new string[] { instrument.ToStringInvariant() };
            var quotes = _kite.GetQuote(instrumentIds);
            return quotes[symbol.ID.Symbol];
        }


        private void OnOrderClose(string[] entries)
        {
            var brokerId = entries[0];
            if (entries[5].IndexOf("canceled", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var order = CachedOrderIDs
                    .FirstOrDefault(o => o.Value.BrokerId.Contains(brokerId))
                    .Value;
                if (order == null)
                {
                    order = _algorithm.Transactions.GetOrderByBrokerageId(brokerId);
                    if (order == null)
                    {
                        // not our order, nothing else to do here
                        return;
                    }
                }
                Orders.Order outOrder;
                if (CachedOrderIDs.TryRemove(order.Id, out outOrder))
                {
                    OnOrderEvent(new OrderEvent(order,
                        DateTime.UtcNow,
                        OrderFee.Zero,
                        "Upstox Order Event")
                    { Status = OrderStatus.Canceled });
                }
            }
        }

        private void EmitFillOrder(Messages.Order orderUpdate)
        {
            try
            {
                var brokerId = orderUpdate.OrderId;
                Log.Trace("EmitFillOrder Broker ID:" + brokerId);
                var order = CachedOrderIDs
                    .FirstOrDefault(o => o.Value.BrokerId.Contains(brokerId))
                    .Value;
                if (order == null)
                {
                    order = _algorithm.Transactions.GetOrderByBrokerageId(brokerId);
                    if (order == null)
                    {
                        // not our order, nothing else to do here
                        return;
                    }
                }

                if(orderUpdate.Status=="OPEN"&& orderUpdate.Quantity != orderUpdate.FilledQuantity)
                {
                    return;
                }

                var symbol = _symbolMapper.ConvertUpstoxSymbolToLeanSymbol(orderUpdate.InstrumentToken);
                var fillPrice = orderUpdate.Price;
                var fillQuantity = orderUpdate.FilledQuantity;
                var direction = fillQuantity < 0 ? OrderDirection.Sell : OrderDirection.Buy;
                var updTime = orderUpdate.OrderTimestamp.GetValueOrDefault();
                var orderFee = new OrderFee(new CashAmount(
                        20,
                        Currencies.INR
                    ));
                var status = OrderStatus.Filled;
                if (fillQuantity != order.Quantity)
                {
                    decimal totalFillQuantity;
                    _fills.TryGetValue(order.Id, out totalFillQuantity);
                    totalFillQuantity += fillQuantity;
                    _fills[order.Id] = totalFillQuantity;
                    status = totalFillQuantity == order.Quantity
                        ? OrderStatus.Filled
                        : OrderStatus.PartiallyFilled;
                }

                var orderEvent = new OrderEvent
                (
                    order.Id, symbol, updTime, status,
                    direction, fillPrice, fillQuantity,
                    orderFee, $"Upstox Order Event {direction}"
                );

                // if the order is closed, we no longer need it in the active order list
                if (status == OrderStatus.Filled)
                {
                    Orders.Order outOrder;
                    CachedOrderIDs.TryRemove(order.Id, out outOrder);
                    decimal ignored;
                    _fills.TryRemove(order.Id, out ignored);
                }

                OnOrderEvent(orderEvent);
            }
            catch (Exception e)
            {
                Log.Error(e);
                throw;
            }
        }

        /// <summary>
        /// Lock the streaming processing while we're sending orders as sometimes they fill before the REST call returns.
        /// </summary>
        public void LockStream()
        {
            Log.Trace("UpstoxBrokerage.Messaging.LockStream(): Locking Stream");
            _streamLocked = true;
        }

        /// <summary>
        /// Unlock stream and process all backed up messages.
        /// </summary>
        public void UnlockStream()
        {
            Log.Trace("UpstoxBrokerage.Messaging.UnlockStream(): Processing Backlog...");
            while (_messageBuffer.Any())
            {
                MessageData e;
                _messageBuffer.TryDequeue(out e);
                OnMessageImpl(e);
            }
            Log.Trace("UpstoxBrokerage.Messaging.UnlockStream(): Stream Unlocked.");
            // Once dequeued in order; unlock stream.
            _streamLocked = false;
        }

        #region IBrokerage

        /// <summary>
        /// Returns the brokerage account's base currency
        /// </summary>
        public override string AccountBaseCurrency => Currencies.INR;

        /// <summary>
        /// Checks if the websocket connection is connected or in the process of connecting
        /// </summary>
        public override bool IsConnected => WebSocket.IsOpen;
        /// <summary>
        /// Connects to Upstox wss
        /// </summary>

        public override void Connect()
        {
            if (IsConnected)
                return;

            Log.Trace("UpstoxBrokerage.Connect(): Connecting...");

            var resetEvent = new ManualResetEvent(false);
            EventHandler triggerEvent = (o, args) => resetEvent.Set();
            WebSocket.Open += triggerEvent;
            WebSocket.Connect();
            if (!resetEvent.WaitOne(ConnectionTimeout))
            {
                throw new TimeoutException("Websockets connection timeout.");
            }
            WebSocket.Open -= triggerEvent;
        }

        /// <summary>
        /// Closes the websockets connection
        /// </summary>
        public override void Disconnect()
        {
            //base.Disconnect();
            if (WebSocket.IsOpen)
            {
                WebSocket.Close();
            }
        }


        /// <summary>
        /// Places a new order and assigns a new broker ID to the order
        /// </summary>
        /// <param name="order">The order to be placed</param>
        /// <returns>True if the request for a new order has been placed, false otherwise</returns>
        public override bool PlaceOrder(Order order)
        {
            LockStream();
            
            uint orderQuantity = Convert.ToUInt32(Math.Abs(order.Quantity));
            JObject orderResponse;
            decimal? triggerPrice = null;

            if (order.Type == OrderType.StopLimit || order.Type == OrderType.StopMarket || order.Type == OrderType.Limit)
            {
                triggerPrice = GetOrderTriggerPrice(order);
            }
            decimal? orderPrice = GetOrderPrice(order);
            var kiteOrderType = ConvertOrderType(order.Type);
            //if (order.Type == OrderType.Bracket)
            //{
            //    orderResponse = _kite.PlaceOrder(order.Symbol.ID.Market, order.Symbol.ID.Symbol,"",order.Quantity.ConvertInvariant<int>());
            //}
            //else
            //{

            ////TODO: Get the product type from Algo (MIS,NRML,CNC)
            orderResponse = _kite.PlaceOrder(order.Symbol.ID.Market.ToUpperInvariant(), order.Symbol.ID.Symbol, order.Direction.ToString().ToUpperInvariant(), orderQuantity, orderPrice,UpstoxAPIProductType.MIS.ToString(), kiteOrderType,null,null,triggerPrice);
            //}
            Log.Debug("UpstoxOrderResponse:");
            Log.Debug(orderResponse.ToString());

            ////TODO: Calculate order fees later
            var orderFee = new OrderFee(new CashAmount(20, Currencies.INR));
            if ((string)orderResponse["status"] == "error")
            {
                var errorMessage = $"Order failed, Order Id: {order.Id} timestamp: {order.Time} quantity: {order.Quantity} content: {orderResponse["message"]}";
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow.ConvertFromUtc(TimeZones.Kolkata), orderFee, "Upstox Order Event") { Status = OrderStatus.Invalid });
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, errorMessage));

                UnlockStream();
                return true;
            }

            if ((string)orderResponse["status"] == "success")
            {

                if (string.IsNullOrEmpty((string)orderResponse["data"]["order_id"]))
                {
                    var errorMessage = $"Error parsing response from place order: {(string)orderResponse["status_message"]}";
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow.ConvertFromUtc(TimeZones.Kolkata), orderFee, "Upstox Order Event") { Status = OrderStatus.Invalid, Message = errorMessage });
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, (string)orderResponse["status_message"], errorMessage));

                    UnlockStream();
                    return true;
                }

                var brokerId = (string)orderResponse["data"]["order_id"];
                if (CachedOrderIDs.ContainsKey(order.Id))
                {
                    CachedOrderIDs[order.Id].BrokerId.Clear();
                    CachedOrderIDs[order.Id].BrokerId.Add(brokerId);
                }
                else
                {
                    order.BrokerId.Add(brokerId);
                    CachedOrderIDs.TryAdd(order.Id, order);
                }

                // Generate submitted event
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow.ConvertFromUtc(TimeZones.Kolkata), orderFee, "Upstox Order Event") { Status = OrderStatus.Submitted });
                Log.Trace($"Order submitted successfully - OrderId: {order.Id}");

                UnlockStream();
                return true;
            }

            var message = $"Order failed, Order Id: {order.Id} timestamp: {order.Time} quantity: {order.Quantity} content: {orderResponse["status_message"]}";
            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow.ConvertFromUtc(TimeZones.Kolkata), orderFee, "Upstox Order Event") { Status = OrderStatus.Invalid });
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, message));

            UnlockStream();
            return true;

        }

        /// <summary>
        /// Return a relevant price for order depending on order type
        /// Price must be positive
        /// </summary>
        /// <param name="order"></param>
        /// <returns></returns>
        private static decimal? GetOrderPrice(Order order)
        {
            switch (order.Type)
            {
                case OrderType.Limit:
                    return ((LimitOrder)order).LimitPrice;
                case OrderType.Market:
                    // Order price must be positive for market order too;
                    // refuses for price = 0
                    return null;
                case OrderType.StopLimit:
                    return ((StopLimitOrder)order).LimitPrice;
                case OrderType.StopMarket:
                    return ((StopMarketOrder)order).StopPrice;
            }

            throw new NotSupportedException($"UpstoxBrokerage.ConvertOrderType: Unsupported order type: {order.Type}");
        }

        /// <summary>
        /// Return a relevant price for order depending on order type
        /// Price must be positive
        /// </summary>
        /// <param name="order"></param>
        /// <returns></returns>
        private static decimal? GetOrderTriggerPrice(Order order)
        {
            switch (order.Type)
            {
                case OrderType.Limit:
                    return ((LimitOrder)order).LimitPrice;
                case OrderType.Market:
                    // Order price must be positive for market order too;
                    // refuses for price = 0
                    return null;
                case OrderType.StopLimit:
                    return ((StopLimitOrder)order).StopPrice;
                case OrderType.StopMarket:
                    return ((StopMarketOrder)order).StopPrice;
            }

            throw new NotSupportedException($"UpstoxBrokerage.ConvertOrderType: Unsupported order type: {order.Type}");
        }

        private static string ConvertOrderType(OrderType orderType)
        {

        //            public const string ORDER_TYPE_MARKET = "MARKET";
        //public const string ORDER_TYPE_LIMIT = "LIMIT";
        //public const string ORDER_TYPE_SLM = "SL-M";
        //public const string ORDER_TYPE_SL = "SL";

            switch (orderType)
            {
                case OrderType.Limit:
                    return "LIMIT";
                case OrderType.Market:
                    return "MARKET";
                case OrderType.StopMarket:
                    return "SL-M";
                case OrderType.StopLimit:
                    return "SL";
                default:
                    throw new NotSupportedException($"UpstoxBrokerage.ConvertOrderType: Unsupported order type: {orderType}");
            }
        }


        /// <summary>
        /// Updates the order with the same id
        /// </summary>
        /// <param name="order">The new order information</param>
        /// <returns>True if the request was made for the order to be updated, false otherwise</returns>
        public override bool UpdateOrder(Order order)
        {
            LockStream();

            uint orderQuantity = Convert.ToUInt32(Math.Abs(order.Quantity));
            JObject orderResponse;
            decimal? triggerPrice = null;

            if (order.Type == OrderType.StopLimit || order.Type == OrderType.StopMarket || order.Type == OrderType.Limit)
            {
                triggerPrice = GetOrderTriggerPrice(order);
            }
            decimal? orderPrice = GetOrderPrice(order);
            var kiteOrderType = ConvertOrderType(order.Type);

            orderResponse = _kite.ModifyOrder(order.BrokerId[0].ToStringInvariant(),
                null,
                order.Symbol.ID.Market.ToUpperInvariant(), 
                order.Symbol.ID.Symbol, 
                order.Direction.ToString().ToUpperInvariant(), 
                orderQuantity,
                orderPrice,
                UpstoxAPIProductType.MIS.ToString(),
                kiteOrderType,
                null,
                null, 
                triggerPrice
                );
            var orderFee = OrderFee.Zero;
            if ((string)orderResponse["status"] == "success")
            {
                if (string.IsNullOrEmpty((string)orderResponse["data"]["order_id"]))
                {
                    var errorMessage = $"Error parsing response from modify order: {orderResponse["status_message"]}";
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, orderFee, "Upstox Update Order Event") { Status = OrderStatus.Invalid, Message = errorMessage });
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning,(string) orderResponse["status"], errorMessage));

                    UnlockStream();
                    return true;
                }

                var brokerId = (string)orderResponse["data"]["order_id"];
                if (CachedOrderIDs.ContainsKey(order.Id))
                {
                    CachedOrderIDs[order.Id].BrokerId.Clear();
                    CachedOrderIDs[order.Id].BrokerId.Add(brokerId);
                }
                else
                {
                    order.BrokerId.Add(brokerId);
                    CachedOrderIDs.TryAdd(order.Id, order);
                }

                // Generate submitted event
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, orderFee, "Upstox Update Order Event") { Status = OrderStatus.UpdateSubmitted });
                Log.Trace($"Order modified successfully - OrderId: {order.Id}");

                UnlockStream();
                return true;
            }

            var message = $"Order failed, Order Id: {order.Id} timestamp: {order.Time} quantity: {order.Quantity} content: {orderResponse["status_message"]}";
            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, orderFee, "Upstox Update Order Event") { Status = OrderStatus.Invalid });
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, message));

            UnlockStream();
            return true;
        }

        /// <summary>
        /// Cancels the order with the specified ID
        /// </summary>
        /// <param name="order">The order to cancel</param>
        /// <returns>True if the request was submitted for cancellation, false otherwise</returns>
        public override bool CancelOrder(Order order)
        {
            LockStream();
            //if (order.Type == OrderType.Bracket)
            //{
            //    orderResponse = _kite.CancelOrder(order.Id.ToStringInvariant());
            //}
            //else
            //{
            var orderResponse = _kite.CancelOrder(order.BrokerId[0].ToStringInvariant());
            //}
            if ((string)orderResponse["status"] == "success")
            {
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero,"Upstox Order Cancelled Event") { Status = OrderStatus.Canceled });
                UnlockStream();
                return true;
            }
            var errorMessage = $"Error cancelling order: {orderResponse["status_message"]}";
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, (string)orderResponse["status"], errorMessage));

            UnlockStream();
            return false;


        }


        /// <summary>
        /// Gets all orders not yet closed
        /// </summary>
        /// <returns></returns>
        public override List<Orders.Order> GetOpenOrders()
        {
            var allOrders = _kite.GetOrders();
            List<Orders.Order> list = new List<Orders.Order>();
            //Only loop if there are any actual orders inside response
            if (allOrders.Count > 0)
            {

                foreach (var item in allOrders.Where(z => z.Status == "filled"))
                {

                    Orders.Order order;
                    if (item.OrderType.ToLowerInvariant() == "MKT")
                    {
                        order = new MarketOrder { Price = Convert.ToDecimal(item.Price, CultureInfo.InvariantCulture) };
                    }
                    else if (item.OrderType.ToLowerInvariant() == "L")
                    {
                        order = new LimitOrder { LimitPrice = Convert.ToDecimal(item.Price, CultureInfo.InvariantCulture) };
                    }
                    else if (item.OrderType.ToLowerInvariant() == "SL-M")
                    {
                        order = new StopMarketOrder { StopPrice = Convert.ToDecimal(item.Price, CultureInfo.InvariantCulture) };
                    }
                    else
                    {
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "UnKnownOrderType",
                            "UpstoxBrorage.GetOpenOrders: Unsupported order type returned from brokerage: " + item.OrderType));
                        continue;
                    }

                    var itemTotalQty = item.Quantity;
                    var originalQty = item.Quantity;
                    order.Quantity = item.TransactionType.ToLowerInvariant() == "sell" ? -itemTotalQty : originalQty;
                    order.BrokerId = new List<string> { item.OrderId };
                    order.Symbol = _symbolMapper.GetLeanSymbol(item.InstrumentToken.ToStringInvariant());
                    order.Time = (DateTime)item.OrderTimestamp;
                    order.Status = ConvertOrderStatus(item);
                    order.Price = item.Price;
                    list.Add(order);

                }
                foreach (var item in list)
                {
                    if (item.Status.IsOpen())
                    {
                        var cached = CachedOrderIDs.Where(c => c.Value.BrokerId.Contains(item.BrokerId.First()));
                        if (cached.Any())
                        {
                            CachedOrderIDs[cached.First().Key] = item;
                        }
                    }
                }
            }

            return list;
        }

        private OrderStatus ConvertOrderStatus(Messages.Order orderDetails)
        {
            var filledQty = Convert.ToInt32(orderDetails.FilledQuantity, CultureInfo.InvariantCulture);
            var pendingQty = Convert.ToInt32(orderDetails.PendingQuantity, CultureInfo.InvariantCulture);
            var orderDetail = _kite.GetOrderHistory(orderDetails.OrderId);
            if (orderDetails.Status != "complete" && filledQty == 0)
            {
                return OrderStatus.Submitted;
            }
            else if (filledQty > 0 && pendingQty > 0)
            {
                return OrderStatus.PartiallyFilled;
            }
            else if (pendingQty == 0)
            {
                return OrderStatus.Filled;
            }
            else if (orderDetails.Status.ToUpperInvariant() == "CANCELLED")
            {
                return OrderStatus.Canceled;
            }

            return OrderStatus.None;
        }

        /// <summary>
        /// Gets all open positions
        /// </summary>
        /// <returns></returns>
        public override List<Holding> GetAccountHoldings()
        {
            var holdingsList = new List<Holding>();
            var HoldingResponse = _kite.GetHoldings();
            if (HoldingResponse == null)
            {
                return holdingsList;
            }
            foreach (var item in HoldingResponse)
            {
                ////TODO: Handle this later for now ignore CNC holdings since we currently only support MIS
                if (item.Product == UpstoxAPIProductType.MIS.ToString().ToUpperInvariant())
                {
                    //(avgprice - lasttradedprice) * holdingsqty
                    Holding holding = new Holding
                    {
                        AveragePrice = item.AveragePrice,
                        Symbol = _symbolMapper.GetLeanSymbol(item.TradingSymbol),
                        MarketPrice = item.LastPrice,
                        Quantity = item.Quantity,
                        Type = SecurityType.Equity,
                        UnrealizedPnL = item.PNL, //(item.averagePrice - item.lastTradedPrice) * item.holdingsQuantity,
                        CurrencySymbol = Currencies.GetCurrencySymbol(AccountBaseCurrency),
                        //TODO:Can be item.holdings value too, need to debug
                        MarketValue = item.ClosePrice * item.Quantity

                    };
                    holdingsList.Add(holding);
                }
            }
            return holdingsList;
        }

        /// <summary>
        /// Gets the total account cash balance for specified account type
        /// </summary>
        /// <returns></returns>
        public override List<CashAmount> GetCashBalance()
        {
            var list = new List<CashAmount>();
            var response = _kite.GetMargins();
            decimal amt = Convert.ToDecimal(response.Equity.Net, CultureInfo.InvariantCulture);
            list.Add(new CashAmount(amt, AccountBaseCurrency));
            return list;
        }

        /// <summary>
        /// Gets the history for the requested security
        /// </summary>
        /// <param name="request">The historical data request</param>
        /// <returns>An enumerable of bars covering the span specified in the request</returns>
        public override IEnumerable<BaseData> GetHistory(HistoryRequest request)
        {
            if (request.Symbol.SecurityType != SecurityType.Equity && request.Symbol.SecurityType != SecurityType.Future && request.Symbol.SecurityType != SecurityType.Option)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidSecurityType",
                    $"{request.Symbol.SecurityType} security type not supported, no history returned"));
                yield break;
            }

            if (request.Resolution == Resolution.Tick || request.Resolution == Resolution.Second)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidResolution",
                    $"{request.Resolution} resolution not supported, no history returned"));
                yield break;
            }

            if (request.StartTimeUtc >= request.EndTimeUtc)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidDateRange",
                    "The history request start date must precede the end date, no history returned"));
                yield break;
            }

            // if the end time cannot be rounded to resolution without a remainder
            //TODO Fix this 
            //if (request.EndTimeUtc.Ticks % request.Resolution.ToTimeSpan().Ticks > 0)
            //{
            //    // give a warning and return
            //    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidEndTime",
            //        "The history request's end date is not a full multiple of a resolution. " +
            //        "Upstox API only allows to support trade bar history requests. The start and end dates " +
            //        "of a such request are expected to match exactly with the beginning of the first bar and ending of the last"));
            //    yield break;
            //}

            DateTime latestTime = request.StartTimeUtc;
            var requests = new List<HistoryRequest>();
            requests.Add(request);

            foreach (var historyRequest in requests)
            {
                if (historyRequest.Symbol.ID.SecurityType != SecurityType.Equity && historyRequest.Symbol.ID.SecurityType != SecurityType.Future && historyRequest.Symbol.ID.SecurityType != SecurityType.Option)
                {
                    throw new ArgumentException("Upstox does not support this security type: " + historyRequest.Symbol.ID.SecurityType);
                }

                if (historyRequest.StartTimeUtc >= historyRequest.EndTimeUtc)
                {
                    throw new ArgumentException("Invalid date range specified");
                }

                var start = historyRequest.StartTimeUtc.ConvertTo(DateTimeZone.Utc, TimeZones.Kolkata);
                var end = historyRequest.EndTimeUtc.ConvertTo(DateTimeZone.Utc, TimeZones.Kolkata);

                var history = Enumerable.Empty<BaseData>();


                switch (historyRequest.Resolution)
                {
                    case Resolution.Minute:
                        history = GetHistoryForPeriod(historyRequest.Symbol, start, end,historyRequest.Resolution,"minute");
                        break;

                    case Resolution.Hour:
                        history = GetHistoryForPeriod(historyRequest.Symbol, start, end,historyRequest.Resolution,"60minute");
                        break;

                    case Resolution.Daily:
                        history = GetHistoryForPeriod(historyRequest.Symbol, start, end, historyRequest.Resolution,"day");
                        break;
                }

                foreach (var baseData in history)
                {
                    yield return baseData;
                }
            }            
        }

        private IEnumerable<BaseData> GetHistoryForPeriod(Symbol symbol, DateTime start, DateTime end, Resolution resolution,string upstoxResolution)
        {
            Log.Debug("UpstoxBrokerage.GetHistoryForPeriod();");
            var scripSymbol = _symbolMapper.GetUpstoxInstrumentToken(symbol,Market.NSE);
            var candles = _kite.GetHistoricalData(scripSymbol.ToStringInvariant(), start, end, upstoxResolution);

            if (!candles.Any())
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "NoHistoricalData",
                    $"Exchange returned no data for {symbol} on history request " +
                    $"from {start:s} to {end:s}"));
            }

            var period = resolution.ToTimeSpan();

            foreach (var candle in candles)
            {
                yield return new TradeBar()
                {
                    Time = candle.TimeStamp,
                    Symbol = symbol,
                    Low = candle.Low,
                    High = candle.High,
                    Open = candle.Open,
                    Close = candle.Close,
                    Volume = candle.Volume,
                    Value = candle.Close,
                    DataType = MarketDataType.TradeBar,
                    Period = period,
                    EndTime = candle.TimeStamp.Add(period)
                };
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public override void Dispose()
        {
        }

        private void OnError(object sender, WebSocketError e)
        {
            Log.Error($"UpstoxSubscriptionManager.OnError(): Message: {e.Message} Exception: {e.Exception}");
        }

        private void OnMessage(object sender, MessageData e)
        {
            LastHeartbeatUtcTime = DateTime.UtcNow;

            // Verify if we're allowed to handle the streaming packet yet; while we're placing an order we delay the
            // stream processing a touch.
            try
            {
                if (_streamLocked)
                {
                    _messageBuffer.Enqueue(e);
                    return;
                }
            }
            catch (Exception err)
            {
                Log.Error(err);
            }

            OnMessageImpl(e);
        }

        /// <summary>
        /// Implementation of the OnMessage event
        /// </summary>
        /// <param name="e"></param>
        private void OnMessageImpl(MessageData e)
        {
            try
            {
                if (e.MessageType == WebSocketMessageType.Binary)
                {
                    if (e.Count > 1)
                    {
                        int offset = 0;
                        ushort count = ReadShort(e.Data, ref offset); //number of packets

                        for (ushort i = 0; i < count; i++)
                        {
                            ushort length = ReadShort(e.Data, ref offset); // length of the packet
                            Messages.Tick tick = new Messages.Tick();
                            if (length == 8) // ltp
                                tick = ReadLTP(e.Data, ref offset);
                            else if (length == 28) // index quote
                                tick = ReadIndexQuote(e.Data, ref offset);
                            else if (length == 32) // index quote
                                tick = ReadIndexFull(e.Data, ref offset);
                            else if (length == 44) // quote
                                tick = ReadQuote(e.Data, ref offset);
                            else if (length == 184) // full with marketdepth and timestamp
                                tick = ReadFull(e.Data, ref offset);
                            // If the number of bytes got from stream is less that that is required
                            // data is invalid. This will skip that wrong tick
                            if (tick.InstrumentToken != 0 && offset <= e.Count && tick.Mode == Constants.MODE_FULL)
                            {
                                var symbol = _symbolMapper.ConvertUpstoxSymbolToLeanSymbol(tick.InstrumentToken);

                                EmitQuoteTick(symbol, tick.Bids, tick.BuyQuantity, tick.Offers, tick.SellQuantity,tick.Timestamp.GetValueOrDefault());
                                EmitTradeTick(symbol, tick.LastTradeTime.GetValueOrDefault(), tick.LastPrice, tick.LastQuantity);
                            }
                        }
                    }
                }
                else if (e.MessageType == WebSocketMessageType.Text)
                {
                    string message = Encoding.UTF8.GetString(e.Data.Take(e.Count).ToArray());

                    JObject messageDict = Utils.JsonDeserialize(message);
                    if ((string)messageDict["type"] == "order")
                    {
                        
                        EmitFillOrder(new Messages.Order(messageDict["data"]));
                    }
                    else if ((string)messageDict["type"] == "error")
                    {
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1, $"Upstox WSS Error. Data: {e.Data} Exception: {messageDict["data"]}"));

                    }
                }
                else if (e.MessageType == WebSocketMessageType.Close)
                {
                    WebSocket.Close();
                }
            }
            catch (Exception exception)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1, $"Parsing wss message failed. Data: {e.Data} Exception: {exception}"));
                throw;
            }
        }

        private void EmitTradeTick(Symbol symbol, DateTime time, decimal price, decimal amount)
        {
            try
            {
                lock (TickLocker)
                {
                    _aggregator.Update(new Tick
                    {
                        Value = price,
                        Time = time,
                        Symbol = symbol,
                        TickType = TickType.Trade,
                        Quantity = Math.Abs(amount),
                        Exchange= symbol.ID.Market
                    });
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
                throw;
            }
        }



        private void EmitQuoteTick(Symbol symbol, DepthItem[] bids, decimal bidSize, DepthItem[] asks, decimal askSize, DateTime time)
        {
            if (bids != null && bids.Max(x => x.Price) > 0 && asks != null && asks.Max(x => x.Price) > 0)
            {
                var tick = new Tick
                {
                    AskPrice = asks.Max(x => x.Price),
                    BidPrice = bids.Max(x => x.Price),
                    Time = time,
                    Symbol = symbol,
                    TickType = TickType.Quote,
                    AskSize = askSize,
                    BidSize = bidSize,
                    
                    
                };

                lock (TickLocker)
                {
                    _aggregator.Update(tick);
                }
            }
        }

        /// <summary>
        /// Reads 2 byte short int from byte stream
        /// </summary>
        private ushort ReadShort(byte[] b, ref int offset)
        {
            ushort data = (ushort)(b[offset + 1] + (b[offset] << 8));
            offset += 2;
            return data;
        }

        /// <summary>
        /// Reads 4 byte int32 from byte stream
        /// </summary>
        private uint ReadInt(byte[] b, ref int offset)
        {
            uint data = BitConverter.ToUInt32(new byte[] { b[offset + 3], b[offset + 2], b[offset + 1], b[offset + 0] }, 0);
            offset += 4;
            return data;
        }

        /// <summary>
        /// Reads an ltp mode tick from raw binary data
        /// </summary>
        private Messages.Tick ReadLTP(byte[] b, ref int offset)
        {
            Messages.Tick tick = new Messages.Tick();
            tick.Mode = Constants.MODE_LTP;
            tick.InstrumentToken = ReadInt(b, ref offset);

            decimal divisor = (tick.InstrumentToken & 0xff) == 3 ? 10000000.0m : 100.0m;

            tick.Tradable = (tick.InstrumentToken & 0xff) != 9;
            tick.LastPrice = ReadInt(b, ref offset) / divisor;
            return tick;
        }

        /// <summary>
        /// Reads a index's quote mode tick from raw binary data
        /// </summary>
        private Messages.Tick ReadIndexQuote(byte[] b, ref int offset)
        {
            Messages.Tick tick = new Messages.Tick();
            tick.Mode = Constants.MODE_QUOTE;
            tick.InstrumentToken = ReadInt(b, ref offset);

            decimal divisor = (tick.InstrumentToken & 0xff) == 3 ? 10000000.0m : 100.0m;

            tick.Tradable = (tick.InstrumentToken & 0xff) != 9;
            tick.LastPrice = ReadInt(b, ref offset) / divisor;
            tick.High = ReadInt(b, ref offset) / divisor;
            tick.Low = ReadInt(b, ref offset) / divisor;
            tick.Open = ReadInt(b, ref offset) / divisor;
            tick.Close = ReadInt(b, ref offset) / divisor;
            tick.Change = ReadInt(b, ref offset) / divisor;
            return tick;
        }

        private Messages.Tick ReadIndexFull(byte[] b, ref int offset)
        {
            Messages.Tick tick = new Messages.Tick();
            tick.Mode = Constants.MODE_FULL;
            tick.InstrumentToken = ReadInt(b, ref offset);

            decimal divisor = (tick.InstrumentToken & 0xff) == 3 ? 10000000.0m : 100.0m;

            tick.Tradable = (tick.InstrumentToken & 0xff) != 9;
            tick.LastPrice = ReadInt(b, ref offset) / divisor;
            tick.High = ReadInt(b, ref offset) / divisor;
            tick.Low = ReadInt(b, ref offset) / divisor;
            tick.Open = ReadInt(b, ref offset) / divisor;
            tick.Close = ReadInt(b, ref offset) / divisor;
            tick.Change = ReadInt(b, ref offset) / divisor;
            uint time = ReadInt(b, ref offset);
            tick.Timestamp = Utils.UnixToDateTime(time);
            return tick;
        }

        /// <summary>
        /// Reads a quote mode tick from raw binary data
        /// </summary>
        private Messages.Tick ReadQuote(byte[] b, ref int offset)
        {
            Messages.Tick tick = new Messages.Tick
            {
                Mode = Constants.MODE_QUOTE,
                InstrumentToken = ReadInt(b, ref offset)
            };

            decimal divisor = (tick.InstrumentToken & 0xff) == 3 ? 10000000.0m : 100.0m;

            tick.Tradable = (tick.InstrumentToken & 0xff) != 9;
            tick.LastPrice = ReadInt(b, ref offset) / divisor;
            tick.LastQuantity = ReadInt(b, ref offset);
            tick.AveragePrice = ReadInt(b, ref offset) / divisor;
            tick.Volume = ReadInt(b, ref offset);
            tick.BuyQuantity = ReadInt(b, ref offset);
            tick.SellQuantity = ReadInt(b, ref offset);
            tick.Open = ReadInt(b, ref offset) / divisor;
            tick.High = ReadInt(b, ref offset) / divisor;
            tick.Low = ReadInt(b, ref offset) / divisor;
            tick.Close = ReadInt(b, ref offset) / divisor;

            return tick;
        }

        /// <summary>
        /// Reads a full mode tick from raw binary data
        /// </summary>
        private Messages.Tick ReadFull(byte[] b, ref int offset)
        {
            Messages.Tick tick = new Messages.Tick();
            tick.Mode = Constants.MODE_FULL;
            tick.InstrumentToken = ReadInt(b, ref offset);

            decimal divisor = (tick.InstrumentToken & 0xff) == 3 ? 10000000.0m : 100.0m;

            tick.Tradable = (tick.InstrumentToken & 0xff) != 9;
            tick.LastPrice = ReadInt(b, ref offset) / divisor;
            tick.LastQuantity = ReadInt(b, ref offset);
            tick.AveragePrice = ReadInt(b, ref offset) / divisor;
            tick.Volume = ReadInt(b, ref offset);
            tick.BuyQuantity = ReadInt(b, ref offset);
            tick.SellQuantity = ReadInt(b, ref offset);
            tick.Open = ReadInt(b, ref offset) / divisor;
            tick.High = ReadInt(b, ref offset) / divisor;
            tick.Low = ReadInt(b, ref offset) / divisor;
            tick.Close = ReadInt(b, ref offset) / divisor;

            // UpstoxAPIConnect 3 fields
            tick.LastTradeTime = Utils.UnixToDateTime(ReadInt(b, ref offset));
            tick.OI = ReadInt(b, ref offset);
            tick.OIDayHigh = ReadInt(b, ref offset);
            tick.OIDayLow = ReadInt(b, ref offset);
            tick.Timestamp = Utils.UnixToDateTime(ReadInt(b, ref offset));


            tick.Bids = new DepthItem[5];
            for (int i = 0; i < 5; i++)
            {
                tick.Bids[i].Quantity = ReadInt(b, ref offset);
                tick.Bids[i].Price = ReadInt(b, ref offset) / divisor;
                tick.Bids[i].Orders = ReadShort(b, ref offset);
                offset += 2;
            }

            tick.Offers = new DepthItem[5];
            for (int i = 0; i < 5; i++)
            {
                tick.Offers[i].Quantity = ReadInt(b, ref offset);
                tick.Offers[i].Price = ReadInt(b, ref offset) / divisor;
                tick.Offers[i].Orders = ReadShort(b, ref offset);
                offset += 2;
            }
            return tick;
        }
        #endregion

    }

}
