﻿using Newtonsoft.Json;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.Logging;
using OsEngine.Market.Servers.BitGet.BitGetSpot.Entity;
using OsEngine.Market.Servers.Entity;
using RestSharp;
using SuperSocket.ClientEngine;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using WebSocket4Net;

namespace OsEngine.Market.Servers.BitGet.BitGetSpot
{
    public class BitGetServerSpot : AServer
    {
        public BitGetServerSpot()
        {
            BitGetServerSpotRealization realization = new BitGetServerSpotRealization();
            ServerRealization = realization;

            CreateParameterString(OsLocalization.Market.ServerParamPublicKey, "");
            CreateParameterPassword(OsLocalization.Market.ServerParamSecretKey, "");
            CreateParameterPassword(OsLocalization.Market.ServerParamPassphrase, "");
        }
    }

    public class BitGetServerSpotRealization : IServerRealization
    {
        #region 1 Constructor, Status, Connection

        public BitGetServerSpotRealization()
        {
            ServerStatus = ServerConnectStatus.Disconnect;

            Thread threadMessageReaderPublic = new Thread(MessageReaderPublic);
            threadMessageReaderPublic.IsBackground = true;
            threadMessageReaderPublic.Name = "MessageReaderPublic";
            threadMessageReaderPublic.Start();

            Thread threadMessageReaderPrivate = new Thread(MessageReaderPrivate);
            threadMessageReaderPrivate.IsBackground = true;
            threadMessageReaderPrivate.Name = "MessageReaderPrivate";
            threadMessageReaderPrivate.Start();

            Thread thread = new Thread(CheckAliveWebSocket);
            thread.Name = "CheckAliveWebSocket";
            thread.Start();
        }

        public void Connect()
        {
            PublicKey = ((ServerParameterString)ServerParameters[0]).Value;
            SeckretKey = ((ServerParameterPassword)ServerParameters[1]).Value;
            Passphrase = ((ServerParameterPassword)ServerParameters[2]).Value;

            if (string.IsNullOrEmpty(PublicKey) ||
                string.IsNullOrEmpty(SeckretKey) ||
                string.IsNullOrEmpty(Passphrase))
            {
                SendLogMessage("Can`t run Bitget Spot connector. No keys or passphrase",
                    LogMessageType.Error);
                return;
            }
                        
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Ssl3
                | SecurityProtocolType.Tls11
                | SecurityProtocolType.Tls12
                | SecurityProtocolType.Tls13
                | SecurityProtocolType.Tls;

            try
            {
                string requestStr = "/api/v2/public/time";
                RestRequest requestRest = new RestRequest(requestStr, Method.GET);
                IRestResponse response = new RestClient(BaseUrl).Execute(requestRest);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    TimeToSendPingPublic = DateTime.Now;
                    TimeToSendPingPrivate = DateTime.Now;
                    FIFOListWebSocketPublicMessage = new ConcurrentQueue<string>();
                    FIFOListWebSocketPrivateMessage = new ConcurrentQueue<string>();
                    CreateWebSocketConnection();
                    _lastConnectionStartTime = DateTime.Now;
                }
                else
                {
                    SendLogMessage("Connection can be open. BitGet. Error request", LogMessageType.Error);

                    if (ServerStatus != ServerConnectStatus.Disconnect)
                    {
                        ServerStatus = ServerConnectStatus.Disconnect;
                        DisconnectEvent();
                    }
                }
            }
            catch (Exception exeption)
            {
                SendLogMessage(exeption.ToString(), LogMessageType.Error);
                SendLogMessage("Connection can be open. BitGet. Error request", LogMessageType.Error);

                if (ServerStatus != ServerConnectStatus.Disconnect)
                {
                    ServerStatus = ServerConnectStatus.Disconnect;
                    DisconnectEvent();
                }
            }
        }

        public void Dispose()
        {
            try
            {
                UnsubscribeFromAllWebSockets();
                _subscribledSecutiries.Clear();
                DeleteWebSocketConnection();
            }
            catch (Exception exeption)
            {
                SendLogMessage(exeption.ToString(), LogMessageType.Error);
            }

            FIFOListWebSocketPublicMessage = null;
            FIFOListWebSocketPrivateMessage = null;

            if (ServerStatus != ServerConnectStatus.Disconnect)
            {
                ServerStatus = ServerConnectStatus.Disconnect;
                DisconnectEvent();
            }
        }

        public ServerType ServerType
        {
            get { return ServerType.BitGetSpot; }
        }

        public ServerConnectStatus ServerStatus { get; set; }

        public DateTime ServerTime { get; set; }

        private DateTime _lastConnectionStartTime = DateTime.MinValue;

        public event Action ConnectEvent;

        public event Action DisconnectEvent;

        #endregion

        #region 2 Properties

        public List<IServerParameter> ServerParameters { get; set; }

        private string BaseUrl = "https://api.bitget.com";

        private string PublicKey;

        private string SeckretKey;

        private string Passphrase;

        private int _limitCandlesData = 200;

        private int _limitCandlesTrader = 1000;

        #endregion

        #region 3 Securities

        public void GetSecurities()
        {
            try
            {
                string requestStr = $"/api/v2/spot/public/symbols";
                RestRequest requestRest = new RestRequest(requestStr, Method.GET);
                IRestResponse response = new RestClient(BaseUrl).Execute(requestRest);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    SendLogMessage($"Http State Code: {response.StatusCode} - {response.Content}", LogMessageType.Error);
                    return;
                }

                ResponseRestMessage<List<RestMessageSymbol>> symbols = JsonConvert.DeserializeAnonymousType(response.Content, new ResponseRestMessage<List<RestMessageSymbol>>());

                List<Security> securities = new List<Security>();

                if (symbols.data.Count == 0)
                {
                    return;
                }

                for (int i = 0; i < symbols.data.Count; i++)
                {
                    RestMessageSymbol item = symbols.data[i];

                    decimal priceStep = GetPriceStep(Convert.ToInt32(item.pricePrecision));

                    if (item.status.Equals("online"))
                    {
                        Security newSecurity = new Security();

                        newSecurity.Exchange = ServerType.BitGetSpot.ToString();
                        newSecurity.DecimalsVolume = Convert.ToInt32(item.quantityPrecision);
                        newSecurity.Lot = GetVolumeStep(newSecurity.DecimalsVolume);
                        newSecurity.Name = item.symbol;
                        newSecurity.NameFull = item.symbol;
                        newSecurity.NameClass = "Spot";
                        newSecurity.NameId = item.symbol;
                        newSecurity.SecurityType = SecurityType.CurrencyPair;
                        newSecurity.Decimals = Convert.ToInt32(item.pricePrecision);
                        newSecurity.PriceStep = priceStep;
                        newSecurity.PriceStepCost = priceStep;
                        newSecurity.State = SecurityStateType.Activ;

                        securities.Add(newSecurity);
                    }
                }
                SecurityEvent(securities);
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
           
        }

        private decimal GetVolumeStep(int quantityPrecision)
        {
            if (quantityPrecision == 0)
            {
                return 1;
            }
            string priceStep = "0,";
            for (int i = 0; i < quantityPrecision - 1; i++)
            {
                priceStep += "0";
            }

            priceStep += "1";

            return priceStep.ToDecimal();
        }

