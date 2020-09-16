using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Code.Common;
using Code.Component;
using Newtonsoft.Json.Linq;
using protobuf;
using ProtoBuf;
using UnityEngine;
using UnityEngine.Networking;
using Object = System.Object;

namespace Code.Core
{
    public enum NetType
    {
        None,
        Lobby,
        Game21,
    }


    public enum EvtType
    {
        Close,
        OnMessage,
        SendMessage,
    }


    public class NetPacket
    {
        public MsgID MsgId;
        public byte[] Buf;
        public NetType NetType;
        public IExtensible Msg;
        public int BodyLength;
        public EvtType EvtType;
        public int Ret;

        public T Decode<T>() where T : new()
        {
            if (Buf == null)
            {
                return new T();
            }

            using (var mem = new MemoryStream())
            {
                mem.SetLength(Buf.Length);
                mem.Write(Buf, 0, Buf.Length);
                mem.Position = 0;
                return Serializer.Deserialize<T>(mem);
            }
        }
    }


    public class NetClient : MonoBehaviour
    {
        private class Conn
        {
            private Socket _socket;

            private NetType NetType { get; }

            private Thread _tr;
            private volatile bool _connected;
            private volatile bool _connecting;
            private volatile bool _closeing;

            public Conn(NetType netType)
            {
                NetType = netType;
                _socket = null;
                _tr = null;
            }

            public void Record()
            {
                _recording = true;
            }

            public bool _Send(NetPacket packet)
            {
                if (!_connected)
                {
                    XLog.Log("connection is closed");
                    return false;
                }

                try
                {
                    var head = new byte[HeadLength];
                    var body = new byte[] { };
                    if (packet.Msg != null)
                    {
                        using (var mem = new MemoryStream())
                        {
                            Serializer.Serialize(mem, packet.Msg);
                            body = mem.ToArray();
                        }
                    }

                    long packetLength = body.Length;
                    //1.写入消息长度2字节//
                    head[0] = (byte) (packetLength & 0xFF);
                    head[1] = (byte) ((packetLength >> 8) & 0xFF);
                    //2.写入消息类型2字节//
                    head[2] = (byte) (((int) packet.MsgId) & 0xFF);
                    head[3] = (byte) (((int) packet.MsgId >> 8) & 0xFF);
                    var buf = new byte[HeadLength + packetLength];
                    Buffer.BlockCopy(head, 0, buf, 0, HeadLength);
                    if (packetLength != 0)
                    {
                        Buffer.BlockCopy(body, 0, buf, HeadLength, body.Length);
                    }

                    _socket.Send(buf);
                    return true;
                }
                catch (Exception e)
                {
                    XLog.LogError(e);
                    _ShutDown();
                }

                return false;
            }

            public void Close()
            {
                _closeing = true;
                _ShutDown();
            }

            private void _ShutDown()
            {
                if (!_connected) return;
                XLog.Log("_ShutDown------------>" + NetType);
                try
                {
                    _socket?.Shutdown(SocketShutdown.Both);
                }
                catch (Exception e2)
                {
                    XLog.LogError(e2);
                }

                _connected = false;
                _connecting = false;
            }

            public void Connect()
            {
                if (_connecting || _connected) return;
                _connecting = true;
                _tr = new Thread(Recv) {IsBackground = true};
                _tr.Start();
            }

            public bool IsConnected => _connected;
            public bool IsConnecting => _connecting;


            public bool IsRecording => _recording;

            public int Port
            {
                get
                {
                    if (NetType == NetType.Lobby)
                    {
                        return Const.LobbyPort;
                    }

                    if (NetType == NetType.Game21)
                    {
                        return Const.Game21Port;
                    }

                    return -1;
                }
            }


