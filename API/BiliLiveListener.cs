using Newtonsoft.Json;
using Newtonsoft.Json.Linq; // 引入 Linq 命名空间
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BiliAPI
{
    class BiliLiveListener
    {

        public enum Protocols { Tcp, Ws, Wss };
        public Protocols Protocol { get; set; }
        // 【新增】用来存储 Cookie 的属性
        public string UserCookie { get; set; }

        public delegate void ConnectionEventHandler();
        public event ConnectionEventHandler Connected;
        public event ConnectionEventHandler Disconnected;

        public delegate void ConnectionFailedHandler(string message);
        public event ConnectionFailedHandler ConnectionFailed;

        public delegate void ServerHeartbeatRecievedHandler();
        public event ServerHeartbeatRecievedHandler ServerHeartbeatRecieved;

        public delegate void PopularityRecievedHandler(long popularity);
        public event PopularityRecievedHandler PopularityRecieved;

        // 替换为 JToken 数组
        public delegate void JsonsRecievedHandler(JToken[] jsons);
        public event JsonsRecievedHandler JsonsRecieved;

        public delegate void ItemsRecievedHandler(BiliLiveJsonParser.IItem[] items);
        public event ItemsRecievedHandler ItemsRecieved;

        private TcpClient DanmakuTcpClient { get; set; }
        private ClientWebSocket DanmakuWebSocket { get; set; }
        private long RoomId { get; set; }

        private BiliPackReader PackReader { get; set; }
        private BiliPackWriter PackWriter { get; set; }

        private Thread HeartbeatSenderThread { get; set; }
        private bool IsHeartbeatSenderRunning { get; set; }

        private Thread EventListenerThread { get; set; }
        private bool IsEventListenerRunning { get; set; }

        /// <summary>
        /// Constructor 
        /// </summary>
        /// <param name="roomId"></param>
        public BiliLiveListener(long roomId, Protocols protocol, string cookie = "")
        {
            IsHeartbeatSenderRunning = false;
            IsEventListenerRunning = false;
            RoomId = roomId;
            Protocol = protocol;
            UserCookie = cookie; // 保存传进来的 Cookie
        }

        #region Public methods

        public Task<bool> ConnectAsync() => new Task<bool>(Connect);

        public bool Connect()
        {
            PingReply pingReply = null;
            try
            {
                pingReply = new Ping().Send("live.bilibili.com");
            }
            catch (Exception)
            {

            }
            if (pingReply == null || pingReply.Status != IPStatus.Success)
            {
                ConnectionFailed?.Invoke("网络连接失败");
                return false;
            }

            DanmakuServer danmakuServer = GetDanmakuServer(RoomId);
            if (danmakuServer == null)
                return false;

            switch (Protocol)
            {
                case Protocols.Tcp:
                    DanmakuTcpClient = GetTcpConnection(danmakuServer);
                    Stream stream = DanmakuTcpClient.GetStream();

                    stream.ReadTimeout = 30 * 1000 + 1000;
                    stream.WriteTimeout = 30 * 1000 + 1000;

                    PackReader = new BiliPackReader(stream);
                    PackWriter = new BiliPackWriter(stream);
                    break;
                case Protocols.Ws:
                    DanmakuWebSocket = GetWsConnection(danmakuServer);
                    PackReader = new BiliPackReader(DanmakuWebSocket);
                    PackWriter = new BiliPackWriter(DanmakuWebSocket);
                    break;
                case Protocols.Wss:
                    DanmakuWebSocket = GetWssConnection(danmakuServer);
                    PackReader = new BiliPackReader(DanmakuWebSocket);
                    PackWriter = new BiliPackWriter(DanmakuWebSocket);
                    break;
            }

            if (!InitConnection(danmakuServer))
            {
                Disconnect();
                return false;
            }

            StartEventListener();
            StartHeartbeatSender();

            Connected?.Invoke();
            return true;
        }

        public Task DisconnectAsync() => new Task(Disconnect);

        public void Disconnect()
        {
            StopEventListener();
            StopHeartbeatSender();
            if (DanmakuTcpClient != null)
                DanmakuTcpClient.Close();
            if (DanmakuWebSocket != null)
            {
                DanmakuWebSocket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, string.Empty, CancellationToken.None);
                DanmakuWebSocket.Abort();
                DanmakuWebSocket.Dispose();
            }
            Disconnected?.Invoke();
        }

        #endregion

        #region Connect to a DanmakuServer

        private class DanmakuServer
        {
            public long RoomId;
            public string Server;
            public int Port;
            public int WsPort;
            public int WssPort;
            public string Token;
        }

        private TcpClient GetTcpConnection(DanmakuServer danmakuServer)
        {
            TcpClient tcpClient = new TcpClient();
            tcpClient.Connect(danmakuServer.Server, danmakuServer.Port);
            return tcpClient;
        }

        private ClientWebSocket GetWsConnection(DanmakuServer danmakuServer)
        {
            ClientWebSocket clientWebSocket = new ClientWebSocket();
            clientWebSocket.ConnectAsync(new Uri($"ws://{danmakuServer.Server}:{danmakuServer.WsPort}/sub"), CancellationToken.None).GetAwaiter().GetResult();
            return clientWebSocket;
        }

        private ClientWebSocket GetWssConnection(DanmakuServer danmakuServer)
        {
            ClientWebSocket clientWebSocket = new ClientWebSocket();
            clientWebSocket.ConnectAsync(new Uri($"wss://{danmakuServer.Server}:{danmakuServer.WssPort}/sub"), CancellationToken.None).GetAwaiter().GetResult();
            return clientWebSocket;
        }

        // 【新增】自动从 Cookie 中提取真实的 UID (DedeUserID)
        private long ExtractUidFromCookie(string cookie)
        {
            if (string.IsNullOrWhiteSpace(cookie)) return 0;

            // B站 Cookie 中的 UID 字段名叫 DedeUserID
            Match match = Regex.Match(cookie, @"DedeUserID=(\d+)");
            if (match.Success && long.TryParse(match.Groups[1].Value, out long uid))
            {
                return uid;
            }
            return 0; // 如果没填 Cookie 或者格式不对，退回游客状态
        }

        private bool InitConnection(DanmakuServer danmakuServer)
        {
            long currentUid = ExtractUidFromCookie(UserCookie); // 获取真实UID
            // 使用 Newtonsoft.Json 的 JObject 初始化
            JObject initMsg = new JObject
            {
                { "uid", currentUid }, // <--- 【关键修改】填入你的真实UID！
                { "roomid", danmakuServer.RoomId },
                { "protover", 3 },
                { "platform", "web" },
                { "clientver", "1.12.0" },
                { "type", 2 },
                { "key", danmakuServer.Token }
            };

            try
            {
                // Formatting.None 防止附带多余的换行符和空格造成体积浪费
                PackWriter.SendMessage((int)BiliPackWriter.MessageType.CONNECT, initMsg.ToString(Formatting.None));
                return true;
            }
            catch (SocketException)
            {
                ConnectionFailed?.Invoke("连接请求发送失败");
                return false;
            }
            catch (InvalidOperationException)
            {
                ConnectionFailed?.Invoke("连接请求发送失败");
                return false;
            }
            catch (IOException)
            {
                ConnectionFailed?.Invoke("连接请求发送失败");
                return false;
            }
        }

        #endregion

        #region Room info

        private long GetRealRoomId(long roomId)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://api.live.bilibili.com/room/v1/Room/room_init?id=" + roomId);
                if (!string.IsNullOrWhiteSpace(UserCookie))
                {
                    request.Headers[HttpRequestHeader.Cookie] = UserCookie;
                }
                HttpWebResponse response = (HttpWebResponse)request.GetResponse();
                using (StreamReader streamReader = new StreamReader(response.GetResponseStream()))
                {
                    string result = streamReader.ReadToEnd();
                    Match match = Regex.Match(result, "\"room_id\":(?<RoomId>[0-9]+)");
                    if (match.Success)
                        return long.Parse(match.Groups["RoomId"].Value);
                    return 0;
                }

            }
            catch (WebException)
            {
                ConnectionFailed?.Invoke("未能找到直播间");
                return -1;
            }

        }

        private DanmakuServer GetDanmakuServer(long roomId)
        {
            roomId = GetRealRoomId(roomId);
            if (roomId < 0)
            {
                return null;
            }
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://api.live.bilibili.com/room/v1/Danmu/getConf?room_id=" + roomId);
                if (!string.IsNullOrWhiteSpace(UserCookie))
                {
                    request.Headers[HttpRequestHeader.Cookie] = UserCookie;
                }
                HttpWebResponse response = (HttpWebResponse)request.GetResponse();

                JToken json;
                using (StreamReader streamReader = new StreamReader(response.GetResponseStream()))
                {
                    // 使用 JToken 读取
                    string jsonString = streamReader.ReadToEnd();
                    json = JToken.Parse(jsonString);
                }

                if ((int)json["code"] != 0)
                {
                    Console.Error.WriteLine("Error occurs when resolving dm servers");
                    Console.Error.WriteLine(json.ToString(Formatting.None));
                    return null;
                }

                DanmakuServer danmakuServer = new DanmakuServer
                {
                    RoomId = roomId,
                    Server = (string)json["data"]["host_server_list"][0]["host"],
                    Port = (int)json["data"]["host_server_list"][0]["port"],
                    WsPort = (int)json["data"]["host_server_list"][0]["ws_port"],
                    WssPort = (int)json["data"]["host_server_list"][0]["wss_port"],
                    Token = (string)json["data"]["token"]
                };

                return danmakuServer;

            }
            catch (WebException)
            {
                ConnectionFailed?.Invoke("直播间信息获取失败");
                return null;
            }
        }

        #endregion

        #region Heartbeat Sender

        private void StopHeartbeatSender()
        {
            IsHeartbeatSenderRunning = false;
            if (HeartbeatSenderThread != null)
                HeartbeatSenderThread.Abort();
        }

        private void StartHeartbeatSender()
        {
            StopHeartbeatSender();
            HeartbeatSenderThread = new Thread(delegate ()
            {
                IsHeartbeatSenderRunning = true;
                while (IsHeartbeatSenderRunning)
                {
                    try
                    {
                        PackWriter.SendMessage((int)BiliPackWriter.MessageType.HEARTBEAT, "");
                    }
                    catch (SocketException)
                    {
                        ConnectionFailed?.Invoke("心跳包发送失败");
                        Disconnect();
                    }
                    catch (InvalidOperationException)
                    {
                        ConnectionFailed?.Invoke("心跳包发送失败");
                        Disconnect();
                    }
                    catch (IOException)
                    {
                        ConnectionFailed?.Invoke("心跳包发送失败");
                        Disconnect();
                    }
                    Thread.Sleep(30 * 1000);
                }
            });
            HeartbeatSenderThread.Start();
        }

        #endregion

        #region Event listener

        private void StopEventListener()
        {
            IsEventListenerRunning = false;
            if (EventListenerThread != null)
                EventListenerThread.Abort();
        }

        private void StartEventListener()
        {
            EventListenerThread = new Thread(delegate ()
            {
                IsEventListenerRunning = true;
                while (IsEventListenerRunning)
                {
                    try
                    {
                        BiliPackReader.IPack[] packs = PackReader.ReadPacksAsync();

                        List<JToken> jsons = new List<JToken>();
                        List<BiliLiveJsonParser.IItem> items = new List<BiliLiveJsonParser.IItem>();

                        foreach (BiliPackReader.IPack pack in packs)
                        {
                            switch (pack.PackType)
                            {
                                case BiliPackReader.PackTypes.Popularity:
                                    PopularityRecieved?.Invoke(((BiliPackReader.PopularityPack)pack).Popularity);
                                    break;
                                case BiliPackReader.PackTypes.Command:
                                    JToken value = ((BiliPackReader.CommandPack)pack).Value;
                                    jsons.Add(value);

                                    // 【新增】只要收到弹幕包，不管三七二十一，先原样打印出来！
                                    string cmdRaw = (string)value["cmd"];
                                    if (cmdRaw != null && cmdRaw.StartsWith("DANMU_MSG"))
                                    {
                                        //Console.WriteLine($"[截获原始弹幕] {value.ToString(Newtonsoft.Json.Formatting.None)}");
                                    }

                                    BiliLiveJsonParser.IItem item = BiliLiveJsonParser.Parse(value);
                                    if (item != null)
                                        items.Add(item);
                                    break;
                                case BiliPackReader.PackTypes.Heartbeat:
                                    ServerHeartbeatRecieved?.Invoke();
                                    break;
                                
                            }
                        }

                        if (jsons.Count > 0)
                        {
                            JsonsRecieved?.Invoke(jsons.ToArray());
                        }
                        if (items.Count > 0)
                        {
                            ItemsRecieved?.Invoke(items.ToArray());
                        }
                    }
                    catch (SocketException)
                    {
                        ConnectionFailed?.Invoke("网络Socket断开连接");
                        Disconnect();
                    }
                    catch (IOException ex)
                    {
                        // <--- 【关键修改】把 ex.Message 加上，别让报错变成哑巴
                        ConnectionFailed?.Invoke($"数据流意外中断: {ex.Message}");
                        Disconnect();
                    }
                    catch (Exception ex) // 【新增】捕获所有未知的解析和转换异常
                    {
                        // 这样一旦解压失败或JSON报错，你马上就能看到详细的错误堆栈
                        ConnectionFailed?.Invoke($"数据解析崩溃: {ex.Message}");
                        Disconnect();
                    }
                }
            });
            EventListenerThread.Start();
        }

        #endregion
    }
}