        private decimal GetPriceStep(int pricePrecision)
        {
            if (pricePrecision == 0)
            {
                return 1;
            }

            string res = String.Empty;

            for (int i = 0; i < pricePrecision; i++)
            {
                if (i == 0)
                {
                    res += "0,";
                }
                else
                {
                    res += "0";
                }
            }
            res += "1";

            return res.ToDecimal();
        }

        public event Action<List<Security>> SecurityEvent;

        #endregion

        #region 4 Portfolios

        public void GetPortfolios()
        {
            CreateQueryPortfolio(true);
        }
                
        private void CreateQueryPortfolio(bool IsUpdateValueBegin)
        {
            try
            {
                HttpResponseMessage responseMessage = CreatePrivateQueryOrders("/api/v2/spot/account/assets", "GET", null, null);
                string json = responseMessage.Content.ReadAsStringAsync().Result;

                ResponseRestMessage<List<RestMessageAccount>> stateResponse = JsonConvert.DeserializeAnonymousType(json, new ResponseRestMessage<List<RestMessageAccount>>());

                if (responseMessage.StatusCode == HttpStatusCode.OK)
                {
                    if (stateResponse.code.Equals("00000") == true)
                    {
                        ConvertPorfolio(stateResponse, IsUpdateValueBegin);
                    }
                    else
                    {
                        SendLogMessage($"Code: {stateResponse.code}\n"
                            + $"Message: {stateResponse.msg}", LogMessageType.Error);
                    }
                }
                else
                {
                    SendLogMessage($"Http State Code: {responseMessage.StatusCode}", LogMessageType.Error);

                    if (stateResponse != null && stateResponse.code != null)
                    {
                        SendLogMessage($"Code: {stateResponse.code}\n"
                            + $"Message: {stateResponse.msg}", LogMessageType.Error);
                    }
                }
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }

        private void ConvertPorfolio(ResponseRestMessage<List<RestMessageAccount>> json, bool IsUpdateValueBegin)
        {           
            Portfolio portfolio = new Portfolio();

            portfolio.Number = "BitGetSpot";
            portfolio.ValueBegin = 1;
            portfolio.ValueCurrent = 1;

            for (int i = 0; i < json.data.Count; i++)
            {
                PositionOnBoard pos = new PositionOnBoard();

                pos.PortfolioName = "BitGetSpot";
                pos.SecurityNameCode = json.data[i].coin.ToUpper();
                pos.ValueBlocked = json.data[i].frozen.ToDecimal();
                pos.ValueCurrent = json.data[i].available.ToDecimal();

                if (IsUpdateValueBegin)
                {
                    pos.ValueBegin = json.data[i].available.ToDecimal();
                }

                portfolio.SetNewPosition(pos);
            }
        
            PortfolioEvent(new List<Portfolio> { portfolio });
        }

        public event Action<List<Portfolio>> PortfolioEvent;

        #endregion

        #region 5 Data

        public List<Candle> GetLastCandleHistory(Security security, TimeFrameBuilder timeFrameBuilder, int candleCount)
        {
            int tfTotalMinutes = (int)timeFrameBuilder.TimeFrameTimeSpan.TotalMinutes;
            DateTime endTime = DateTime.UtcNow;
            DateTime startTime = endTime.AddMinutes(-tfTotalMinutes * candleCount);

            return GetCandleData(security, timeFrameBuilder, startTime, endTime, endTime, false);
        }

        public List<Candle> GetCandleDataToSecurity(Security security, TimeFrameBuilder timeFrameBuilder, DateTime startTime, DateTime endTime, DateTime actualTime)
        {
            return GetCandleData(security, timeFrameBuilder, startTime, endTime, actualTime, true);
        }

        private List<Candle> GetCandleData(Security security, TimeFrameBuilder timeFrameBuilder, DateTime startTime, DateTime endTime, DateTime actualTime, bool isOsData)
        {
            if (!CheckTime(startTime, endTime, actualTime))
            {
                return null;
            }

            int tfTotalMinutes = (int)timeFrameBuilder.TimeFrameTimeSpan.TotalMinutes;

            if (!CheckTf(tfTotalMinutes))
            {
                return null;
            }

            int limitCandles = _limitCandlesTrader;

            if (isOsData)
            {
                limitCandles = _limitCandlesData;
            }

            TimeSpan span = endTime - startTime;

            if (limitCandles > span.TotalMinutes / tfTotalMinutes)
            {
                limitCandles = (int)Math.Round(span.TotalMinutes / tfTotalMinutes, MidpointRounding.AwayFromZero);
            }

            List<Candle> allCandles = new List<Candle>();

            DateTime startTimeData = startTime;
            DateTime endTimeData = startTimeData.AddMinutes(tfTotalMinutes * limitCandles);

            do
            {
                long from = TimeManager.GetTimeStampMilliSecondsToDateTime(startTimeData);
                long to = TimeManager.GetTimeStampMilliSecondsToDateTime(endTimeData);

                string interval = GetInterval(timeFrameBuilder.TimeFrameTimeSpan);

                List<Candle> candles = RequestCandleHistory(security, interval, from, to, isOsData, limitCandles);

                if (candles == null || candles.Count == 0)
                {
                    break;
                }

                Candle last = candles[candles.Count - 1];

                if (allCandles.Count > 0)
                {
                    if (allCandles[allCandles.Count - 1].TimeStart == candles[0].TimeStart)
                    {
                        candles.RemoveAt(0);
                    }
                }

                if (last.TimeStart >= endTime)

                {
                    for (int i = 0; i < candles.Count; i++)
                    {
                        if (candles[i].TimeStart <= endTime)
                        {
                            allCandles.Add(candles[i]);
                        }
                    }
                    break;
                }

                allCandles.AddRange(candles);

                startTimeData = endTimeData;
                endTimeData = startTimeData.AddMinutes(tfTotalMinutes * limitCandles);

                if (startTimeData >= endTime)
                {
                    break;
                }

                if (endTimeData > endTime)
                {
                    endTimeData = endTime;
                }

                span = endTimeData - startTimeData;

                if (limitCandles > span.TotalMinutes / tfTotalMinutes)
                {
                    limitCandles = (int)Math.Round(span.TotalMinutes / tfTotalMinutes, MidpointRounding.AwayFromZero);
                }

            } while (true);

            return allCandles;
        }

        private bool CheckTime(DateTime startTime, DateTime endTime, DateTime actualTime)
        {
            if (startTime >= endTime ||
                startTime >= DateTime.Now ||
                actualTime > endTime ||
                actualTime > DateTime.Now ||
                endTime < DateTime.UtcNow.AddYears(-20))
            {
                return false;
            }
            return true;
        }

        private bool CheckTf(int timeFrameMinutes)
        {
            if (timeFrameMinutes == 1 ||
                timeFrameMinutes == 3 ||
                timeFrameMinutes == 5 ||
                timeFrameMinutes == 15 ||
                timeFrameMinutes == 30 ||
                timeFrameMinutes == 60 ||
                timeFrameMinutes == 240)
            {
                return true;
            }
            return false;
        }

        private string GetInterval(TimeSpan tf)
        {
            if (tf.Minutes != 0)
            {
                return $"{tf.Minutes}min";
            }
            else
            {
                return $"{tf.Hours}h";
            }
        }

        private readonly RateGate _rgCandleData = new RateGate(1, TimeSpan.FromMilliseconds(100));

        private List<Candle> RequestCandleHistory(Security security, string interval, long startTime, long endTime, bool isOsData, int limitCandles)
        {
            _rgCandleData.WaitToProceed(100);

            string stringUrl = "/api/v2/spot/market/candles";

            if (isOsData)
            {
                stringUrl = "/api/v2/spot/market/history-candles";
            }

            try
            {
                string requestStr = $"{stringUrl}?symbol={security.Name}&productType={security.NameClass.ToLower()}&" +
                    $"startTime={startTime}&granularity={interval}&limit={limitCandles}&endTime={endTime}";
                RestRequest requestRest = new RestRequest(requestStr, Method.GET);
                IRestResponse response = new RestClient(BaseUrl).Execute(requestRest);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return ConvertCandles(response.Content);
                }
                else
                {
                    SendLogMessage($"Http State Code: {response.StatusCode} - {response.Content}", LogMessageType.Error);
                }
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }

            return null;
        }