            private void Recv()
            {
                try
                {
                    //创建套接字
                    var port = Port;
                    XLog.Log($"开始连接服务器---------------------->Host:{Const.Host} Port{port} NetType:{NetType}");
                    _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    //端口及IP
                    _socket.Connect(Const.Host, port);
                    XLog.Log("连接服务器成功====================>addr:" + Const.Host + ":" + port);
                    _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    _socket.NoDelay = true;
                    _socket.Blocking = true;
                    _connected = true;
                    _connecting = false;
                    _closeing = false;
                    while (_connected)
                    {
                        if (null == _socket)
                        {
                            Thread.Sleep(200);
                            continue;
                        }

                        var head = new byte[4];
                        _socket.Receive(head);
                        var packet = new NetPacket
                        {
                            BodyLength = (head[0] & 0x000000ff) | (head[1] & 0x000000ff) << 8,
                            MsgId = (MsgID) ((head[3] & 0x000000ff) << 8 | (head[2] & 0x000000ff)),
                            NetType = NetType,
                            EvtType = EvtType.OnMessage
                        };
                        XLog.Log($"on mesage {packet.MsgId} nettype--->{packet.NetType}");
                        if (packet.BodyLength < 8192 * 5 && packet.BodyLength > 0)
                        {
                            packet.Buf = new byte[packet.BodyLength];
                            _socket.Receive(packet.Buf);
                        }

                        RecvQue.Enqueue(packet);
                        if (_recording)
                        {
                            RecordQue.Enqueue(packet);
                        }
                    }
                }
                catch (Exception e)
                {
                    XLog.LogError(e);
                }
                finally
                {
                    if (!_closeing)
                    {
                        RecvQue.Enqueue(new NetPacket {EvtType = EvtType.Close, NetType = NetType});
                    }

                    _ShutDown();
                    _closeing = false;
                    _connecting = false;
                    _connected = false;
                    XLog.Log("Recv Thread Exit------------------->" + NetType);
                }
            }
        }


        private Socket _clientLobby;
        private Socket _clientGame;

        /// <summary>
        /// 发送ping时间间隔
        /// </summary>
        public int pingDiff;

        /// <summary>
        /// 网络延迟
        /// </summary>
        public float lobbyDelay;

        public float gameDelay;

        public BaseScene listener;
        private static NetClient Ins { get; set; }
        private static readonly ConcurrentQueue<NetPacket> SendQue = new ConcurrentQueue<NetPacket>();
        private static readonly ConcurrentQueue<NetPacket> RecvQue = new ConcurrentQueue<NetPacket>();
        private static readonly ConcurrentQueue<NetPacket> RecordQue = new ConcurrentQueue<NetPacket>();
        private static volatile bool _recording;
        private static long TimeOffset => (DateTime.Now.ToUniversalTime().Ticks - 621355968000000000) / 10000000;
        private const int HeadLength = 4;


        public static void SetTestInfo(string token, int uid)
        {
#if TEST_NEIWANG
            Const.SetTokenAndUid(token, uid);
#endif
        }

        public static bool IsNull => Ins == null;


        private static readonly Conn LobbyConn = new Conn(NetType.Lobby);
        private static readonly Conn GameConn21 = new Conn(NetType.Game21);

        public static void Close(NetType netType)
        {
            if (netType == NetType.Lobby)
            {
                LobbyConn.Close();
            }
            else if (netType == NetType.Game21)
            {
                GameConn21.Close();
            }
        }


        public static IEnumerator Connect(NetType netType, Action<bool> cb = null)
        {
            var connect = false;
            if (netType == NetType.Lobby)
            {
                LobbyConn.Connect();
                while (LobbyConn.IsConnecting) yield return new WaitForSeconds(0.3f);
                connect = LobbyConn.IsConnected;
            }
            else if (netType == NetType.Game21)
            {
                GameConn21.Connect();
                while (GameConn21.IsConnecting) yield return new WaitForSeconds(0.3f);
                connect = GameConn21.IsConnected;
            }


            if (!connect)
            {
                AlertObj.ShowNetCut();
            }

            cb?.Invoke(connect);
        }


        protected void Awake()
        {
            if (pingDiff < 5) pingDiff = 5;
            if (Ins == null)
            {
                Ins = this;
                DontDestroyOnLoad(gameObject);
                ThreadPool.QueueUserWorkItem(SendThread, 4);
                ThreadPool.QueueUserWorkItem(PingThread, 5);
                Ins.StartCoroutine(HandlePacket());
            }
            else
            {
                Destroy(this);
            }
        }

