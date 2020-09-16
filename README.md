# unitywebsocket
unity socket webcoket code using in my game free best http  
this code using in my game  include uniyt websocket client and socket client  
using code like blow  
you can modify code and using in your own project  

``` C#
        private void send_login_message(string openid, string passporttype)
        {

            StartCoroutine(NetClient.Connect(NetType.Lobby, (ok) =>
            {
                if (ok)
                {
                    XLog.Log("login---->openid:" + Const.OpenId);
                    NetClient.SendLobby(MsgID.MSGID_LOGIN_REQ, new CGameLoginReq
                    {
                        szloginType = passporttype,
                        szloginID = openid,
                    });
                }
            }));
        }
        
    # base scene each scenen extend this  NetClient will recv message then call  OnGameMessage or OnLobbyMessage
    
    public interface IAccountRefreshListener
    {
        void OnAccountRefresh(bool ok);
    }
    
    public abstract class BaseScene : MonoBehaviour, IAccountRefreshListener
    {
        public bool installLoading;
        public bool insallPayNoitfy;
        public bool installTip;
        public bool installBord;
        public bool installChat;
        public bool installNet;
        public bool isInGame;
        public bool accountRefresh;
        public GameObject poproot;
        
        public bool isConnectGame;
        public bool isConnectLobby;


        public GameSetting gameSetting;
        public string sceneName;
        public static BaseScene Ins { get; private set; }
        public List<BasePopWin> popWins = new List<BasePopWin>();
    
        public T FindPopWin<T>() where T : BasePopWin
        {
            foreach (var o in popWins)
            {
                if (o is T win)
                {
                    return win;
                }
            }
            return null;
        }

        protected void ChangeScene(string target)
        {
            Const.TargetScene = target;
            SceneManager.LoadScene(Const.LoadingScene);
        }

        protected void Install()
        {
            Const.CurrentScene = sceneName;
            Ins = this;
            gameSetting.Install();
            if (poproot != null)
            {
                popWins.Clear();
                var cs = poproot.GetComponentsInChildren<BasePopWin>(true);
                foreach (var c in cs)
                {
                    popWins.Add(c);
                }
            }

            //加载各种插件
            if (NetClient.IsNull)
            {
                Instantiate(Res.LOAD_PB("net") as GameObject, null);
            }

            if (AudioManager.IsNull)
            {
                Instantiate(Res.LOAD_PB("sound") as GameObject, null);
            }

            if (installTip)
            {
                var tip = Res.LOAD_PB("tippool") as GameObject;
                var pb = Instantiate(tip, poproot.transform);
                TipObj.Install(pb.GetComponent<TipObj>(), installBord);

                var popAlert = Res.LOAD_PB("pop/pop_alert") as GameObject;
                pb = Instantiate(popAlert, poproot.transform);
                AlertObj.Install(pb.GetComponent<AlertObj>());
            }

            Const.IsInGame = isInGame;


            if (installLoading)
            {
                var loading = Res.LOAD_PB("loading") as GameObject;
                var pb = Instantiate(loading, poproot.transform);
                pb.gameObject.GetComponent<RectTransform>().offsetMax = new Vector2(0, 0);
                pb.gameObject.GetComponent<RectTransform>().offsetMin = new Vector2(0, 0);
                pb.gameObject.transform.localScale = new Vector3(1, 1, 1);
                LoadingObj.Install(pb.GetComponent<LoadingObj>());
            }

            if (installNet)
            {
                NetClient.Attach(this);
            }

            if (accountRefresh)
            {
                Account.RefreshListener.Add(this);
            }
        }

        void OnDestroy()
        {
            CleanUp();
            if (isConnectGame || isConnectLobby)
            {
                CancelInvoke($"CheckConnect");
            }

            if (installNet)
            {
                NetClient.DeAttach();
            }

            if (accountRefresh && Account.RefreshListener.Contains(this))
            {
                Account.RefreshListener.Remove(this);
            }
        }

        protected virtual void CleanUp()
        {
        }

        public virtual void OnLobbyMessage(NetPacket packet)
        {
            XLog.Log("OnLobbyMessage---->MsgID:" + packet.MsgId);
            if (installBord && packet.MsgId == MsgID.MSGID_BROADCAST_RES)
            {
                var msg = packet.Decode<ChatMessage>();
                var type = (AdType) msg.TYPE;
                var result = string.Empty;
                var usr = $"[{msg.NICK}(ID:{msg.TO_UID})]";
                string arg;
                switch (type)
                {
                    case AdType.AD_PAY:
                        arg = Convert.ToInt64(msg.CONTENT).ToString();
                        result = string.Format(Lan.AdPay, usr, arg);
                        break;
                    case AdType.AD_WIN_IN_21:
                        arg = (Convert.ToInt64(msg.CONTENT) / Lan.WAN).ToString("N0");
                        result = string.Format(Lan.AdWin21, usr, arg);
                        break;
                    case AdType.AD_WIN_IN_LOTTO_CHIPS:
                        arg = (Convert.ToInt64(msg.CONTENT) / Lan.WAN).ToString("N0");
                        result = string.Format(Lan.AdWinLotto, usr, arg);
                        break;
                    case AdType.AD_SYS:
                        break;
                }
                if (!string.IsNullOrEmpty(result))
                {
                    TipObj.Show(result, TipType.Bord);
                }
            }
            else if (installChat && packet.MsgId == MsgID.MSGID_P2P_MESSAGE_RES)
            {
                var msg = packet.Decode<ChatMessage>();
                var chatPop = FindPopWin<ChatPop>();
                if (chatPop.isActiveAndEnabled && chatPop.toUid == msg.TO_UID)
                {
                    chatPop.Recv(msg);
                }
            }
            else if (insallPayNoitfy && packet.MsgId == MsgID.MSGID_RECHARGEVER_RES)
            {
                var msg = packet.Decode<RechargeVerRes>();
                Const.Usr.Refresh(() =>
                {
                    FindPopWin<ShopPop>()?.BindData();
                });
                TipObj.Show(msg.stGetChips.iLongValue, () =>
                {
                    TipObj.Show(Lan.PaySuccess);
                });
                
            }
        }

        public virtual void OnGameMessage(NetPacket packet)
        {
            XLog.Log("OnGameMessage---->MsgID:" + packet.MsgId);
        }

        public virtual void OnConnect(NetType netType, bool ok)
        {
            XLog.Log("OnConnect---->netType:" + netType);
        }

        public virtual void OnClose(NetType netType)
        {
            XLog.Log("OnClose---->netType:" + netType);
            if ((netType == NetType.Lobby && isConnectLobby)
                || (netType == NetType.Game21 && isConnectGame))
            {
                AlertObj.ShowNetCut();
            }
        }


        

        public virtual void OnAccountRefresh(bool ok)
        {
            XLog.Log("player info is refreshed");
        }
    }
        
        
``` 

game url:https://play.google.com/store/apps/details?id=com.xuganggo.blackjack

![image](https://github.com/xugangjava/unitywebsocket/blob/master/image/logo.png)