        private List<Candle> ConvertCandles(string json)
        {
            RestMessageCandle symbols = JsonConvert.DeserializeObject<RestMessageCandle>(json);

            List<Candle> candles = new List<Candle>();

            if (symbols.data.Count == 0)
            {
                return null;
            }

            for (int i = 0; i < symbols.data.Count; i++)
            {
                if (CheckCandlesToZeroData(symbols.data[i]))
                {
                    continue;
                }

                Candle candle = new Candle();

                candle.State = CandleState.Finished;
                candle.TimeStart = TimeManager.GetDateTimeFromTimeStamp(long.Parse(symbols.data[i][0]));
                candle.Volume = symbols.data[i][5].ToDecimal();
                candle.Close = symbols.data[i][4].ToDecimal();
                candle.High = symbols.data[i][2].ToDecimal();
                candle.Low = symbols.data[i][3].ToDecimal();
                candle.Open = symbols.data[i][1].ToDecimal();

                candles.Add(candle);
            }
            return candles;
        }

        private bool CheckCandlesToZeroData(List<string> item)
        {
            if (item[1].ToDecimal() == 0 ||
                item[2].ToDecimal() == 0 ||
                item[3].ToDecimal() == 0 ||
                item[4].ToDecimal() == 0)
            {
                return true;
            }
            return false;
        }

        public List<Trade> GetTickDataToSecurity(Security security, DateTime startTime, DateTime endTime, DateTime actualTime)
        {
            return null;
        }

        #endregion

        #region 6 WebSocket creation

        private string _webSocketUrlPublic = "wss://ws.bitget.com/v2/ws/public";

        private string _webSocketUrlPrivate = "wss://ws.bitget.com/v2/ws/private";

        private WebSocket _webSocketPublic;

        private WebSocket _webSocketPrivate;

        private bool _publicSocketOpen = false;

        private bool _privateSocketOpen = false;

        private void CreateWebSocketConnection()
        {
            try
            {
                _publicSocketOpen = false;
                _privateSocketOpen = false;

                if (_webSocketPublic != null)
                {
                    return;
                }

                _webSocketPublic = new WebSocket(_webSocketUrlPublic);
                _webSocketPublic.EnableAutoSendPing = true;
                _webSocketPublic.AutoSendPingInterval = 10;
                _webSocketPublic.Opened += WebSocketPublic_Opened;
                _webSocketPublic.Closed += WebSocketPublic_Closed;
                _webSocketPublic.MessageReceived += WebSocketPublic_MessageReceived;
                _webSocketPublic.Error += WebSocketPublic_Error;
                _webSocketPublic.Open();

                if (_webSocketPrivate != null)
                {
                    return;
                }

                _webSocketPrivate = new WebSocket(_webSocketUrlPrivate);
                _webSocketPrivate.EnableAutoSendPing = true;
                _webSocketPrivate.AutoSendPingInterval = 10;
                _webSocketPrivate.Opened += WebSocketPrivate_Opened;
                _webSocketPrivate.Closed += WebSocketPrivate_Closed;
                _webSocketPrivate.MessageReceived += WebSocketPrivate_MessageReceived;
                _webSocketPrivate.Error += WebSocketPrivate_Error;
                _webSocketPrivate.Open();
            }
            catch (Exception exeption)
            {
                SendLogMessage(exeption.ToString(), LogMessageType.Error);
            }
        }

        private void DeleteWebSocketConnection()
        {
            if (_webSocketPublic != null)
            {
                try
                {
                    _webSocketPublic.Opened -= WebSocketPublic_Opened;
                    _webSocketPublic.Closed -= WebSocketPublic_Closed;
                    _webSocketPublic.MessageReceived -= WebSocketPublic_MessageReceived;
                    _webSocketPublic.Error -= WebSocketPublic_Error;
                    _webSocketPublic.Close();
                }
                catch
                {
                    // ignore
                }

                _webSocketPublic = null;
            }

            if (_webSocketPrivate != null)
            {
                try
                {
                    _webSocketPrivate.Opened -= WebSocketPrivate_Opened;
                    _webSocketPrivate.Closed -= WebSocketPrivate_Closed;
                    _webSocketPrivate.MessageReceived -= WebSocketPrivate_MessageReceived;
                    _webSocketPrivate.Error -= WebSocketPrivate_Error;
                    _webSocketPrivate.Close();
                }
                catch
                {
                    // ignore
                }

                _webSocketPrivate = null;
            }
        }

        private string _socketActivateLocker = "socketAcvateLocker";

        private void CheckSocketsActivate()
        {
            lock (_socketActivateLocker)
            {
                if (_publicSocketOpen
                    && _privateSocketOpen
                    && ServerStatus == ServerConnectStatus.Disconnect)
                {
                    ServerStatus = ServerConnectStatus.Connect;

                    if (ConnectEvent != null)
                    {
                        ConnectEvent();
                    }
                }
            }
        }

        private void CreateAuthMessageWebSocekt()
        {
            try
            {
                string TimeStamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                string Sign = GenerateSignature(TimeStamp, "GET", "/user/verify", null, null, SeckretKey);

                RequestWebsocketAuth requestWebsocketAuth = new RequestWebsocketAuth();

                requestWebsocketAuth.op = "login";
                requestWebsocketAuth.args = new List<AuthItem>();
                requestWebsocketAuth.args.Add(new AuthItem());
                requestWebsocketAuth.args[0].apiKey = PublicKey;
                requestWebsocketAuth.args[0].passphrase = Passphrase;
                requestWebsocketAuth.args[0].timestamp = TimeStamp;
                requestWebsocketAuth.args[0].sign = Sign;

                string AuthJson = JsonConvert.SerializeObject(requestWebsocketAuth);

                _webSocketPrivate.Send(AuthJson);
            }
            catch (Exception ex)
            {
                SendLogMessage(ex.Message, LogMessageType.Error);
            }
        }

        #endregion

        #region 7 WebSocket events

        private void WebSocketPublic_Opened(object sender, EventArgs e)
        {
            try
            {
                if (ServerStatus == ServerConnectStatus.Disconnect)
                {
                    SendLogMessage("Bitget WebSocket Public connection open", LogMessageType.System);
                    _publicSocketOpen = true;
                    CheckSocketsActivate();
                }
            }
            catch (Exception ex)
            {
                SendLogMessage(ex.ToString(), LogMessageType.Error);
            }
        }