        /*private void OnDestroy()
        {
            /*XLog.Log("net client dispose");
            DeAttach();
            LobbyConn?.Close();
            GameConn21?.Close();#1#
        }*/

        public static void Attach(BaseScene listener)
        {
            if (_recording)
            {
                _recording = false;
                while (!RecordQue.IsEmpty)
                {
                    RecordQue.TryDequeue(out var r);
                    if (r.NetType == NetType.Lobby)
                    {
                        listener.OnLobbyMessage(r);
                    }
                    else
                    {
                        listener.OnGameMessage(r);
                    }
                }
            }

            Ins.listener = listener;
        }

        private static IEnumerator HandlePacket()
        {
            NetPacket packet;
            BaseScene listener;
            while (Ins != null)
            {
                if (RecvQue.TryDequeue(out packet))
                {
                    do
                    {
                        listener = Ins.listener;
                        yield return new WaitForEndOfFrame();
                    } while (listener is null);

                    try
                    {
                        if (packet.EvtType == EvtType.Close)
                        {
                            listener.OnClose(packet.NetType);
                        }
                        else if (packet.EvtType == EvtType.OnMessage)
                        {
                            if (packet.NetType == NetType.Lobby)
                            {
                                listener.OnLobbyMessage(packet);
                            }
                            else if (packet.NetType == NetType.Game21)
                            {
                                listener.OnGameMessage(packet);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        XLog.LogError(e);
                    }
                }
                yield return new WaitForEndOfFrame();
            }
        }


        public static void DeAttach()
        {
            if (Ins == null) return;
            Ins.listener = null;
        }


        public static void StartRecord(NetType netType)
        {
            switch (netType)
            {
                case NetType.None:
                    break;
                case NetType.Lobby:
                    break;
                case NetType.Game21:
                    GameConn21.Record();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(netType), netType, null);
            }
        }


        #region 线程

        private void PingThread(object obj)
        {
            var packet = new NetPacket
            {
                MsgId = MsgID.MSGID_PING,
                EvtType = EvtType.SendMessage
            };

            while (Ins != null)
            {
                try
                {
                    var sec = TimeOffset;
                    if (LobbyConn.IsConnected)
                    {
                        LobbyConn._Send(packet);
                    }
                    else
                    {
                        if (Const.CurrentScene == Const.LobbyScene)
                        {
                            RecvQue.Enqueue(new NetPacket {EvtType = EvtType.Close, NetType = NetType.Lobby});
                        }
                    }

                    if (GameConn21.IsConnected)
                    {
                        GameConn21._Send(packet);
                    }
                    else
                    {
                        if (Const.CurrentScene == Const.BlackjackScene)
                        {
                            RecvQue.Enqueue(new NetPacket {EvtType = EvtType.Close, NetType = NetType.Game21});
                        }
                    }
                }
                catch (Exception e)
                {
                    XLog.LogError(e);
                }

                Thread.Sleep(pingDiff * 1000);
            }
        }


        private static void SendThread(object o)
        {
            while (Ins != null)
            {
                try
                {
                    if (SendQue.IsEmpty)
                    {
                        Thread.Sleep(200);
                        continue;
                    }

                    if (SendQue.TryDequeue(out var packet))
                    {
                        if (packet.NetType == NetType.Lobby)
                        {
                            while (!LobbyConn.IsConnected)
                            {
                                Thread.Sleep(200);
                            }

                            LobbyConn._Send(packet);
                        }
                        else if (packet.NetType == NetType.Game21)
                        {
                            while (!GameConn21.IsConnected)
                            {
                                Thread.Sleep(200);
                            }

                            GameConn21._Send(packet);
                        }
                    }
                }
                catch (Exception e)
                {
                    XLog.LogError(e);
                }
            }

            XLog.Log("Send Thread Exit------------------->");
        }

        #endregion

        #region send

        public static void SendLobby(MsgID msgId, IExtensible msg)
        {
            SendQue.Enqueue(new NetPacket
            {
                Msg = msg,
                MsgId = msgId,
                NetType = NetType.Lobby,
                EvtType = EvtType.SendMessage
            });
        }

        public static void SendGame(NetType netType, MsgID msgId, IExtensible msg)
        {
            SendQue.Enqueue(new NetPacket
            {
                Msg = msg,
                MsgId = msgId,
                NetType = netType,
                EvtType = EvtType.SendMessage
            });
        }

        #endregion


        #region httpsupport

        public struct ReqWork
        {
            public string url;
            public IDictionary<string, object> dic;
            public Action<JObject, bool> callback;
        }

        public static void DoReqWork(IEnumerable<ReqWork> handles)
        {
            Ins.StartCoroutine(_DoReqWork(handles));
        }

        public static IEnumerator _DoReqWork(IEnumerable<ReqWork> handles)
        {
            LoadingObj.Show();
            foreach (var h in handles)
            {
                var dic = h.dic != null
                    ? new Dictionary<string, object>(h.dic)
                    : new Dictionary<string, object>();

                XLog.Log($"PostInGame      {Const.LoginType}");
                dic.Add("TOKEN", Const.Token);
                dic.Add("USRID", Const.Uid.ToString());
                dic.Add("LAN", Const.Lan);
                var json = new JObject();
                foreach (var kv in dic)
                {
                    json.Add(new JProperty(kv.Key, kv.Value));
                }

                yield return Post(h.url, json.ToString(), h.callback);
            }

            LoadingObj.Hide();
        }

        public static void Req(string url, IDictionary<string, object> dic,
            Action<JObject, bool> callback)
        {
            var self = Ins.listener;
            if (dic == null)
            {
                dic = new Dictionary<string, object>();
            }

            XLog.Log($"PostInGame      {Const.LoginType}");
            dic.Add("TOKEN", Const.Token);
            dic.Add("UID", Const.Uid);
            dic.Add("LAN", Const.Lan);
            var json = new JObject();
            foreach (var kv in dic)
            {
                json.Add(new JProperty(kv.Key, kv.Value));
            }

            self.StartCoroutine(Post(url, json.ToString(),
                (obj, success) =>
                {
                    callback(obj, success && obj["success"] != null && obj["success"].Value<bool>());
                }));
        }


        public static IEnumerator Post(string url, string postData, Action<JObject, bool> callback)
        {
            url = Const.WebHost + "/api/" + url + "/";
            XLog.Log("POST:" + url, XLog.LogColor.Blue);
            using (var www = UnityWebRequest.Post(url, postData))
            {
                yield return www.SendWebRequest();
                if (www.isHttpError || www.isNetworkError)
                {
                    callback(null, false);
                }
                else
                {
                    if (callback == null) yield break;
                    var json = JObject.Parse(www.downloadHandler.text);
                    XLog.Log(url + "--->JSON:" + json);
                    callback(json, true);
                }
            }
        }


        public static bool CheckError(JObject obj, bool ok)
        {
            if (!ok)
            {
                TipObj.Show(obj?["text"] != null ? obj["text"].Value<string>() : Lan.NetWorkError);
            }

            return ok;
        }

        public static IEnumerator GetIp(Action<string, bool> callback)
        {
            var url = $"http://whois.pconline.com.cn/ipJson.jsp?ip={Const.Ip}&json=true";
            XLog.Log("POST GET IP:" + url, XLog.LogColor.Blue);
            using (var www = UnityWebRequest.Get(url))
            {
                yield return www.SendWebRequest();
                if (www.isHttpError || www.isNetworkError)
                {
                    callback(null, false);
                }
                else
                {
                    if (callback == null) yield break;
                    var json = JObject.Parse(www.downloadHandler.text);
                    XLog.Log(url + "--->JSON:" + json);
                    callback(json["addr"].Value<string>(), true);
                }
            }
        }

        #endregion
    }
}