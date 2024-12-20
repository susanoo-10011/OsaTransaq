﻿/*
 *Your rights to use the code are governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 *Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.Logging;
using OsEngine.Market.Servers.Entity;
using OsEngine.Market.Servers.MoexAlgopack.Entity;
using OsEngine.Market.Servers.Transaq.TransaqEntity;
using RestSharp;
using RestSharp.Deserializers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Xml;
using System.Xml.Serialization;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.TrackBar;
using Candle = OsEngine.Entity.Candle;
using Order = OsEngine.Entity.Order;
using Security = OsEngine.Entity.Security;
using Trade = OsEngine.Entity.Trade;

namespace OsEngine.Market.Servers.Transaq
{
    public class TransaqServer : AServer
    {
        public TransaqServer()
        {
            ServerRealization = new TransaqServerRealization();

            CreateParameterString(OsLocalization.Market.Message63, "");
            CreateParameterPassword(OsLocalization.Market.Message64, "");
            CreateParameterString(OsLocalization.Market.Label41, "tr1.finam.ru");
            CreateParameterString(OsLocalization.Market.Message90, "3900");
            CreateParameterBoolean(OsLocalization.Market.UseStock, true);
            CreateParameterBoolean(OsLocalization.Market.UseFutures, true);
            CreateParameterBoolean(OsLocalization.Market.UseCurrency, true);
            CreateParameterBoolean(OsLocalization.Market.UseOptions, false);
            CreateParameterBoolean(OsLocalization.Market.UseOther, false);
            CreateParameterButton(OsLocalization.Market.ButtonNameChangePassword);

            ServerParameters[4].Comment = OsLocalization.Market.Label107;
            ServerParameters[5].Comment = OsLocalization.Market.Label107;
            ServerParameters[6].Comment = OsLocalization.Market.Label107;
            ServerParameters[7].Comment = OsLocalization.Market.Label107;
            ServerParameters[8].Comment = OsLocalization.Market.Label107;
            ServerParameters[9].Comment = OsLocalization.Market.Label108;
            ServerParameters[10].Comment = OsLocalization.Market.Label105;

        }

        public void GetCandleHistory(CandleSeries series)
        {
            ((TransaqServerRealization)ServerRealization).GetCandleHistory(series);
        }
    }

    public class TransaqServerRealization : IServerRealization
    {
        #region 1 Constructor, Status, Connection

        delegate bool CallBackDelegate(IntPtr pData);
        private CallBackDelegate _myCallbackDelegate;

        public TransaqServerRealization()
        {
            ServerStatus = ServerConnectStatus.Disconnect;

            _deserializer = new XmlDeserializer();
            _logPath = AppDomain.CurrentDomain.BaseDirectory + @"Engine\TransaqLog";
            DirectoryInfo dirInfo = new DirectoryInfo(_logPath);

             _myCallbackDelegate = new CallBackDelegate(CallBackDataHandler);
            SetCallback(_myCallbackDelegate);

            if (!dirInfo.Exists)
            {
                dirInfo.Create();
            }

            Thread worker = new Thread(CycleGettingPortfolios);
            worker.Name = "ThreadTransaqGetPortfolio";
            worker.Start();

            Thread worker2 = new Thread(ThreadDataParsingWorkPlace);
            worker2.Name = "ThreadTransaqDataParsing";
            worker2.Start();

            Thread worker3 = new Thread(ThreadTradesParsingWorkPlace);
            worker3.Name = "TransaqThreadTradesParsing";
            worker3.Start();

            Thread worker4 = new Thread(ThreadMarketDepthsParsingWorkPlace);
            worker4.Name = "TransaqThreadDepthsParsing";
            worker4.Start();

            Thread worker5 = new Thread(Converter);
            worker5.Name = "TransaqThreadConverter";
            worker5.Start();
        }

        public ServerType ServerType
        {
            get { return ServerType.Transaq; }
        }

        public List<IServerParameter> ServerParameters { get; set; }

        public DateTime ServerTime { get; set; }

        public ServerConnectStatus ServerStatus { get; set; }

        private CancellationTokenSource _cancellationTokenSource;

        private CancellationToken _cancellationToken;

        public void Connect()
        {
            string login = ((ServerParameterString)ServerParameters[0]).Value;
            string password = ((ServerParameterPassword)ServerParameters[1]).Value;
            string serverIp = ((ServerParameterString)ServerParameters[2]).Value;
            string serverPort = ((ServerParameterString)ServerParameters[3]).Value;

            _useStock = ((ServerParameterBool)ServerParameters[4]).Value;
            _useFutures = ((ServerParameterBool)ServerParameters[5]).Value;
            _useCurrency = ((ServerParameterBool)ServerParameters[6]).Value;
            _useOptions = ((ServerParameterBool)ServerParameters[7]).Value;
            _useOther = ((ServerParameterBool)ServerParameters[8]).Value;
            ServerParameterButton btn = ((ServerParameterButton)ServerParameters[9]);

            btn.UserClickButton += () => { ButtonClickChangePasswordWindowShow(); };

            try
            {
                ConnectorInitialize();

                // formation of the command text / формирование текста команды
                string cmd = "<command id=\"connect\">";
                cmd = cmd + "<login>" + login + "</login>";
                cmd = cmd + "<password>" + password + "</password>";
                cmd = cmd + "<host>" + serverIp + "</host>";
                cmd = cmd + "<port>" + serverPort + "</port>";
                cmd = cmd + "<push_pos_equity>" + 3 + "</push_pos_equity>";
                cmd = cmd + "<rqdelay>100</rqdelay>";
                cmd = cmd + "</command>";

                // sending the command / отправка команды
                ConnectorSendCommand(cmd);
            }
            catch (Exception e)
            {
                SendLogMessage(e.ToString(), LogMessageType.Error);
            }

            //_client.Connected += _client_Connected;
            //_client.Disconnected += _client_Disconnected;
            //_client.LogMessageEvent += SendLogMessage;

            _cancellationTokenSource = new CancellationTokenSource();

            _cancellationToken = _cancellationTokenSource.Token;

        }

        /// <summary>
        /// Initializes the library: starts the callback queue processing thread
        /// Выполняет инициализацию библиотеки: запускает поток обработки очереди обратных вызовов
        /// </summary>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptionsAttribute]
        public bool ConnectorInitialize()
        {
            try
            {
                IntPtr pResult = Initialize(MarshalUtf8.StringToHGlobalUtf8(_logPath), 1);

                if (!pResult.Equals(IntPtr.Zero))
                {
                    MarshalUtf8.PtrToStringUtf8(pResult);

                    FreeMemory(pResult);

                    return false;
                }
                else
                {
                    FreeMemory(pResult);
                    return true;
                }
            }
            catch (Exception e)
            {
                SendLogMessage(e.ToString(), LogMessageType.Error);
                return false;
            }
        }

        /// <summary>
        /// disconnect to exchange
        /// разорвать соединение с биржей 
        /// </summary>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptionsAttribute]
        public void Disconnect()
        {
            try
            {
                // formation of the command text / формирование текста команды
                string cmd = "<command id=\"disconnect\">";
                cmd = cmd + "</command>";

                // sending the command / отправка команды
                ConnectorSendCommand(cmd);
            }
            catch (Exception e)
            {
                SendLogMessage(e.ToString(), LogMessageType.Error);
            }
        }

        public void Dispose()
        {
            if (ServerStatus != ServerConnectStatus.Disconnect)
            {
                ServerStatus = ServerConnectStatus.Disconnect;
                DisconnectEvent();
            }

            try
            {
                Disconnect();
                Thread.Sleep(2000);
                bool res = ConnectorUnInitialize();

                if (res)
                {
                    _client_Disconnected();
                }
            }
            catch (Exception e)
            {
                SendLogMessage(e.ToString(), LogMessageType.Error);
            }

            _depths?.Clear();

            _depths = null;

            _allCandleSeries?.Clear();

            _cancellationTokenSource?.Cancel();

            _transaqSecuritiesInString = new ConcurrentQueue<string>();

            _securities = new List<Security>();

            _subscribeSecurities = new List<Security>();

        }

        /// <summary>
        /// Shuts down the internal threads of the library, including completing thread queue callbacks
        /// Выполняет остановку внутренних потоков библиотеки, в том числе завершает поток обработки очереди обратных вызовов
        /// </summary>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptionsAttribute]
        public bool ConnectorUnInitialize()
        {
            try
            {
                IntPtr pResult = UnInitialize();

                if (!pResult.Equals(IntPtr.Zero))
                {
                    MarshalUtf8.PtrToStringUtf8(pResult);
                    FreeMemory(pResult);
                    return false;
                }
                else
                {
                    FreeMemory(pResult);
                    return true;
                }
            }
            catch (Exception e)
            {
                SendLogMessage(e.ToString(), LogMessageType.Error);
                return false;
            }
        }

        public event Action ConnectEvent;

        public event Action DisconnectEvent;

        #endregion

        #region 2 Properties

        private readonly string _logPath;

        private bool _useStock = false;

        private bool _useFutures = false;

        private bool _useOptions = false;

        private bool _useCurrency = false;

        private bool _useOther = false;


        private readonly XmlDeserializer _deserializer;

        #endregion

        #region 3 Client to Transaq

        //private TransaqClient _client;

        void _client_Connected()
        {
            SendLogMessage("Transaq client activated ", LogMessageType.System);

            CreateSecurities();

            if (ServerStatus != ServerConnectStatus.Connect)
            {
                ServerStatus = ServerConnectStatus.Connect;
                ConnectEvent?.Invoke();
            }
        }

        void _client_Disconnected()
        {
            SendLogMessage("Transaq client disconnected ", LogMessageType.System);

            if (ServerStatus != ServerConnectStatus.Disconnect)
            {
                ServerStatus = ServerConnectStatus.Disconnect;
                DisconnectEvent?.Invoke();
            }
        }

        private void _client_NeedChangePassword()
        {
            Application.Current.Dispatcher.Invoke((Action)delegate {
                string message = OsLocalization.Market.Message94;

                ChangeTransaqPassword changeTransaqPasswordWindow = new ChangeTransaqPassword(message, this);
                changeTransaqPasswordWindow.ShowDialog();
            });
        }

        private void ButtonClickChangePasswordWindowShow()
        {
            ChangeTransaqPassword changeTransaqPassword = new ChangeTransaqPassword(this);
            changeTransaqPassword.ShowDialog();
        }

        public void ChangePassword(string oldPassword, string newPassword, ChangeTransaqPassword window)
        {
            try
            {
                if (ServerStatus == ServerConnectStatus.Disconnect)
                {
                    window.TextInfo.Text = OsLocalization.Market.Label102;
                    return;
                }

                string cmd = $"<command id=\"change_pass\" oldpass=\"{oldPassword}\" newpass=\"{newPassword}\"/>";

                // sending command / отправка команды
                string res = ConnectorSendCommand(cmd);

                if (res == "<result success=\"true\"/>")
                {
                    ((ServerParameterPassword)ServerParameters[1]).Value = newPassword;
                    window.TextInfo.Text = OsLocalization.Market.Label103;
                }
                else
                {
                    window.TextInfo.Text = res;
                }

                Dispose();
            }
            catch (Exception ex)
            {
                window.TextInfo.Text = ex.ToString();
            }
        }

        #endregion

        #region 4 Securities

        public void GetSecurities() { }

        private List<Security> _securities = new List<Security>();

        private ConcurrentQueue<string> _transaqSecuritiesInString = new ConcurrentQueue<string>();

        private List<TransaqEntity.Security> _transaqSecurities = new List<TransaqEntity.Security>();

        private List<SecurityInfo> _secsSpecification = new List<SecurityInfo>();

        private void _client_UpdateSecurity(SecurityInfo secs)
        {
            _lastUpdateSecurityArrayTime = DateTime.Now;

            bool isInArray = false;

            for (int i = 0; i < _secsSpecification.Count; i++)
            {
                if (_secsSpecification[i].Secid == secs.Secid)
                {
                    _secsSpecification[i] = secs;
                    isInArray = true;
                    break;
                }
            }

            if (isInArray == false)
            {
                _secsSpecification.Add(secs);
            }

            if (_securities == null ||
                _securities.Count == 0)
            {
                return;
            }

            if (_secsSpecification != null
                && _secsSpecification.Count != 0)
            {
                for (int i = 0; i < _secsSpecification.Count; i++)
                {
                    SecurityInfo secInfo = _secsSpecification[i];

                    for (int j = 0; j < _securities.Count; j++)
                    {
                        Security secCur = _securities[j];

                        if (secCur.NameId == secInfo.Secid)
                        {
                            if (string.IsNullOrEmpty(secInfo.Maxprice) == false)
                            {
                                secCur.PriceLimitHigh = secInfo.Maxprice.ToDecimal();
                            }

                            if (string.IsNullOrEmpty(secInfo.Minprice) == false)
                            {
                                secCur.PriceLimitLow = secInfo.Minprice.ToDecimal();
                            }

                            if (string.IsNullOrEmpty(secInfo.Buy_deposit) == false)
                            {
                                secCur.Go = secInfo.Buy_deposit.ToDecimal();
                            }

                            break;
                        }
                    }
                }
            }
        }

        private void _client_ClientOnUpdatePairs(string securities)
        {
            
        }

        private DateTime _lastUpdateSecurityArrayTime;

        private void CreateSecurities()
        {

            while (true)
            {
                Thread.Sleep(500);
                if (_lastUpdateSecurityArrayTime == DateTime.MinValue)
                {
                    continue;
                }
                if (_lastUpdateSecurityArrayTime.AddSeconds(3) > DateTime.Now)
                {
                    continue;
                }

                break;
            }

            DateTime timeStart = DateTime.Now;

            while (_transaqSecuritiesInString.IsEmpty == false)
            {
                string curArray = null;

                if (_transaqSecuritiesInString.TryDequeue(out curArray))
                {
                    CreateSecurities(curArray);
                }
            }

            _securities.RemoveAll(s => s == null);

            if (_securities.Count == 0)
            {
                return;
            }

            if (_secsSpecification != null)
            {
                for (int i = 0; i < _secsSpecification.Count; i++)
                {
                    SecurityInfo secInfo = _secsSpecification[i];

                    for (int j = 0; j < _securities.Count; j++)
                    {
                        Security secCur = _securities[j];

                        if (secCur.NameId == secInfo.Secid)
                        {
                            if (string.IsNullOrEmpty(secInfo.Maxprice) == false)
                            {
                                secCur.PriceLimitHigh = secInfo.Maxprice.ToDecimal();
                            }

                            if (string.IsNullOrEmpty(secInfo.Minprice) == false)
                            {
                                secCur.PriceLimitLow = secInfo.Minprice.ToDecimal();
                            }

                            if (string.IsNullOrEmpty(secInfo.Buy_deposit) == false)
                            {
                                secCur.Go = secInfo.Buy_deposit.ToDecimal();
                            }

                            break;
                        }
                    }
                }
            }

            SecurityEvent?.Invoke(_securities);

            TimeSpan timeOnWork = DateTime.Now - timeStart;

            SendLogMessage("Time securities add: " + timeOnWork.ToString(), LogMessageType.System);
            SendLogMessage("Securities count: " + _securities.Count, LogMessageType.System);
        }

        private void CreateSecurities(string data)
        {
            List<TransaqEntity.Security> transaqSecurities = _deserializer.Deserialize<List<TransaqEntity.Security>>(new RestResponse() { Content = data }); ;

            foreach (TransaqEntity.Security securityData in transaqSecurities)
            {
                try
                {
                    if (!CheckFilter(securityData))
                    {
                        continue;
                    }

                    bool isInArray = false;

                    for (int i = 0; i < _transaqSecurities.Count; i++)
                    {
                        if (_transaqSecurities[i].Secid == securityData.Secid)
                        {
                            isInArray = true;
                            break;
                        }
                    }

                    if (isInArray == false)
                    {
                        _transaqSecurities.Add(securityData);
                    }

                    Security security = new Security();

                    security.Name = securityData.Seccode;
                    security.NameFull = securityData.Shortname;
                    security.NameClass = securityData.Board;
                    security.NameId = securityData.Secid;
                    security.Decimals = Convert.ToInt32(securityData.Decimals);
                    security.Exchange = securityData.Board;

                    if (securityData.Sectype == "FUT")
                    {
                        security.SecurityType = SecurityType.Futures;
                    }
                    else if (securityData.Sectype == "SHARE")
                    {
                        security.SecurityType = SecurityType.Stock;
                    }
                    else if (securityData.Sectype == "OPT")
                    {
                        security.SecurityType = SecurityType.Option;
                    }
                    else if (securityData.Sectype == "BOND")
                    {
                        security.SecurityType = SecurityType.Bond;
                    }
                    else if (securityData.Sectype == "CURRENCY"
                        || securityData.Sectype == "CETS"
                        || security.NameClass == "CETS")
                    {
                        security.SecurityType = SecurityType.CurrencyPair;
                    }
                    else if (securityData.Sectype == "FUND")
                    {
                        security.SecurityType = SecurityType.Fund;
                    }
                    else if (security.NameClass == "MCT"
                       && (security.NameFull.Contains("call") || security.NameFull.Contains("put")))
                    {
                        security.NameClass = "MCT_put_call";
                        security.SecurityType = SecurityType.Option;
                    }
                    else if (security.NameClass == "MCT")
                    {
                        security.SecurityType = SecurityType.Futures;
                    }
                    else if (security.NameClass == "QUOTES")
                    {
                        // ignore
                    }
                    else if (security.NameClass == "DVP")
                    {
                        // ignore
                    }
                    else if (security.NameClass == "INDEXM"
                        || security.NameClass == "INDEXE"
                        || security.NameClass == "INDEXR")
                    {
                        security.SecurityType = SecurityType.Index;
                    }
                    else
                    {
                        //security.NameClass = securityData.Sectype;
                    }

                    if (security.SecurityType == SecurityType.None)
                    {
                        security.SecurityType = SecurityType.Bond;
                    }

                    security.Lot = securityData.Lotsize.ToDecimal();

                    if (security.Lot == 0)
                    {
                        security.Lot = 1;
                    }

                    security.PriceStep = securityData.Minstep.ToDecimal();

                    if (security.PriceStep == 0)
                    {
                        continue;
                    }

                    decimal pointCost;
                    try
                    {
                        pointCost = securityData.Point_cost.ToDecimal();
                    }
                    catch (Exception e)
                    {
                        decimal.TryParse(securityData.Point_cost, NumberStyles.Float, CultureInfo.InvariantCulture, out pointCost);
                    }

                    if (security.SecurityType == SecurityType.Futures
                    || security.SecurityType == SecurityType.Option)
                    {
                        if (security.PriceStep > 1)
                        {
                            security.PriceStepCost = security.PriceStep * pointCost / 100;
                        }
                        else
                        {
                            security.PriceStepCost = pointCost / 100;
                        }
                    }
                    else
                    {
                        security.PriceStepCost = security.PriceStep;
                    }

                    security.State = securityData.Active == "true" ? SecurityStateType.Activ : SecurityStateType.Close;

                    if (security.State != SecurityStateType.Activ)
                    {
                        continue;
                    }

                    if (_securities.Contains(security))
                    {
                        continue;
                    }

                    _securities.Add(security);
                }
                catch (Exception e)
                {
                    SendLogMessage(e.Message, LogMessageType.Error);
                }
            }
        }

        private string _locker = "filterLocker";

        private bool CheckFilter(TransaqEntity.Security security)
        {
            lock (_locker)
            {
                if (security.Sectype == "SHARE")
                {
                    if (_useStock)
                    {
                        return true;
                    }
                    return false;
                }
                if (security.Sectype == "FUT")
                {
                    if (_useFutures)
                    {
                        return true;
                    }
                    return false;
                }
                if (security.Sectype == "OPT")
                {
                    if (_useOptions)
                    {
                        return true;
                    }
                    return false;
                }
                if (security.Sectype == "CETS" || security.Sectype == "CURRENCY")
                {
                    if (_useCurrency)
                    {
                        return true;
                    }
                    return false;
                }
                if (_useOther)
                {
                    return true;
                }

                return false;
            }
        }

        public event Action<List<Security>> SecurityEvent;

        #endregion

        #region 5 Portfolios

        public void GetPortfolios()
        {

        }

        private List<Portfolio> _portfolios;

        private void CycleGettingPortfolios()
        {
            while (true)
            {
                try
                {
                    Thread.Sleep(3000);

                    if (ServerStatus == ServerConnectStatus.Disconnect)
                    {
                        continue;
                    }

                    if (_clients == null || _clients.Count == 0)
                    {
                        continue;
                    }

                    for (int i = 0; i < _clients.Count; i++)
                    {
                        if (ServerStatus == ServerConnectStatus.Disconnect)
                        {
                            break;
                        }

                        Client client = _clients[i];

                        string command;

                        if (client.Type == "mct")
                        {
                            command = $"<command id=\"get_portfolio_mct\" client=\"{client.Id}\"/>";

                            string res = ConnectorSendCommand(command);

                            if (res != "<result success=\"true\"/>")
                            {
                                // whait
                                Thread.Sleep(5000);
                            }
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(client.Union))
                            {
                                command = $"<command id=\"get_mc_portfolio\" union=\"{client.Union}\" />";
                            }
                            else
                            {
                                command = $"<command id=\"get_mc_portfolio\" client=\"{client.Id}\" />";
                            }

                            string res = ConnectorSendCommand(command);

                            if (res != "<result success=\"true\"/>")
                            {
                                // whait
                                Thread.Sleep(5000);
                            }
                        }

                        if (string.IsNullOrEmpty(client.Union) && !string.IsNullOrEmpty(client.Forts_acc))
                        {
                            command = $"<command id=\"get_client_limits\" client=\"{client.Id}\"/>";
                            string res = ConnectorSendCommand(command);

                            if (res != "<result success=\"true\"/>")
                            {
                                // whait
                                Thread.Sleep(5000);
                            }
                        }
                    }
                }
                catch (Exception error)
                {
                    SendLogMessage(error.ToString(), LogMessageType.Error);
                    Thread.Sleep(5000);
                }
            }
        }

        private List<Client> _clients;

        private void _client_ClientsInfoUpdate(Client clientInfo)
        {
            try
            {
                if (_clients == null)
                {
                    _clients = new List<Client>();
                }

                if (!string.IsNullOrEmpty(clientInfo.Union))
                {
                    var needClient = _clients.Find(c => string.IsNullOrEmpty(clientInfo.Union));

                    if (needClient == null)
                    {
                        _clients.Add(clientInfo);
                    }
                }
                else
                {
                    _clients.Add(clientInfo);
                }
            }
            catch (Exception error)
            {
                SendLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void _client_ClientOnUpdatePortfolio(string portfolio)
        {
            try
            {
                if (_portfolios == null)
                {
                    _portfolios = new List<Portfolio>();
                }

                Portfolio unitedPortfolio = ParsePortfolio(portfolio);

                Portfolio needPortfolio = _portfolios.Find(p => p.Number == unitedPortfolio.Number);

                if (needPortfolio != null)
                {
                    _portfolios.Remove(needPortfolio);
                }
                _portfolios.Add(unitedPortfolio);

                PortfolioEvent?.Invoke(_portfolios);
            }
            catch (Exception error)
            {
                SendLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private Portfolio ParsePortfolio(string data)
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(data);

            XmlElement root = doc.DocumentElement;

            if (root == null)
            {
                return null;
            }

            var union = root.GetAttribute("union");
            var client = root.GetAttribute("client");

            var openEquity = root.SelectSingleNode("open_equity");
            var equity = root.SelectSingleNode("equity");
            var block = root.SelectSingleNode("go");
            var cover = root.SelectSingleNode("cover");

            var allSecurity = root.GetElementsByTagName("security");

            var portfolio = new Portfolio();

            if (openEquity != null)
            {
                portfolio.ValueBegin = openEquity.InnerText.ToDecimal();
            }
            if (equity != null)
            {
                portfolio.ValueCurrent = equity.InnerText.ToDecimal();
            }
            if (equity != null
                && cover != null
                && block != null)
            {
                portfolio.ValueBlocked =
                    (equity.InnerText.ToDecimal()
                    - cover.InnerText.ToDecimal())
                    + block.InnerText.ToDecimal();
            }

            if (!string.IsNullOrEmpty(union))
            {
                portfolio.Number = "United_" + union;
            }
            else
            {
                portfolio.Number = client;
            }

            foreach (var security in allSecurity)
            {
                var node = (XmlNode)security;

                var pos = new PositionOnBoard();

                pos.SecurityNameCode = node.SelectSingleNode("seccode")?.InnerText;
                pos.PortfolioName = portfolio.Number;

                XmlNode beginNode = node.SelectSingleNode("open_balance");
                XmlNode buyNode = node.SelectSingleNode("bought");
                XmlNode sellNode = node.SelectSingleNode("sold");

                if (beginNode != null)
                {
                    pos.ValueBegin = beginNode.InnerText.ToDecimal();
                }

                if (buyNode != null &&
                    sellNode != null)
                {
                    pos.ValueCurrent = pos.ValueBegin + buyNode.InnerText.ToDecimal() - sellNode.InnerText.ToDecimal();
                }

                portfolio.SetNewPosition(pos);
            }

            return portfolio;
        }

        private void _client_ClientOnUpdateLimits(ClientLimits clientLimits)
        {
            try
            {
                if (_portfolios == null)
                {
                    return;
                }

                var needPortfolio = _portfolios.Find(p => p.Number == clientLimits.Client);

                if (needPortfolio != null)
                {
                    InitPortfolio(needPortfolio, clientLimits);
                }

                PortfolioEvent?.Invoke(_portfolios);

            }
            catch (Exception error)
            {
                SendLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private Portfolio InitPortfolio(Portfolio portfolio, ClientLimits clientLimits)
        {
            portfolio.ValueBegin = clientLimits.MoneyCurrent.ToDecimal();
            portfolio.ValueCurrent = clientLimits.MoneyFree.ToDecimal();
            portfolio.ValueBlocked = clientLimits.MoneyReserve.ToDecimal();
            portfolio.Profit = clientLimits.Profit.ToDecimal();

            return portfolio;
        }

        private void _client_ClientOnUpdatePositions(TransaqPositions transaqPositions)
        {
            if (_portfolios == null)
            {
                _portfolios = new List<Portfolio>();
            }

            if (transaqPositions.Forts_position.Count == 0)
            {
                foreach (var portfolio in _portfolios)
                {
                    portfolio.ClearPositionOnBoard();
                }
            }
            else
            {
                foreach (var fortsPosition in transaqPositions.Forts_position)
                {
                    var needPortfolio = _portfolios.Find(p => p.Number == fortsPosition.Client);

                    if (needPortfolio != null)
                    {
                        PositionOnBoard pos = new PositionOnBoard()
                        {
                            SecurityNameCode = fortsPosition.Seccode,
                            ValueBegin = fortsPosition.Startnet.ToDecimal(),
                            ValueCurrent = fortsPosition.Totalnet.ToDecimal(),
                            ValueBlocked = fortsPosition.Openbuys.ToDecimal() +
                                           fortsPosition.Opensells.ToDecimal(),
                            PortfolioName = needPortfolio.Number,

                        };
                        needPortfolio.SetNewPosition(pos);
                    }
                }
            }

            PortfolioEvent?.Invoke(_portfolios);
        }

        public event Action<List<Portfolio>> PortfolioEvent;

        #endregion

        #region 6 Data

        public List<Trade> GetTickDataToSecurity(Security security, DateTime startTime, DateTime endTime, DateTime actualTime)
        {
            List<Trade> trades = new List<Trade>();

            string cmd = "<command id=\"subscribe_ticks\">";
            cmd += "<security>";
            cmd += "<board>" + security.NameClass + "</board>";
            cmd += "<seccode>" + security.Name + "</seccode>";
            cmd += "<tradeno>1</tradeno>";
            cmd += "</security>";
            cmd += "<filter>true</filter>";
            cmd += "</command>";

            // sending command / Ð¾Ñ‚Ð¿Ñ€Ð°Ð²ÐºÐ° ÐºÐ¾Ð¼Ð°Ð½Ð´Ñ‹
            string res = ConnectorSendCommand(cmd);

            if (res != "<result success=\"true\"/>")
            {
                SendLogMessage("GetTickDataToSecurity method error " + res, LogMessageType.Error);
                return null;
            }

            DateTime lastTickTime = DateTime.MinValue;
            List<Tick> ticks = new List<Tick>();

            while (true)
            {

                try
                {
                    ticks = _allTicks.Where(x => x.Seccode == security.Name).ToList();
                    if (ticks.Count > 0)
                    {
                        lastTickTime = DateTime.Parse(ticks.Last().Tradetime);
                    }
                }
                catch
                {

                }

                if (lastTickTime >= actualTime)
                {
                    foreach (var tick in ticks)
                    {
                        trades.Add(new Trade()
                        {
                            SecurityNameCode = tick.Seccode,
                            Id = tick.Tradeno,
                            Price = tick.Price.ToDecimal(),
                            Side = tick.Buysell == "B" ? Side.Buy : Side.Sell,
                            Volume = tick.Quantity.ToDecimal(),
                            Time = DateTime.Parse(tick.Tradetime),
                        });
                    }

                    string cmd_uns = "<command id=\"subscribe_ticks\">";
                    cmd_uns += "<filter>true</filter>";
                    cmd_uns += "</command>";

                    string res2 = ConnectorSendCommand(cmd_uns);

                    _allTicks.RemoveAll(x => x.Seccode == security.Name);

                    if (res2 != "<result success=\"true\"/>")
                    {
                        SendLogMessage("GetTickDataToSecurity method error 2 " + res2, LogMessageType.Error);
                    }

                    break;
                }

                Thread.Sleep(300);
            }

            return trades;

        }

        public List<Candle> GetCandleDataToSecurity(Security security, TimeFrameBuilder timeFrameBuilder, DateTime startTime, DateTime endTime,
            DateTime actualTime)
        {
            return null;
        }

        private RateGate _rateGateCandle = new RateGate(1, TimeSpan.FromMilliseconds(300));

        public void GetCandleHistory(CandleSeries series)
        {
            _rateGateCandle.WaitToProceed();
            Task.Run(() => GetCandles(series, 1), _cancellationToken);
        }

        private void GetCandles(CandleSeries series, int countTry)
        {
            try
            {
                if (ServerStatus == ServerConnectStatus.Disconnect)
                {
                    return;
                }

                Security security = series.Security;
                TimeFrame tf = series.TimeFrame;

                int newTf;
                int oldTf;
                string needPeriodId = GetNeedIdPeriod(tf, out newTf, out oldTf);

                string cmd = "<command id=\"gethistorydata\">";
                cmd += "<security>";
                cmd += "<board>" + security.NameClass + "</board>";
                cmd += "<seccode>" + security.Name + "</seccode>";
                cmd += "</security>";
                cmd += "<period>" + needPeriodId + "</period>";
                cmd += "<count>" + 1000 + "</count>";
                cmd += "<reset>" + "true" + "</reset>";
                cmd += "</command>";

                // sending command / отправка команды
                string res = ConnectorSendCommand(cmd);

                if (res != "<result success=\"true\"/>")
                {
                    if (countTry >= 3)
                    {
                        SendLogMessage(OsLocalization.Market.Message95 + "  " + res, LogMessageType.Error);

                        if (ServerStatus != ServerConnectStatus.Disconnect)
                        {
                            ServerStatus = ServerConnectStatus.Disconnect;
                            DisconnectEvent();
                        }

                        return;
                    }
                    else
                    {
                        countTry++;
                        GetCandles(series, countTry);
                        return;
                    }
                }

                DateTime startLoadingTime = DateTime.Now;

                while (startLoadingTime.AddSeconds(10) > DateTime.Now)
                {
                    TransaqEntity.Candles candles = null;

                    for (int i = 0; i < _allCandleSeries.Count; i++)
                    {
                        TransaqEntity.Candles curSeries = _allCandleSeries[i];

                        if (curSeries.Seccode == security.Name && curSeries.Period == needPeriodId)
                        {
                            candles = curSeries;
                            break;
                        }
                    }

                    if (candles == null)
                    {
                        Thread.Sleep(200);
                        continue;
                    }

                    List<Candle> donorCandles = ParseCandles(candles);

                    if ((tf == TimeFrame.Min1 && needPeriodId == "1") ||
                        (tf == TimeFrame.Min5 && needPeriodId == "2") ||
                        (tf == TimeFrame.Min15 && needPeriodId == "3") ||
                        (tf == TimeFrame.Hour1 && needPeriodId == "4"))
                    {
                        series.CandlesAll = donorCandles;
                    }
                    else
                    {
                        series.CandlesAll = BuildCandles(donorCandles, newTf, oldTf);
                    }

                    for (int i = 0; series.CandlesAll != null && i < series.CandlesAll.Count; i++)
                    {
                        if (series.CandlesAll[i] == null)
                        {
                            series.CandlesAll.RemoveAt(i);
                            i--;
                        }
                    }

                    for (int i = 1; series.CandlesAll != null && i < series.CandlesAll.Count; i++)
                    {
                        if (series.CandlesAll[i - 1].TimeStart == series.CandlesAll[i].TimeStart)
                        {
                            series.CandlesAll.RemoveAt(i);
                            i--;
                        }
                    }

                    for (int i = 0; series.CandlesAll != null && i < series.CandlesAll.Count; i++)
                    {
                        if (series.CandlesAll[i].Open == 0
                            || series.CandlesAll[i].High == 0
                            || series.CandlesAll[i].Low == 0 
                            || series.CandlesAll[i].Close == 0)
                        {
                            series.CandlesAll.RemoveAt(i);
                            i--;
                        }
                    }

                    series.UpdateAllCandles();
                    series.IsStarted = true;
                    return;
                }

                if (countTry >= 3)
                {
                    SendLogMessage(OsLocalization.Market.Message95 + security.Name, LogMessageType.Error);
                    return;
                }
                else
                {
                    countTry++;
                    GetCandles(series, countTry);
                    return;
                }
            }
            catch (Exception ex)
            {
                SendLogMessage("Error GetCandles  " + ex.ToString(), LogMessageType.Error);
            }
        }

        private List<Candle> ParseCandles(TransaqEntity.Candles candles)
        {
            try
            {
                List<Candle> osCandles = new List<Candle>();

                foreach (var candle in candles.Candle)
                {
                    osCandles.Add(new Candle()
                    {
                        Open = candle.Open.ToDecimal(),
                        High = candle.High.ToDecimal(),
                        Low = candle.Low.ToDecimal(),
                        Close = candle.Close.ToDecimal(),
                        Volume = candle.Volume.ToDecimal(),
                        TimeStart = DateTime.Parse(candle.Date),
                    });
                }

                return osCandles;
            }
            catch (Exception e)
            {
                return null;
            }
        }

        private List<Candle> BuildCandles(List<Candle> oldCandles, int needTf, int oldTf)
        {
            List<Candle> newCandles = new List<Candle>();

            if(oldCandles == null ||
                oldCandles.Count == 0)
            {
                return newCandles;
            }

            int index;

            if (needTf == 120)
            {
                index = oldCandles.FindIndex(can => can.TimeStart.Hour % 2 == 0);
            }
            else if (needTf == 1440)
            {
                index = oldCandles.FindIndex(can => can.TimeStart.Hour == 10 &&
                                                    can.TimeStart.Minute == 0 &&
                                                    can.TimeStart.Second == 0);

                for (int i = index; i < oldCandles.Count; i++)
                {
                    if (oldCandles[i].TimeStart.Hour == 10 &&
                        oldCandles[i].TimeStart.Minute == 0 &&
                        oldCandles[i].TimeStart.Second == 0)
                    {
                        if (newCandles.Count != 0)
                        {
                            newCandles[newCandles.Count - 1].State = CandleState.Finished;
                        }
                        newCandles.Add(new Candle());
                        newCandles[newCandles.Count - 1].State = CandleState.None;
                        newCandles[newCandles.Count - 1].Open = oldCandles[i].Open;
                        newCandles[newCandles.Count - 1].TimeStart = oldCandles[i].TimeStart;
                        newCandles[newCandles.Count - 1].Low = Decimal.MaxValue;
                    }

                    if (newCandles.Count == 0)
                    {
                        continue;
                    }

                    newCandles[newCandles.Count - 1].High = oldCandles[i].High > newCandles[newCandles.Count - 1].High
                        ? oldCandles[i].High
                        : newCandles[newCandles.Count - 1].High;

                    newCandles[newCandles.Count - 1].Low = oldCandles[i].Low < newCandles[newCandles.Count - 1].Low
                        ? oldCandles[i].Low
                        : newCandles[newCandles.Count - 1].Low;

                    newCandles[newCandles.Count - 1].Close = oldCandles[i].Close;

                    newCandles[newCandles.Count - 1].Volume += oldCandles[i].Volume;

                }

                return newCandles;
            }
            else
            {
                index = oldCandles.FindIndex(can => can.TimeStart.Minute % needTf == 0);
            }

            if(index < 0)
            {
                index = 0;
            }

            int count = needTf / oldTf;

            int counter = 0;

            Candle newCandle = new Candle();

            for (int i = index; i < oldCandles.Count; i++)
            {
                counter++;

                if (counter == 1)
                {
                    newCandle = new Candle();
                    newCandle.Open = oldCandles[i].Open;
                    newCandle.TimeStart = oldCandles[i].TimeStart;
                    if (needTf <= 60 && newCandle.TimeStart.Minute % needTf != 0)  //AVP, если свечка пришла в некратное ТФ время, например, был пропуск свечи, то ТФ правим на кратное. на MOEX  в пропущенные на клиринге свечках, на 10 минутках давало сбой - сдвиг свечек на 5 минут.
                    {
                        newCandle.TimeStart = newCandle.TimeStart.AddMinutes((newCandle.TimeStart.Minute % needTf) * -1);
                    }
                    newCandle.Low = Decimal.MaxValue;
                }

                newCandle.High = oldCandles[i].High > newCandle.High
                    ? oldCandles[i].High
                    : newCandle.High;

                newCandle.Low = oldCandles[i].Low < newCandle.Low
                    ? oldCandles[i].Low
                    : newCandle.Low;

                newCandle.Volume += oldCandles[i].Volume;



                if (counter == count || (needTf <= 60 && i < oldCandles.Count - 2 && oldCandles[i + 1].TimeStart.Minute % needTf == 0))    // AVP добавил проверку "или", что следующая свечка в мелком ТФ, должна войти в следующую свечу более крупного ТФ
                {
                    newCandle.Close = oldCandles[i].Close;
                    newCandle.State = CandleState.Finished;
                    newCandles.Add(newCandle);
                    counter = 0;
                }

                if (i == oldCandles.Count - 1 && counter != count)
                {
                    newCandle.Close = oldCandles[i].Close;
                    newCandle.State = CandleState.Started;
                    newCandles.Add(newCandle);

                }
            }

            return newCandles;
        }

        private string GetNeedIdPeriod(TimeFrame tf, out int newTf, out int oldTf)
        {
            switch (tf)
            {
                case TimeFrame.Min1:
                    newTf = 1;
                    oldTf = 1;
                    return "1";
                case TimeFrame.Min2:
                    newTf = 2;
                    oldTf = 1;
                    return "1";
                case TimeFrame.Min3:
                    newTf = 3;
                    oldTf = 1;
                    return "1";
                case TimeFrame.Min5:
                    newTf = 5;
                    oldTf = 5;
                    return "2";
                case TimeFrame.Min10:
                    newTf = 10;
                    oldTf = 5;
                    return "2";
                case TimeFrame.Min20:
                    newTf = 20;
                    oldTf = 5;
                    return "2";
                case TimeFrame.Min15:
                    newTf = 15;
                    oldTf = 15;
                    return "3";
                case TimeFrame.Min30:
                    newTf = 30;
                    oldTf = 15;
                    return "3";
                case TimeFrame.Min45:
                    newTf = 45;
                    oldTf = 15;
                    return "3";
                case TimeFrame.Hour1:
                    newTf = 60;
                    oldTf = 60;
                    return "4";
                case TimeFrame.Hour2:
                    newTf = 120;
                    oldTf = 60;
                    return "4";
                case TimeFrame.Day:
                    newTf = 1440;
                    oldTf = 60;
                    return "4";
                case TimeFrame.Hour4:
                    newTf = 240;
                    oldTf = 60;
                    return "4";
                default:
                    newTf = 0;
                    oldTf = 0;
                    return "5";
            }
        }

        public List<Candle> GetLastCandleHistory(Security security, TimeFrameBuilder timeFrameBuilder, int candleCount)
        {
            return null;
        }

        private List<TransaqEntity.Candles> _allCandleSeries = new List<TransaqEntity.Candles>();

        private void _client_ClientOnNewCandles(TransaqEntity.Candles candles)
        {
            
        }

        private List<Tick> _allTicks = new List<Tick>();

        #endregion

        #region 7 Security subscrible

        private RateGate _rateGateSubscrible = new RateGate(1, TimeSpan.FromMilliseconds(300));

        private List<Security> _subscribeSecurities = new List<Security>();

        public void Subscrible(Security security)
        {
            if (ServerStatus == ServerConnectStatus.Disconnect)
            {
                return;
            }

            for (int i = 0; i < _subscribeSecurities.Count; i++)
            {
                if (_subscribeSecurities[i].Name == security.Name
                    && _subscribeSecurities[i].NameId == security.NameId
                     && _subscribeSecurities[i].NameFull == security.NameFull
                    && _subscribeSecurities[i].NameClass == security.NameClass)
                {
                    return;
                }
            }

            _subscribeSecurities.Add(security);

            SubscribeRecursion(security, 1);
        }

        private void SubscribeRecursion(Security security, int counter)
        {
            _rateGateSubscrible.WaitToProceed();

            if (ServerStatus == ServerConnectStatus.Disconnect)
            {
                return;
            }

            string cmd = "<command id=\"subscribe\">";
            cmd += "<alltrades>";
            cmd += "<security>";
            cmd += "<board>" + security.NameClass + "</board>";
            cmd += "<seccode>" + security.Name + "</seccode>";
            cmd += "</security>";
            cmd += "</alltrades>";
            cmd += "<quotes>";
            cmd += "<security>";
            cmd += "<board>" + security.NameClass + "</board>";
            cmd += "<seccode>" + security.Name + "</seccode>";
            cmd += "</security>";
            cmd += "</quotes>";
            cmd += "</command>";

            // sending command / отправка команды
            string res = ConnectorSendCommand(cmd);

            if (res != "<result success=\"true\"/>")
            {
                if (counter >= 3)
                {
                    SendLogMessage("Subscrible security error " + security.Name + "   " + res, LogMessageType.Error);
                    return;
                }
                else
                {
                    if (ServerStatus == ServerConnectStatus.Disconnect)
                    {
                        return;
                    }

                    counter++;
                    SubscribeRecursion(security, counter);
                }
            }
        }

        #endregion

        #region 8 Trade

        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptionsAttribute]
        public void SendOrder(Order order)
        {
            try
            {
                string side = order.Side == Side.Buy ? "B" : "S";

                Security needSec = _securities.Find(
                    s => s.Name == order.SecurityNameCode &&
                    s.NameClass == order.SecurityClassCode);

                if (needSec == null)
                {
                    needSec = _securities.Find(
                    s => s.Name == order.SecurityNameCode);
                }

                string cmd = "<command id=\"neworder\">";
                cmd += "<security>";
                cmd += "<board>" + needSec.NameClass + "</board>";
                cmd += "<seccode>" + needSec.Name + "</seccode>";
                cmd += "</security>";

                if (order.PortfolioNumber.StartsWith("United_"))
                {
                    var union = order.PortfolioNumber.Split('_')[1];
                    cmd += "<union>" + union + "</union>";
                }
                else
                {
                    cmd += "<client>" + order.PortfolioNumber + "</client>";
                }
                if (order.TypeOrder == OrderPriceType.Limit)
                {
                    cmd += "<price>" + order.Price.ToString().Replace(',', '.') + "</price>";
                }
                else if (order.TypeOrder == OrderPriceType.Market)
                {
                    cmd += "<bymarket/>";
                    //cmd += "<price>" + "0" + "</price>";
                }

                string volume = order.Volume.ToString();

                volume = volume.Replace(",0", "");
                volume = volume.Replace(".0", "");

                cmd += "<quantity>" + volume + "</quantity>";
                cmd += "<buysell>" + side + "</buysell>";
                cmd += "<brokerref>" + order.NumberUser + "</brokerref>";
                cmd += "<unfilled> PutInQueue </unfilled>";

                if (needSec.NameClass == "TQBR")
                {
                    cmd += "<usecredit> true </usecredit>";
                }

                cmd += "</command>";

                lock (_sendOrdersLocker)
                {
                    _sendOrders.Add(order);
                    if (_sendOrders.Count > 500)
                    {
                        _sendOrders.RemoveAt(0);
                    }
                }

                // sending command / отправка команды
                string res = ConnectorSendCommand(cmd);

                if (res == null)
                {
                    order.State = OrderStateType.Fail;
                    SendLogMessage("SendOrderFall. Order num: " + order.NumberUser, LogMessageType.Error);
                }

                Result result = Deserialize<Result>(res);

                if (!result.Success)
                {
                    order.State = OrderStateType.Fail;
                    SendLogMessage("SendOrderFall" + result.Message, LogMessageType.Error);

                    if (MyOrderEvent != null)
                    {
                        MyOrderEvent(order);
                    }
                }
                else
                {
                    order.NumberUser = result.TransactionId;
                }

                order.TimeCallBack = ServerTime;
            }
            catch (Exception ex)
            {
                SendLogMessage(ex.ToString(), LogMessageType.Error);
            }
        }

        List<Order> _sendOrders = new List<Order>();

        private string _sendOrdersLocker = "sendOrdersLocker";

        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptionsAttribute]
        public void CancelOrder(Order order)
        {
            try
            {
                string cmd = "<command id=\"cancelorder\">";
                cmd += "<transactionid>" + order.NumberUser + "</transactionid>";
                cmd += "</command>";

                // отправка команды
                string res = ConnectorSendCommand(cmd);

                if (!res.StartsWith("<result success=\"true\""))
                {
                    SendLogMessage("CancelOrder method error " + res, LogMessageType.Error);
                }
            }
            catch (Exception ex)
            {
                SendLogMessage(ex.ToString(), LogMessageType.Error);
            }
        }

        public void ChangeOrderPrice(Order order, decimal newPrice)
        {

        }

        public void CancelAllOrders()
        {

        }

        public void GetOrdersState(List<Order> orders)
        {

        }

        public void ResearchTradesToOrders(List<Order> orders)
        {

        }

        public void CancelAllOrdersToSecurity(Security security)
        {

        }

        public void GetAllActivOrders()
        {

        }

        public void GetOrderStatus(Order order)
        {

        }

        #endregion


        #region Parsing incomig data

        private readonly ConcurrentQueue<string> _newMessage = new ConcurrentQueue<string>();

        /// <summary>
        /// processor of data from callbacks 
        /// обработчик данных пришедших через каллбек
        /// </summary>
        /// <param name="pData">data from Transaq / данные, поступившие от транзака</param>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptionsAttribute]
        private bool CallBackDataHandler(IntPtr pData)
        {
            try
            {
                //if (_isDisposed == false)
                //{
                    string data = MarshalUtf8.PtrToStringUtf8(pData);
                    _newMessage.Enqueue(data);
                    FreeMemory(pData);
                //}

                return true;
            }
            catch (Exception e)
            {
                SendLogMessage(e.ToString(), LogMessageType.Error);
                return false;
            }
        }

        /// <summary>
        /// takes messages from the shared queue, converts them to C# classes, and sends them to up
        /// берет сообщения из общей очереди, конвертирует их в классы C# и отправляет на верх
        /// </summary>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptionsAttribute]
        private void Converter()
        {
            while (true)
            {
                try
                {
                    if (!_newMessage.IsEmpty)
                    {
                        string data;

                        if (_newMessage.TryDequeue(out data))
                        {
                            // тяжёлые данные переносим в другую очередь разбирающуюся другим потоком
                            if (data.StartsWith("<quotes>"))
                            {
                                List<Quote> quotes = _deserializer.Deserialize<List<Quote>>(new RestResponse() { Content = data });

                                _mdQueue.Enqueue(quotes);
                            }
                            else if (data.StartsWith("<alltrades>"))
                            {
                                List<TransaqEntity.Trade> allTrades = _deserializer.Deserialize<List<TransaqEntity.Trade>>(new RestResponse() { Content = data });

                                _tradesQueue.Enqueue(allTrades);
                            }
                            else if (data.StartsWith("<ticks>"))
                            {
                                List<Tick> newTicks = _deserializer.Deserialize<List<Tick>>(new RestResponse() { Content = data });

                                _allTicks.AddRange(newTicks);
                            }
                            else if (data.StartsWith("<pits>"))
                            {
                                continue;
                            }
                            else if (data.StartsWith("<orders>"))
                            {
                                List<TransaqEntity.Order> orders = _deserializer.Deserialize<List<TransaqEntity.Order>>(new RestResponse() { Content = data });

                                _ordersQueue.Enqueue(orders);
                            }
                            else if (data.StartsWith("<mc_portfolio"))
                            {
                                _client_ClientOnUpdatePortfolio(data);
                            }
                            else if (data.StartsWith("<positions"))
                            {
                                TransaqPositions positions = Deserialize<TransaqPositions>(data);

                                _client_ClientOnUpdatePositions(positions);
                            }
                            else if (data.StartsWith("<trades>"))
                            {
                                List<TransaqEntity.Trade> myTrades = _deserializer.Deserialize<List<TransaqEntity.Trade>>(new RestResponse() { Content = data });

                                _myTradesQueue.Enqueue(myTrades);
                            }
                            else if (data.StartsWith("<sec_info_upd>"))
                            {
                                SecurityInfo newInfo = _deserializer.Deserialize<SecurityInfo>(new RestResponse() { Content = data }); ;
                                _client_UpdateSecurity(newInfo);
                            }
                            else if (data.StartsWith("<securities>"))
                            {
                                _transaqSecuritiesInString.Enqueue(data);
                                _lastUpdateSecurityArrayTime = DateTime.Now;
                            }
                            else if (data.StartsWith("<clientlimits"))
                            {
                                ClientLimits limits = Deserialize<ClientLimits>(data);

                                _client_ClientOnUpdateLimits(limits);
                            }
                            else if (data.StartsWith("<client"))
                            {
                                Client clientInfo = _deserializer.Deserialize<Client>(new RestResponse() { Content = data });

                                _client_ClientsInfoUpdate(clientInfo);
                            }
                            else if (data.StartsWith("<candles"))
                            {
                                TransaqEntity.Candles newCandles = Deserialize<TransaqEntity.Candles>(data);

                                _allCandleSeries.Add(newCandles);
                            }
                            else if (data.StartsWith("<messages>"))
                            {
                                if (data.Contains("Время действия Вашего пароля истекло"))
                                {
                                    _client_NeedChangePassword();

                                    _client_Disconnected(); //TODO: может диспос?

                                }
                            }
                            else if (data.StartsWith("<server_status"))
                            {

                                ServerStatus status = Deserialize<ServerStatus>(data);

                                if (status.Connected == "true")
                                {
                                    if (ServerStatus == ServerConnectStatus.Disconnect
                                        && data.Contains("recover=\"true\"") == false)
                                    {
                                        _client_Connected();
                                    }
                                    else
                                    {
                                        if (data.Contains("recover=\"true\""))
                                        {
                                            SendLogMessage("Transaq client status error: <Reconnect>", LogMessageType.Error);

                                            _client_Disconnected();
                                        }
                                    }
                                }
                                else if (status.Connected == "false")
                                {
                                    _client_Disconnected();
                                }
                                else if (status.Connected == "error")
                                {
                                    SendLogMessage("Transaq client status error: " + status.Text, LogMessageType.Error);

                                    _client_Disconnected();
                                }
                            }
                            else if (data.StartsWith("<error>"))
                            {
                                SendLogMessage(data, LogMessageType.Error);
                            }
                            else if (data.StartsWith("<news_header>"))
                            {
                                // пришла новость с рынка
                                //SendLogMessage(data, LogMessageType.Error);
                            }
                            else if (data.StartsWith("<markets>")
                                || data.StartsWith("<boards>")
                                || data.StartsWith("<portfolio_mct client")
                                || data.StartsWith("<overnight status=")
                                 || data.StartsWith("<union id=")
                                || data.StartsWith("<candlekinds>"))
                            {
                                // do nothin
                            }
                            else // if (data.StartsWith("<error>"))
                            {
                                SendLogMessage(data, LogMessageType.System);
                            }
                        }
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
                catch (Exception exception)
                {
                    SendLogMessage(exception.ToString(), LogMessageType.Error);
                }
            }
        }

        #endregion

        #region 9 Incoming streams data

        ConcurrentQueue<List<TransaqEntity.Order>> _ordersQueue = new ConcurrentQueue<List<TransaqEntity.Order>>();

        ConcurrentQueue<List<TransaqEntity.Trade>> _myTradesQueue = new ConcurrentQueue<List<TransaqEntity.Trade>>();

        ConcurrentQueue<List<TransaqEntity.Trade>> _tradesQueue = new ConcurrentQueue<List<TransaqEntity.Trade>>();


        ConcurrentQueue<List<Quote>> _mdQueue = new ConcurrentQueue<List<Quote>>();

        

        private void _client_ClientOnNewTradesEvent(List<TransaqEntity.Trade> trades)
        {
            if (ServerStatus == ServerConnectStatus.Disconnect)
            {
                return;
            }

            
        }

        private void _client_ClientOnUpdateMarketDepth(List<Quote> quotes)
        {
            if (ServerStatus == ServerConnectStatus.Disconnect)
            {
                return;
            }

            
        }

        private void _client_ClientOnMyOrderEvent(List<TransaqEntity.Order> orders)
        {
            
        }

        private void _client_ClientOnMyTradeEvent(List<TransaqEntity.Trade> trades)
        {
            
        }

        #endregion

        #region 10 Incoming data parsing flow

        private void ThreadDataParsingWorkPlace()
        {
            while (true)
            {
                try
                {
                    if (ServerStatus == ServerConnectStatus.Disconnect)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }

                    if (_ordersQueue.IsEmpty == false)
                    {
                        List<TransaqEntity.Order> orders = null;

                        if (_ordersQueue.TryDequeue(out orders))
                        {
                            UpdateMyOrders(orders);
                        }

                    }
                    else if (_myTradesQueue.IsEmpty == false)
                    {
                        List<TransaqEntity.Trade> trades = null;

                        if (_myTradesQueue.TryDequeue(out trades))
                        {
                            UpdateMyTrades(trades);
                        }
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
                catch (Exception e)
                {
                    SendLogMessage(e.ToString(), LogMessageType.Error);
                    Thread.Sleep(5000);
                }
            }
        }

        private void ThreadTradesParsingWorkPlace()
        {
            while (true)
            {
                try
                {
                    if (ServerStatus == ServerConnectStatus.Disconnect)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }

                    if (_tradesQueue.IsEmpty == false)
                    {
                        List<TransaqEntity.Trade> trades = null;

                        if (_tradesQueue.TryDequeue(out trades))
                        {
                            UpdateTrades(trades);
                        }
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
                catch (Exception e)
                {
                    SendLogMessage(e.ToString(), LogMessageType.Error);
                    Thread.Sleep(5000);
                }
            }
        }

        private void ThreadMarketDepthsParsingWorkPlace()
        {
            while (true)
            {
                try
                {
                    if (ServerStatus == ServerConnectStatus.Disconnect)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }

                    if (_mdQueue.IsEmpty == false)
                    {
                        List<Quote> quotes = null;

                        if (_mdQueue.TryDequeue(out quotes))
                        {
                            UpdateMarketDepths(quotes);
                        }
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
                catch (Exception e)
                {
                    SendLogMessage(e.ToString(), LogMessageType.Error);
                    Thread.Sleep(5000);
                }
            }
        }

        private void UpdateMyTrades(List<TransaqEntity.Trade> trades)
        {
            for (int i = 0; i < trades.Count; i++)
            {
                TransaqEntity.Trade trade = trades[i];

                MyTrade myTrade = new MyTrade();
                myTrade.Time = DateTime.Parse(trade.Time);
                myTrade.NumberOrderParent = trade.Orderno;
                myTrade.NumberTrade = trade.Tradeno;
                myTrade.Volume = trade.Quantity.ToDecimal();
                myTrade.Price = trade.Price.ToDecimal();
                myTrade.SecurityNameCode = trade.Seccode;
                myTrade.Side = trade.Buysell == "B" ? Side.Buy : Side.Sell;

                MyTradeEvent?.Invoke(myTrade);
            }
        }

        private void UpdateMyOrders(List<TransaqEntity.Order> orders)
        {
            for (int i = 0; i < orders.Count; i++)
            {
                TransaqEntity.Order order = orders[i];

                if (order.Orderno == "0")
                {
                    //continue;
                }

                Order newOrder = new Order();
                newOrder.SecurityNameCode = order.Seccode;
                newOrder.NumberUser = Convert.ToInt32(order.Transactionid);
                newOrder.NumberMarket = order.Orderno;
                newOrder.TimeCallBack = order.Time != null ? DateTime.Parse(order.Time) : ServerTime;
                newOrder.Side = order.Buysell == "B" ? Side.Buy : Side.Sell;
                newOrder.Volume = order.Quantity.ToDecimal();
                newOrder.Price = order.Price.ToDecimal();
                newOrder.ServerType = ServerType.Transaq;
                newOrder.PortfolioNumber = string.IsNullOrEmpty(order.Union) ? order.Client : order.Union;

                lock (_sendOrdersLocker)
                {
                    if (string.IsNullOrEmpty(newOrder.NumberMarket) == false
                        && newOrder.NumberUser != 0
                        && newOrder.NumberMarket != "0")
                    {
                        for (int i2 = _sendOrders.Count - 1; i2 > -1; i2--)
                        {
                            if (_sendOrders[i2].NumberUser == newOrder.NumberUser)
                            {
                                newOrder.TypeOrder = _sendOrders[i2].TypeOrder;
                                break;
                            }
                        }
                    }
                }

                if (order.Status == "active")
                {
                    newOrder.State = OrderStateType.Active;
                }
                else if (order.Status == "cancelled" ||
                         order.Status == "expired" ||
                         order.Status == "disabled" ||
                         order.Status == "removed")
                {
                    if (order.Status == "removed"
                        && string.IsNullOrEmpty(order.Result) == false)
                    {
                        SendLogMessage(order.Result, LogMessageType.Error);
                    }

                    newOrder.State = OrderStateType.Cancel;
                }
                else if (order.Status == "matched")
                {
                    newOrder.State = OrderStateType.Done;
                }
                else if (order.Status == "denied" ||
                         order.Status == "rejected" ||
                         order.Status == "failed" ||
                         order.Status == "refused")
                {
                    if (string.IsNullOrEmpty(order.Result) == false)
                    {
                        SendLogMessage(order.Result, LogMessageType.Error);
                    }
                    newOrder.State = OrderStateType.Fail;
                }
                else if (order.Status == "forwarding" ||
                         order.Status == "wait" ||
                         order.Status == "watching")
                {
                    newOrder.State = OrderStateType.Pending;
                }
                else
                {
                    newOrder.State = OrderStateType.None;
                }

                MyOrderEvent?.Invoke(newOrder);
            }
        }

        private List<MarketDepth> _depths;

        DateTime _lastMdTime;

        private void UpdateMarketDepths(List<Quote> quotes)
        {
            if (quotes == null || quotes.Count == 0)
            {
                return;
            }
            if (_depths == null)
            {
                _depths = new List<MarketDepth>();
            }

            Dictionary<string, List<Quote>> sortedQuotes = new Dictionary<string, List<Quote>>();

            foreach (var quote in quotes)
            {
                if (!sortedQuotes.ContainsKey(quote.Seccode))
                {
                    sortedQuotes.Add(quote.Seccode, new List<Quote>());
                }

                sortedQuotes[quote.Seccode].Add(quote);
            }

            foreach (var sortedQuote in sortedQuotes)
            {
                MarketDepth needDepth = _depths.Find(depth => depth.SecurityNameCode == sortedQuote.Value[0].Seccode);

                if (needDepth == null)
                {
                    needDepth = new MarketDepth();
                    needDepth.SecurityNameCode = sortedQuote.Value[0].Seccode;
                    _depths.Add(needDepth);
                }

                for (int i = 0; i < sortedQuote.Value.Count; i++)
                {
                    if (sortedQuote.Value[i].Buy == -1 && sortedQuote.Value[i].Sell == -1)
                    {

                    }
                    if (sortedQuote.Value[i].Buy > 0)
                    {
                        var needLevel = needDepth.Bids.Find(level => level.Price == sortedQuote.Value[i].Price);
                        if (needLevel != null)
                        {
                            needLevel.Bid = sortedQuote.Value[i].Buy;
                        }
                        else
                        {
                            if (sortedQuote.Value[i].Price == 0)
                            {
                                continue;
                            }

                            needDepth.Bids.Add(new MarketDepthLevel()
                            {
                                Price = sortedQuote.Value[i].Price,
                                Bid = sortedQuote.Value[i].Buy,
                            });
                            needDepth.Bids.Sort((a, b) =>
                            {
                                if (a.Price > b.Price)
                                {
                                    return -1;
                                }
                                else if (a.Price < b.Price)
                                {
                                    return 1;
                                }
                                else
                                {
                                    return 0;
                                }
                            });

                            while (needDepth.Asks.Count > 0 &&
                             needDepth.Bids[0].Price >= needDepth.Asks[0].Price)
                            {
                                needDepth.Asks.RemoveAt(0);
                            }
                        }

                    }
                    if (sortedQuote.Value[i].Sell > 0)
                    {
                        var needLevel = needDepth.Asks.Find(level => level.Price == sortedQuote.Value[i].Price);
                        if (needLevel != null)
                        {
                            needLevel.Ask = sortedQuote.Value[i].Sell;
                        }
                        else
                        {
                            if (sortedQuote.Value[i].Price == 0)
                            {
                                continue;
                            }

                            needDepth.Asks.Add(new MarketDepthLevel()
                            {
                                Price = sortedQuote.Value[i].Price,
                                Ask = sortedQuote.Value[i].Sell,
                            });
                            needDepth.Asks.Sort((a, b) =>
                            {
                                if (a.Price > b.Price)
                                {
                                    return 1;
                                }
                                else if (a.Price < b.Price)
                                {
                                    return -1;
                                }
                                else
                                {
                                    return 0;
                                }
                            });

                            while (needDepth.Bids.Count > 0 &&
                             needDepth.Bids[0].Price >= needDepth.Asks[0].Price)
                            {
                                needDepth.Bids.RemoveAt(0);
                            }
                        }
                    }
                    if (sortedQuote.Value[i].Buy == -1)
                    {
                        var deleteLevelIndex = needDepth.Bids.FindIndex(level => level.Price == sortedQuote.Value[i].Price);
                        if (deleteLevelIndex != -1)
                        {
                            needDepth.Bids.RemoveAt(deleteLevelIndex);
                        }
                    }
                    if (sortedQuote.Value[i].Sell == -1)
                    {
                        var deleteLevelIndex = needDepth.Asks.FindIndex(level => level.Price == sortedQuote.Value[i].Price);
                        if (deleteLevelIndex != -1)
                        {
                            needDepth.Asks.RemoveAt(deleteLevelIndex);
                        }
                    }
                }

                needDepth.Time = ServerTime == DateTime.MinValue ? TimeManager.GetExchangeTime("Russian Standard Time") : ServerTime;

                if (needDepth.Time <= _lastMdTime)
                {
                    needDepth.Time = _lastMdTime.AddTicks(1);
                }

                _lastMdTime = needDepth.Time;


                if (needDepth.Asks == null ||
                    needDepth.Asks.Count == 0 ||
                    needDepth.Bids == null ||
                    needDepth.Bids.Count == 0)
                {
                    return;
                }

                while (needDepth.Bids.Count > 0 &&
                    needDepth.Bids[0].Price >= needDepth.Asks[0].Price)
                {
                    needDepth.Bids.RemoveAt(0);
                }

                if (needDepth.Bids.Count == 0)
                {
                    return;
                }

                while (needDepth.Bids.Count > 25)
                {
                    needDepth.Bids.RemoveAt(needDepth.Bids.Count - 1);
                }

                while (needDepth.Asks.Count > 25)
                {
                    needDepth.Asks.RemoveAt(needDepth.Asks.Count - 1);
                }

                if (MarketDepthEvent != null)
                {
                    MarketDepthEvent(needDepth.GetCopy());
                }
            }
        }

        private void UpdateTrades(List<TransaqEntity.Trade> trades)
        {
            for (int i = 0; i < trades.Count; i++)
            {
                TransaqEntity.Trade t = trades[i];

                if (string.IsNullOrEmpty(t.Price)
                    || string.IsNullOrEmpty(t.Quantity))
                {
                    continue;
                }

                Trade trade = new Trade();
                trade.SecurityNameCode = t.Seccode;
                trade.Id = t.Tradeno;
                trade.Price = t.Price.ToDecimal();
                trade.Side = t.Buysell == "B" ? Side.Buy : Side.Sell;
                trade.Volume = t.Quantity.ToDecimal();
                trade.Time = DateTime.Parse(t.Time);

                NewTradesEvent?.Invoke(trade);
            }
        }

        public event Action<Order> MyOrderEvent;

        public event Action<MyTrade> MyTradeEvent;

        public event Action<MarketDepth> MarketDepthEvent;

        public event Action<Trade> NewTradesEvent;

        #endregion

        #region 11 Log messages

        private void SendLogMessage(string message, LogMessageType type)
        {
            if (type == LogMessageType.Error)
            {

            }

            if (LogMessageEvent != null)
            {
                LogMessageEvent(message, type);
            }
        }

        public event Action<string, LogMessageType> LogMessageEvent;

        #endregion

        private string _commandLocker = "commandSendLocker";

        /// <summary>
        /// sent the command
        /// отправить команду
        /// </summary>
        /// <param name="command">command as a XML document / команда в виде XML документа</param>
        /// <returns>result of sending command/результат отправки команды</returns>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptionsAttribute]
        public string ConnectorSendCommand(string command)
        {
            try
            {
                lock (_commandLocker)
                {
                    IntPtr pData = MarshalUtf8.StringToHGlobalUtf8(command);
                    IntPtr pResult = SendCommand(pData);

                    string result = MarshalUtf8.PtrToStringUtf8(pResult);

                    Marshal.FreeHGlobal(pData);
                    FreeMemory(pResult);

                    return result;
                }
            }
            catch (AccessViolationException e)
            {
                // no message
                return null;
            }
            catch (Exception e)
            {
                SendLogMessage(e.ToString(), LogMessageType.Error);
                return null;
            }

        }

        /// <summary>
        /// converts a string of data to needed format
        /// преобразует строку с данными в нужный формат
        /// </summary>
        /// <typeparam name="T">type for converting / тип, в который нужно преобразовать данные</typeparam>
        /// <param name="data">data string / строка с данными</param>
        /// <returns>nessesary object / объект нужного типа</returns>
        public T Deserialize<T>(string data)
        {
            T newData;
            var formatter = new XmlSerializer(typeof(T));
            using (StringReader fs = new StringReader(data))
            {
                newData = (T)formatter.Deserialize(fs);
            }
            return newData;
        }

        //--------------------------------------------------------------------------------
        // file of library TXmlConnector.the dll must be in the same folder as the program
        // файл библиотеки TXmlConnector.dll должен находиться в одной папке с программой

        [DllImport("txmlconnector64.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern bool SetCallback(CallBackDelegate pCallback);

        [DllImport("txmlconnector64.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr SendCommand(IntPtr pData);

        [DllImport("txmlconnector64.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern bool FreeMemory(IntPtr pData);

        [DllImport("TXmlConnector64.dll", CallingConvention = CallingConvention.Winapi)]
        private static extern IntPtr Initialize(IntPtr pPath, Int32 logLevel);

        [DllImport("TXmlConnector64.dll", CallingConvention = CallingConvention.Winapi)]
        private static extern IntPtr UnInitialize();

        [DllImport("TXmlConnector64.dll", CallingConvention = CallingConvention.Winapi)]
        private static extern IntPtr SetLogLevel(Int32 logLevel);
        //--------------------------------------------------------------------------------
    }
}