        private void WebSocketPublic_Closed(object sender, EventArgs e)
        {
            try
            {
                if (DisconnectEvent != null
                 & ServerStatus != ServerConnectStatus.Disconnect)
                {
                    SendLogMessage("Connection Closed by BitGet. WebSocket Public Closed Event", LogMessageType.System);
                    ServerStatus = ServerConnectStatus.Disconnect;
                    DisconnectEvent();
                }
            }
            catch (Exception ex)
            {
                SendLogMessage(ex.ToString(), LogMessageType.Error);
            }
        }

        private void WebSocketPublic_MessageReceived(object sender, MessageReceivedEventArgs e)
        {
            try
            {
                if (e == null)
                {
                    return;
                }
                if (string.IsNullOrEmpty(e.Message))
                {
                    return;
                }
                if (e.Message.Length == 4)
                { // pong message
                    return;
                }


                if (FIFOListWebSocketPublicMessage == null)
                {
                    return;
                }

                FIFOListWebSocketPublicMessage.Enqueue(e.Message);
            }
            catch (Exception error)
            {
                SendLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void WebSocketPublic_Error(object sender, ErrorEventArgs error)
        {
            if (error.Exception != null)
            {
                SendLogMessage(error.Exception.ToString(), LogMessageType.Error);
            }
        }

        private void WebSocketPrivate_Opened(object sender, EventArgs e)
        {
            try
            {
                CreateAuthMessageWebSocekt();
                SendLogMessage("Bitget WebSocket Private connection open", LogMessageType.System);
                _privateSocketOpen = true;
                CheckSocketsActivate();
            }
            catch (Exception error)
            {
                SendLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void WebSocketPrivate_Closed(object sender, EventArgs e)
        {
            if (DisconnectEvent != null
                && ServerStatus != ServerConnectStatus.Disconnect)
            {
                SendLogMessage("Connection Closed by BitGet. WebSocket Private Closed Event", LogMessageType.System);
                ServerStatus = ServerConnectStatus.Disconnect;
                DisconnectEvent();
            }
        }

        private void WebSocketPrivate_MessageReceived(object sender, MessageReceivedEventArgs e)
        {
            try
            {
                if (e == null)
                {
                    return;
                }
                if (string.IsNullOrEmpty(e.Message))
                {
                    return;
                }
                if (e.Message.Length == 4)
                { // pong message
                    return;
                }

                if (e.Message.Contains("login"))
                {
                    SubscriblePrivate();
                }

                if (FIFOListWebSocketPrivateMessage == null)
                {
                    return;
                }

                FIFOListWebSocketPrivateMessage.Enqueue(e.Message);
            }
            catch (Exception error)
            {
                SendLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void WebSocketPrivate_Error(object sender, ErrorEventArgs error)
        {
            if (error.Exception != null)
            {
                SendLogMessage(error.Exception.ToString(), LogMessageType.Error);
            }
        }

        #endregion

        #region 8 WebSocket check alive

        private DateTime TimeToSendPingPublic = DateTime.Now;
        private DateTime TimeToSendPingPrivate = DateTime.Now;

        private void CheckAliveWebSocket()
        {
            while (true)
            {
                try
                {
                    Thread.Sleep(1000);

                    if (ServerStatus == ServerConnectStatus.Disconnect)
                    {
                        continue;
                    }

                    if (_webSocketPublic != null &&
                        (_webSocketPublic.State == WebSocketState.Open ||
                        _webSocketPublic.State == WebSocketState.Connecting)
                        )
                    {
                        if (TimeToSendPingPublic.AddSeconds(25) < DateTime.Now)
                        {
                            _webSocketPublic.Send("ping");
                            TimeToSendPingPublic = DateTime.Now;
                        }
                    }
                    else
                    {
                        if (ServerStatus != ServerConnectStatus.Disconnect)
                        {
                            ServerStatus = ServerConnectStatus.Disconnect;
                            DisconnectEvent();
                        }
                    }

                    if (_webSocketPrivate != null &&
                        (_webSocketPrivate.State == WebSocketState.Open ||
                        _webSocketPrivate.State == WebSocketState.Connecting)
                        )
                    {
                        if (TimeToSendPingPrivate.AddSeconds(25) < DateTime.Now)
                        {
                            _webSocketPrivate.Send("ping");
                            TimeToSendPingPrivate = DateTime.Now;
                        }
                    }
                    else
                    {
                        if (ServerStatus != ServerConnectStatus.Disconnect)
                        {
                            ServerStatus = ServerConnectStatus.Disconnect;
                            DisconnectEvent();
                        }
                    }

                }
                catch (Exception ex)
                {
                    SendLogMessage(ex.ToString(), LogMessageType.Error);
                    Thread.Sleep(3000);
                }
            }
        }

        #endregion

        #region 9 Security subscrible

        private RateGate _rateGateSubscrible = new RateGate(1, TimeSpan.FromMilliseconds(350));

        private List<string> _subscribledSecutiries = new List<string>();

        public void Subscrible(Security security)
        {
            try
            {
                _rateGateSubscrible.WaitToProceed();
                CreateSubscribleSecurityMessageWebSocket(security);

            }
            catch (Exception exeption)
            {
                SendLogMessage(exeption.ToString(), LogMessageType.Error);
            }
        }

        private void CreateSubscribleSecurityMessageWebSocket(Security security)
        {
            try
            {
                if (ServerStatus == ServerConnectStatus.Disconnect)
                {
                    return;
                }

                if (_subscribledSecutiries != null)
                {
                    for (int i = 0; i < _subscribledSecutiries.Count; i++)
                    {
                        if (_subscribledSecutiries.Equals(security.Name))
                        {
                            return;
                        }
                    }
                }

                _subscribledSecutiries.Add(security.Name);

                _webSocketPublic.Send($"{{\"op\": \"subscribe\",\"args\": [{{\"instType\": \"SPOT\",\"channel\": \"books15\",\"instId\": \"{security.Name}\"}}]}}");
                _webSocketPublic.Send($"{{\"op\": \"subscribe\",\"args\": [{{ \"instType\": \"SPOT\",\"channel\": \"trade\",\"instId\": \"{security.Name}\"}}]}}");

                _webSocketPrivate.Send($"{{\"op\": \"subscribe\",\"args\": [{{\"instType\": \"SPOT\",\"channel\": \"orders\",\"instId\": \"{security.Name}\"}}]}}");
            }
            catch (Exception ex)
            {
                SendLogMessage(ex.Message, LogMessageType.Error);
            }
        }

        private void SubscriblePrivate()
        {
            try
            {
                _webSocketPrivate.Send($"{{\"op\": \"subscribe\",\"args\": [{{\"instType\": \"SPOT\",\"channel\": \"account\",\"coin\": \"default\"}}]}}");
            }
            catch (Exception exeption)
            {
                SendLogMessage(exeption.Message, LogMessageType.Error);
            }
        }

        private void UnsubscribeFromAllWebSockets()
        {
            if (_webSocketPublic != null)
            {
                try
                {
                    if (_subscribledSecutiries != null)
                    {
                        for (int i = 0; i < _subscribledSecutiries.Count; i++)
                        {
                            _webSocketPublic.Send($"{{\"op\": \"unsubscribe\",\"args\": [{{\"instType\": \"SPOT\",\"channel\": \"books15\",\"instId\": \"{_subscribledSecutiries[i]}\"}}]}}");
                            _webSocketPublic.Send($"{{\"op\": \"unsubscribe\",\"args\": [{{\"instType\": \"SPOT\",\"channel\": \"trade\",\"instId\": \"{_subscribledSecutiries[i]}\"}}]}}");

                            _webSocketPrivate.Send($"{{\"op\": \"subscribe\",\"args\": [{{\"instType\": \"SPOT\",\"channel\": \"orders\",\"instId\": \"{_subscribledSecutiries[i]}\"}}]}}");
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }

            if (_webSocketPrivate != null)
            {
                try
                {
                    _webSocketPrivate.Send($"{{\"op\": \"unsubscribe\",\"args\": [{{\"instType\": \"SPOT\",\"channel\": \"account\",\"coin\": \"default\"}}]}}");                 
                }
                catch
                {
                    // ignore
                }
            }
        }

        #endregion

        #region 10 WebSocket parsing the messages

        private ConcurrentQueue<string> FIFOListWebSocketPublicMessage = new ConcurrentQueue<string>();

        private ConcurrentQueue<string> FIFOListWebSocketPrivateMessage = new ConcurrentQueue<string>();

        private void MessageReaderPublic()
        {
            Thread.Sleep(5000);

            while (true)
            {
                try
                {
                    if (ServerStatus == ServerConnectStatus.Disconnect)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }

                    if (FIFOListWebSocketPublicMessage.IsEmpty)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    string message = null;

                    FIFOListWebSocketPublicMessage.TryDequeue(out message);

                    if (message == null)
                    {
                        continue;
                    }

                    ResponseWebSocketMessageSubscrible SubscribleState = null;

                    try
                    {
                        SubscribleState = JsonConvert.DeserializeAnonymousType(message, new ResponseWebSocketMessageSubscrible());
                    }
                    catch (Exception error)
                    {
                        SendLogMessage("Error in message reader: " + error.ToString(), LogMessageType.Error);
                        SendLogMessage("message str: \n" + message, LogMessageType.Error);
                        continue;
                    }

                    if (SubscribleState.code != null)
                    {
                        if (SubscribleState.code.Equals("0") == false)
                        {
                            SendLogMessage("WebSocket listener error", LogMessageType.Error);
                            SendLogMessage(SubscribleState.code + "\n" +
                                SubscribleState.msg, LogMessageType.Error);

                            if (_lastConnectionStartTime.AddMinutes(5) > DateTime.Now)
                            { // если на старте вёб-сокета проблемы, то надо его перезапускать
                                ServerStatus = ServerConnectStatus.Disconnect;
                                DisconnectEvent();
                            }
                        }

                        continue;
                    }
                    else
                    {
                        ResponseWebSocketMessageAction<object> action = JsonConvert.DeserializeAnonymousType(message, new ResponseWebSocketMessageAction<object>());

                        if (action.arg != null)
                        {
                            if (action.arg.channel.Equals("books15"))
                            {
                                UpdateDepth(message);
                                continue;
                            }
                            if (action.arg.channel.Equals("trade"))
                            {
                                UpdateTrade(message);
                                continue;
                            }
                        }
                    }
                }
                catch (Exception exeption)
                {
                    SendLogMessage(exeption.ToString(), LogMessageType.Error);
                    Thread.Sleep(3000);
                }
            }
        }

        private void MessageReaderPrivate()
        {
            Thread.Sleep(5000);

            while (true)
            {
                try
                {
                    if (ServerStatus == ServerConnectStatus.Disconnect)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }

                    if (FIFOListWebSocketPrivateMessage.IsEmpty)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    string message = null;

                    FIFOListWebSocketPrivateMessage.TryDequeue(out message);

                    if (message == null)
                    {
                        continue;
                    }

                    ResponseWebSocketMessageSubscrible SubscribleState = null;

                    try
                    {
                        SubscribleState = JsonConvert.DeserializeAnonymousType(message, new ResponseWebSocketMessageSubscrible());
                    }
                    catch (Exception error)
                    {
                        SendLogMessage("Error in message reader: " + error.ToString(), LogMessageType.Error);
                        SendLogMessage("message str: \n" + message, LogMessageType.Error);
                        continue;
                    }

                    if (SubscribleState.code != null)
                    {
                        if (SubscribleState.code.Equals("0") == false)
                        {
                            SendLogMessage("WebSocket listener error", LogMessageType.Error);
                            SendLogMessage(SubscribleState.code + "\n" +
                                SubscribleState.msg, LogMessageType.Error);

                            if (_lastConnectionStartTime.AddMinutes(5) > DateTime.Now)
                            { // если на старте вёб-сокета проблемы, то надо его перезапускать
                                ServerStatus = ServerConnectStatus.Disconnect;
                                DisconnectEvent();
                            }
                        }

                        continue;
                    }
                    else
                    {
                        ResponseWebSocketMessageAction<object> action = JsonConvert.DeserializeAnonymousType(message, new ResponseWebSocketMessageAction<object>());

                        if (action.arg != null)
                        {
                            if (action.arg.channel.Equals("account"))
                            {
                                UpdateAccount(message);
                                continue;
                            }
                           
                            if (action.arg.channel.Equals("orders"))
                            {
                                UpdateOrder(message);
                                continue;
                            }
                        }
                    }
                }
                catch (Exception exeption)
                {
                    SendLogMessage(exeption.ToString(), LogMessageType.Error);
                    Thread.Sleep(3000);
                }
            }
        }

        private void UpdateAccount(string message)
        {
            try
            {
                ResponseWebSocketMessageAction<List<ResponseWebSocketAccount>> assets = JsonConvert.DeserializeAnonymousType(message, new ResponseWebSocketMessageAction<List<ResponseWebSocketAccount>>());

                if (assets.data == null ||
                    assets.data.Count == 0)
                {
                    return;
                }

                Portfolio portfolio = new Portfolio();
                portfolio.Number = "BitGetSpot";
                portfolio.ValueBegin = 1;
                portfolio.ValueCurrent = 1;


                for (int i = 0; i < assets.data.Count; i++)
                {
                    PositionOnBoard pos = new PositionOnBoard();

                    pos.PortfolioName = "BitGetSpot";
                    pos.SecurityNameCode = assets.data[i].coin;
                    pos.ValueBlocked = assets.data[i].frozen.ToDecimal();
                    pos.ValueCurrent = assets.data[i].available.ToDecimal();

                    portfolio.SetNewPosition(pos);
                }

                PortfolioEvent(new List<Portfolio> { portfolio });
            }
            catch (Exception ex)
            {
                SendLogMessage(ex.Message, LogMessageType.Error);
            }
        }

        private void UpdateOrder(string message)
        {
            try
            {
                ResponseWebSocketMessageAction<List<ResponseWebSocketOrder>> order = JsonConvert.DeserializeAnonymousType(message, new ResponseWebSocketMessageAction<List<ResponseWebSocketOrder>>());

                if (order.data == null ||
                    order.data.Count == 0)
                {
                    return;
                }

                for (int i = 0; i < order.data.Count; i++)
                {
                    ResponseWebSocketOrder item = order.data[i];

                    if (string.IsNullOrEmpty(item.orderId))
                    {
                        continue;
                    }

                    OrderStateType stateType = GetOrderState(item.status);

                    if (item.orderType.Equals("market") &&
                        stateType != OrderStateType.Done &&
                        stateType != OrderStateType.Partial)
                    {
                        continue;
                    }

                    Order newOrder = new Order();
                    newOrder.SecurityNameCode = item.instId;
                    newOrder.TimeCallBack = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(item.cTime));
                    int.TryParse(item.clientOid, out newOrder.NumberUser);
                    newOrder.NumberMarket = item.orderId.ToString();
                    newOrder.Side = item.side.Equals("buy") ? Side.Buy : Side.Sell;
                    newOrder.State = stateType;
                    newOrder.Volume = item.size.ToDecimal();
                    newOrder.Price = item.price.ToDecimal();
                    newOrder.ServerType = ServerType.BitGetSpot;
                    newOrder.PortfolioNumber = "BitGetSpot";
                    newOrder.SecurityClassCode = order.arg.instType.ToString();
                    newOrder.TypeOrder = OrderPriceType.Limit;
                    
                    MyOrderEvent(newOrder);

                    if (newOrder.State == OrderStateType.Partial)
                    {
                        MyTrade myTrade = new MyTrade();
                        myTrade.Time = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(item.fillTime));
                        myTrade.NumberOrderParent = item.orderId.ToString();
                        myTrade.NumberTrade = item.tradeId;
                        myTrade.Price = item.fillPrice.ToDecimal();
                        myTrade.SecurityNameCode = item.instId;
                        myTrade.Side = item.side.Equals("buy") ? Side.Buy : Side.Sell;

                        if (string.IsNullOrEmpty(item.fillFeeCoin) == false
                            && string.IsNullOrEmpty(item.fillFee) == false
                            && item.fillFee.ToDecimal() != 0)
                        {
                            if (myTrade.SecurityNameCode.StartsWith(item.fillFeeCoin))
                            {
                                myTrade.Volume = Math.Round(item.baseVolume.ToDecimal() + item.fillFee.ToDecimal(), 6, MidpointRounding.AwayFromZero);
                            }
                            else
                            {
                                myTrade.Volume = item.baseVolume.ToDecimal();
                            }
                        }
                        else
                        {
                            myTrade.Volume = item.baseVolume.ToDecimal();
                        }
                       
                        MyTradeEvent(myTrade);
                    }
                }
            }
            catch (Exception ex)
            {
                SendLogMessage(ex.Message, LogMessageType.Error);
            }
        }

        private void UpdateTrade(string message)
        {
            try
            {
                ResponseWebSocketMessageAction<List<ResponseWebsocketTrade>> responseTrade = JsonConvert.DeserializeAnonymousType(message, new ResponseWebSocketMessageAction<List<ResponseWebsocketTrade>>());

                if (responseTrade == null)
                {
                    return;
                }

                if (responseTrade.data == null)
                {
                    return;
                }

                if (responseTrade.data.Count == 0)
                {
                    return;
                }

                long time = 0;

                for (int i = 0; i < responseTrade.data.Count; i++)
                {                   
                    Trade trade = new Trade();

                    if (i == 0)
                    {
                        trade.Time = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(responseTrade.data[i].ts));
                        time = Convert.ToInt64(responseTrade.data[i].ts);
                    }
                    else
                    {
                        time += 1;
                        trade.Time = TimeManager.GetDateTimeFromTimeStamp(time);
                    }

                    trade.SecurityNameCode = responseTrade.arg.instId;
                    trade.Price = responseTrade.data[i].price.ToDecimal();
                    trade.Id = responseTrade.data[i].tradeId;

                    if (trade.Id == null)
                    {
                        return;
                    }
                    
                    trade.Volume = responseTrade.data[i].size.ToDecimal();
                    trade.Side = responseTrade.data[i].side.Equals("buy") ? Side.Buy : Side.Sell;

                    NewTradesEvent(trade);
                }                
            }
            catch (Exception ex)
            {
                SendLogMessage(ex.Message, LogMessageType.Error);
            }
        }

        private void UpdateDepth(string message)
        {
            try
            {
                ResponseWebSocketMessageAction<List<ResponseWebSocketDepthItem>> responseDepth = JsonConvert.DeserializeAnonymousType(message, new ResponseWebSocketMessageAction<List<ResponseWebSocketDepthItem>>());

                if (responseDepth.data == null)
                {
                    return;
                }

                if (responseDepth.data[0].asks.Count == 0 && responseDepth.data[0].bids.Count == 0)
                {
                    return;
                }

                MarketDepth marketDepth = new MarketDepth();

                List<MarketDepthLevel> ascs = new List<MarketDepthLevel>();
                List<MarketDepthLevel> bids = new List<MarketDepthLevel>();

                marketDepth.SecurityNameCode = responseDepth.arg.instId;

                for (int i = 0; i < responseDepth.data[0].asks.Count; i++)
                {
                    decimal ask = responseDepth.data[0].asks[i][1].ToString().ToDecimal();
                    decimal price = responseDepth.data[0].asks[i][0].ToString().ToDecimal();

                    if (ask == 0 ||
                        price == 0)
                    {
                        continue;
                    }

                    MarketDepthLevel level = new MarketDepthLevel();
                    level.Ask = ask;
                    level.Price = price;
                    ascs.Add(level);
                }

                for (int i = 0; i < responseDepth.data[0].bids.Count; i++)
                {
                    decimal bid = responseDepth.data[0].bids[i][1].ToString().ToDecimal();
                    decimal price = responseDepth.data[0].bids[i][0].ToString().ToDecimal();

                    if (bid == 0 ||
                        price == 0)
                    {
                        continue;
                    }

                    MarketDepthLevel level = new MarketDepthLevel();
                    level.Bid = bid;
                    level.Price = price;
                    bids.Add(level);
                }

                marketDepth.Asks = ascs;
                marketDepth.Bids = bids;

                marketDepth.Time = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(responseDepth.data[0].ts));

                if (marketDepth.Time < _lastTimeMd)
                {
                    marketDepth.Time = _lastTimeMd;
                }
                else if (marketDepth.Time == _lastTimeMd)
                {
                    _lastTimeMd = DateTime.FromBinary(_lastTimeMd.Ticks + 1);
                    marketDepth.Time = _lastTimeMd;
                }

                _lastTimeMd = marketDepth.Time;

                MarketDepthEvent(marketDepth);
            }
            catch (Exception ex)
            {
                SendLogMessage(ex.Message, LogMessageType.Error);
            }
        }

        private DateTime _lastTimeMd;

        public event Action<Order> MyOrderEvent;

        public event Action<MyTrade> MyTradeEvent;

        public event Action<MarketDepth> MarketDepthEvent;

        public event Action<Trade> NewTradesEvent;

        #endregion

        #region 11 Trade

        private RateGate _rateGateSendOrder = new RateGate(1, TimeSpan.FromMilliseconds(200));

        private RateGate _rateGateCancelOrder = new RateGate(1, TimeSpan.FromMilliseconds(200));

        public void SendOrder(Order order)
        {
            try
            {
                _rateGateSendOrder.WaitToProceed();

                Dictionary<string, dynamic> jsonContent = new Dictionary<string, dynamic>();

                jsonContent.Add("symbol", order.SecurityNameCode);
                jsonContent.Add("side", order.Side.ToString().ToLower());               
                jsonContent.Add("orderType", order.TypeOrder.ToString().ToLower());
                jsonContent.Add("price", order.Price.ToString().Replace(",", "."));
                jsonContent.Add("size", order.Volume.ToString().Replace(",", "."));
                jsonContent.Add("clientOid", order.NumberUser);
                jsonContent.Add("force", "gtc");

                string jsonRequest = JsonConvert.SerializeObject(jsonContent);

                HttpResponseMessage responseMessage = CreatePrivateQueryOrders("/api/v2/spot/trade/place-order", Method.POST.ToString(), null, jsonRequest);
                string JsonResponse = responseMessage.Content.ReadAsStringAsync().Result;

                ResponseRestMessage<object> stateResponse = JsonConvert.DeserializeAnonymousType(JsonResponse, new ResponseRestMessage<object>());

                if (responseMessage.StatusCode == HttpStatusCode.OK)
                {
                    if (stateResponse.code.Equals("00000") == true)
                    {
                        // ignore
                    }
                    else
                    {
                        CreateOrderFail(order);
                        SendLogMessage($"Code: {stateResponse.code}\n"
                            + $"Message: {stateResponse.msg}", LogMessageType.Error);
                    }
                }
                else
                {
                    CreateOrderFail(order);
                    SendLogMessage($"Http State Code: {responseMessage.StatusCode}", LogMessageType.Error);

                    if (stateResponse != null && stateResponse.code != null)
                    {
                        SendLogMessage($"Code: {stateResponse.code}\n"
                            + $"Message: {stateResponse.msg}", LogMessageType.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                SendLogMessage(ex.Message, LogMessageType.Error);
            }
        }

        public void CancelOrder(Order order)
        {
            try
            {
                _rateGateCancelOrder.WaitToProceed();

                Dictionary<string, string> jsonContent = new Dictionary<string, string>();

                jsonContent.Add("symbol", order.SecurityNameCode);
                jsonContent.Add("orderId", order.NumberMarket);

                string jsonRequest = JsonConvert.SerializeObject(jsonContent);

                HttpResponseMessage response = CreatePrivateQueryOrders("/api/v2/spot/trade/cancel-order", Method.POST.ToString(), null, jsonRequest);
                string JsonResponse = response.Content.ReadAsStringAsync().Result;

                ResponseRestMessage<object> stateResponse = JsonConvert.DeserializeAnonymousType(JsonResponse, new ResponseRestMessage<object>());

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    if (stateResponse.code.Equals("00000") == true)
                    {
                        // ignore
                    }
                    else
                    {
                        CreateOrderFail(order);
                        SendLogMessage($"Code: {stateResponse.code}\n"
                            + $"Message: {stateResponse.msg}", LogMessageType.Error);
                    }
                }
                else
                {
                    CreateOrderFail(order);
                    SendLogMessage($"Http State Code: {response.StatusCode}", LogMessageType.Error);

                    if (stateResponse != null && stateResponse.code != null)
                    {
                        SendLogMessage($"Code: {stateResponse.code}\n"
                            + $"Message: {stateResponse.msg}", LogMessageType.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                SendLogMessage(ex.Message, LogMessageType.Error);
            }
        }

        public void CancelAllOrdersToSecurity(Security security)
        {
            try
            {
                _rateGateCancelOrder.WaitToProceed();

                Dictionary<string, string> jsonContent = new Dictionary<string, string>();

                jsonContent.Add("symbol", security.Name);

                string jsonRequest = JsonConvert.SerializeObject(jsonContent);

                CreatePrivateQueryOrders("/api/v2/spot/trade/cancel-symbol-order", Method.POST.ToString(), null, jsonRequest);
            }
            catch (Exception e)
            {
                SendLogMessage(e.Message, LogMessageType.Error);
            }
        }

        public void CancelAllOrders()
        {
        }

        public void ChangeOrderPrice(Order order, decimal newPrice)
        {
        }

        public void GetAllActivOrders()
        {
            List<Order> orders = GetAllOpenOrders();

            if (orders == null)
            {
                return;
            }

            for (int i = 0; i < orders.Count; i++)
            {
                if (MyOrderEvent != null)
                {
                    MyOrderEvent(orders[i]);
                }
            }
        }

        public void GetOrderStatus(Order order)
        {
            try
            {
                IRestResponse responseMessage = CreatePrivateQuery($"/api/v2/spot/trade/orderInfo?clientOid={order.NumberUser}", Method.GET, null, null);
                string json = responseMessage.Content;

                ResponseRestMessage<List<RestMessageOrders>> stateResponse = JsonConvert.DeserializeAnonymousType(json, new ResponseRestMessage<List<RestMessageOrders>>());

                if (responseMessage.StatusCode == HttpStatusCode.OK)
                {
                    if (stateResponse.code.Equals("00000") == true)
                    {
                        for (int i = 0; i < stateResponse.data.Count; i++)
                        {
                            int numberUser;
                            int.TryParse(stateResponse.data[i].clientOid, out numberUser);

                            if (numberUser == order.NumberUser)
                            {
                                Order newOrder = new Order();

                                newOrder.SecurityNameCode = stateResponse.data[i].symbol;
                                newOrder.SecurityClassCode = "Spot";
                                newOrder.TimeCallBack = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(stateResponse.data[i].cTime));
                                newOrder.NumberUser = numberUser;
                                newOrder.NumberMarket = stateResponse.data[i].orderId;
                                newOrder.Side = stateResponse.data[i].side == "buy" ? Side.Buy : Side.Sell; ;
                                newOrder.State = GetOrderState(stateResponse.data[i].status);
                                newOrder.Volume = stateResponse.data[i].size.ToDecimal();
                                newOrder.Price = stateResponse.data[i].price.ToDecimal();
                                newOrder.ServerType = ServerType.BitGetSpot;
                                newOrder.PortfolioNumber = "BitGetSpot";
                                newOrder.TypeOrder = OrderPriceType.Limit;

                                if (newOrder != null
                                    && MyOrderEvent != null)
                                {
                                    MyOrderEvent(newOrder);
                                }

                                if (newOrder.State == OrderStateType.Done ||
                                    newOrder.State == OrderStateType.Partial)
                                {
                                    FindMyTradesToOrder(newOrder);
                                }
                            }
                        }
                    }
                }
                   
            }
            catch (Exception ex)
            {
                SendLogMessage(ex.Message, LogMessageType.Error);
            }
        }

        private void FindMyTradesToOrder(Order order)
        {
            try
            {
                string path = $"/api/v2/spot/trade/fills?symbol={order.SecurityNameCode}";

                IRestResponse responseMessage = CreatePrivateQuery(path, Method.GET, null, null);
                string json = responseMessage.Content;

                RestMyTradesResponce stateResponse = JsonConvert.DeserializeAnonymousType(json, new RestMyTradesResponce());

                if (responseMessage.StatusCode == HttpStatusCode.OK)
                {
                    if (stateResponse.code.Equals("00000") == true)
                    {
                        for (int i = 0; i < stateResponse.data.Count; i++)
                        {
                            if (stateResponse.data[i].orderId != order.NumberMarket)
                            {
                                continue;
                            }
                            DataMyTrades item = stateResponse.data[i];

                            MyTrade myTrade = new MyTrade();
                            myTrade.Time = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(item.cTime));
                            myTrade.NumberOrderParent = item.orderId.ToString();
                            myTrade.NumberTrade = item.tradeId;                            
                            myTrade.Price = item.priceAvg.ToDecimal();
                            myTrade.SecurityNameCode = item.symbol.ToUpper();
                            myTrade.Side = item.side == "buy" ? Side.Buy : Side.Sell;

                            myTrade.Volume = item.size.ToDecimal();

                            if (string.IsNullOrEmpty(item.feeDetail.feeCoin) == false
                            && string.IsNullOrEmpty(item.feeDetail.totalFee) == false
                            && item.feeDetail.totalFee.ToDecimal() != 0)
                            {
                                if (myTrade.SecurityNameCode.StartsWith(item.feeDetail.feeCoin))
                                {
                                    myTrade.Volume = Math.Round(item.size.ToDecimal() + item.feeDetail.totalFee.ToDecimal(), 6, MidpointRounding.AwayFromZero);
                                }
                                else
                                {
                                    myTrade.Volume = item.size.ToDecimal();
                                }
                            }
                            else
                            {
                                myTrade.Volume = item.size.ToDecimal();
                            }

                            MyTradeEvent(myTrade);
                        }
                    }
                    else
                    {
                        SendLogMessage($"Code: {stateResponse.code}\n"
                            + $"Message: {stateResponse.msg}", LogMessageType.Error);
                    }
                }
            }
            catch (Exception e)
            {
                SendLogMessage(e.Message, LogMessageType.Error);
            }
        }
               
        public List<Order> GetAllOpenOrders()
        {            
            try
            {               
                IRestResponse responseMessage = CreatePrivateQuery($"/api/v2/spot/trade/unfilled-orders", Method.GET, null, null);
                
                string json = responseMessage.Content;

                ResponseRestMessage<List<RestMessageOrders>> stateResponse = JsonConvert.DeserializeAnonymousType(json, new ResponseRestMessage<List<RestMessageOrders>>());

                if (responseMessage.StatusCode == HttpStatusCode.OK)
                {
                    if (stateResponse.code.Equals("00000") == true)
                    {
                        if (stateResponse.data == null)
                        {
                            return null;
                        }

                        List<Order> orders = new List<Order>();

                        for (int ind = 0; ind < stateResponse.data.Count; ind++)
                        {
                            Order curOder = ConvertRestToOrder(stateResponse.data[ind]);
                            orders.Add(curOder);
                        }

                        return orders;
                    }
                    else
                    {
                        SendLogMessage($"Code: {stateResponse.code}\n"
                            + $"Message: {stateResponse.msg}", LogMessageType.Error);
                    }                    
                }
                return null;
            }
            catch (Exception e)
            {
                SendLogMessage(e.Message, LogMessageType.Error);
                return null;
            }
        }

        private Order ConvertRestToOrder(RestMessageOrders item)
        {
            Order newOrder = new Order();

            OrderStateType stateType = GetOrderState(item.status);

            newOrder.SecurityNameCode = item.symbol;
            newOrder.SecurityClassCode = "Spot";
            newOrder.TimeCallBack = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(item.cTime));
            int.TryParse(item.clientOid, out newOrder.NumberUser);
            newOrder.NumberMarket = item.orderId.ToString();
            newOrder.Side = item.side == "buy" ? Side.Buy : Side.Sell;
            newOrder.State = stateType;
            newOrder.Volume = item.size.ToDecimal();
            newOrder.Price = item.priceAvg.ToDecimal();
            newOrder.ServerType = ServerType.BitGetSpot;
            newOrder.PortfolioNumber = "BitGetSpot";
            newOrder.TypeOrder = OrderPriceType.Limit;

            return newOrder;
        }

        private OrderStateType GetOrderState(string orderStateResponse)
        {
            OrderStateType stateType;

            switch (orderStateResponse)
            {
                case ("live"):
                    stateType = OrderStateType.Active;
                    break;
                case ("partially_filled"):
                    stateType = OrderStateType.Partial;
                    break;
                case ("filled"):
                    stateType = OrderStateType.Done;
                    break;
                case ("cancelled"):
                    stateType = OrderStateType.Cancel;
                    break;
                default:
                    stateType = OrderStateType.None;
                    break;
            }

            return stateType;
        }

        private void CreateOrderFail(Order order)
        {
            order.State = OrderStateType.Fail;

            if (MyOrderEvent != null)
            {
                MyOrderEvent(order);
            }
        }

        #endregion

        #region 12 Queries

        private IRestResponse CreatePrivateQuery(string path, Method method, string queryString, string body)
        {
            try
            {
                RestRequest requestRest = new RestRequest(path, method);

                string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                string signature = GenerateSignature(timestamp, method.ToString(), path, queryString, body, SeckretKey);

                requestRest.AddHeader("ACCESS-KEY", PublicKey);
                requestRest.AddHeader("ACCESS-SIGN", signature);
                requestRest.AddHeader("ACCESS-TIMESTAMP", timestamp);
                requestRest.AddHeader("ACCESS-PASSPHRASE", Passphrase);
                requestRest.AddHeader("X-CHANNEL-API-CODE", "6yq7w");

                RestClient client = new RestClient(BaseUrl);

                IRestResponse response = client.Execute(requestRest);

                return response;
            }
            catch (Exception ex)
            {
                SendLogMessage(ex.Message, LogMessageType.Error);
                return null;
            }
        }

        private HttpClient _httpClient = new HttpClient();

        private HttpResponseMessage CreatePrivateQueryOrders(string path, string method, string queryString, string body)
        {
            try
            {
                string requestPath = path;
                string url = $"{BaseUrl}{requestPath}";
                string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                string signature = GenerateSignature(timestamp, method, requestPath, queryString, body, SeckretKey);

                _httpClient.DefaultRequestHeaders.Add("ACCESS-KEY", PublicKey);
                _httpClient.DefaultRequestHeaders.Add("ACCESS-SIGN", signature);
                _httpClient.DefaultRequestHeaders.Add("ACCESS-TIMESTAMP", timestamp);
                _httpClient.DefaultRequestHeaders.Add("ACCESS-PASSPHRASE", Passphrase);
                _httpClient.DefaultRequestHeaders.Add("X-CHANNEL-API-CODE", "6yq7w");

                if (method.Equals("POST"))
                {
                    return _httpClient.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json")).Result;
                }
                else
                {
                    return _httpClient.GetAsync(url).Result;
                }
            }
            catch (Exception ex)
            {
                SendLogMessage(ex.Message, LogMessageType.Error);
                return null;
            }
        }

        private string GenerateSignature(string timestamp, string method, string requestPath, string queryString, string body, string secretKey)
        {
            method = method.ToUpper();
            body = string.IsNullOrEmpty(body) ? string.Empty : body;
            queryString = string.IsNullOrEmpty(queryString) ? string.Empty : "?" + queryString;

            string preHash = timestamp + method + requestPath + queryString + body;

            byte[] secretKeyBytes = Encoding.UTF8.GetBytes(secretKey);
            using (HMACSHA256 hmac = new HMACSHA256(secretKeyBytes))
            {
                byte[] hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(preHash));
                return Convert.ToBase64String(hashBytes);
            }
        }
       
        #endregion

        #region 13 Log

        private void SendLogMessage(string message, LogMessageType messageType)
        {
            LogMessageEvent(message, messageType);
        }

        public event Action<string, LogMessageType> LogMessageEvent;

        #endregion
    }
}