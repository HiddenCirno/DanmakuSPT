using BepInEx;
using BepInEx.Configuration;
using BiliAPI;
using EFT;
using Sirenix.Serialization;
using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using static DanmakuSPT.DanmakuUI;
using Comfort.Common;

namespace DanmakuSPT
{
    [BepInPlugin(PluginsInfo.GUID, PluginsInfo.NAME, PluginsInfo.VERSION)]
    public class PluginsCore : BaseUnityPlugin
    {
        private BiliLiveListener _biliListener;
        private ConfigEntry<uint> _roomIdConfig;
        private ConfigEntry<string> _biliCookieConfig;

        private static readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();

        // 【新增】防僵尸标志位：用来区分是“网络意外断开”还是“玩家主动改配置/关游戏”
        private bool _isIntentionalDisconnect = false;

        public void Awake()
        {
            _roomIdConfig = Config.Bind("1. 直播设置", "Room ID", (uint)210231, "你要监听的B站直播间长房间号");
            _biliCookieConfig = Config.Bind("1. 直播设置", "Cookie", "", "你的B站真实Cookie (包含SESSDATA和DedeUserID)");

            // 【关键修改】监听配置项的修改事件！一旦玩家在 F12 里改了并保存，就会触发 OnConfigChanged
            _roomIdConfig.SettingChanged += OnConfigChanged;
            _biliCookieConfig.SettingChanged += OnConfigChanged;
            var harmony = new Harmony(PluginsInfo.GUID);
            Logger.LogInfo("塔科夫弹幕姬插件已加载！");
        }

        public void Start()
        {
            // 游戏启动时，执行第一次连接
            InitializeAndConnect();
        }

        // 【新增】配置被修改时的回调逻辑
        private void OnConfigChanged(object sender, EventArgs e)
        {
            Logger.LogInfo("[BiliLive] 检测到房间号或Cookie配置已更改，准备重新连接...");

            // 1. 设置主动断开标志位，防止触发“5秒自动重连”
            _isIntentionalDisconnect = true;

            // 2. 彻底销毁旧的监听器
            if (_biliListener != null)
            {
                _biliListener.Disconnect();
                _biliListener = null; // 释放引用，交给垃圾回收
            }

            // 3. 恢复标志位，使用新配置重新初始化
            _isIntentionalDisconnect = false;
            InitializeAndConnect();
        }

        // 【新增】把初始化和连接逻辑单独抽出来，方便被 Start 和 OnConfigChanged 复用
        private void InitializeAndConnect()
        {
            Logger.LogInfo($"[BiliLive] 正在连接房间: {_roomIdConfig.Value}...");

            _biliListener = new BiliLiveListener(_roomIdConfig.Value, BiliLiveListener.Protocols.Wss, _biliCookieConfig.Value);

            _biliListener.Connected += () => Logger.LogInfo("[BiliLive] 成功连接到弹幕服务器！");

            _biliListener.ConnectionFailed += (msg) =>
            {
                // 【核心防护】如果是我们主动换房间导致的断开，直接 Return，不走重连逻辑
                if (_isIntentionalDisconnect) return;

                Logger.LogError($"[BiliLive] 连接失败/断开: {msg}");

                _mainThreadActions.Enqueue(() =>
                {
                    Logger.LogInfo("[BiliLive] 5 秒后尝试重新连接...");
                    System.Threading.Tasks.Task.Delay(5000).ContinueWith(_ =>
                    {
                        // 在 5 秒倒计时结束时，再次检查标志位和实例状态
                        if (!_isIntentionalDisconnect && _biliListener != null)
                        {
                            _biliListener.Connect();
                        }
                    });
                });
            };

            _biliListener.ItemsRecieved += OnBiliItemsReceived;
            _biliListener.Connect();
        }

        private void OnBiliItemsReceived(BiliLiveJsonParser.IItem[] items)
        {
            foreach (var item in items)
            {
                if (item is BiliLiveJsonParser.Danmaku danmaku)
                {
                    Logger.LogInfo($"[弹幕] {danmaku.Sender.Name}: {danmaku.Message}");

                    _mainThreadActions.Enqueue(() =>
                    {
                        DanmakuUI.ShowDanmaku(danmaku);
                        //var scObj = DanmakuUI.ShowTestChat(danmaku);
                    });
                }
                else if (item is BiliLiveJsonParser.Gift gift)
                {
                    Logger.LogInfo($"[礼物] {gift.Sender.Name} 送出了 {gift.GiftName} x{gift.Number}");

                    _mainThreadActions.Enqueue(() =>
                    {
                        DanmakuUI.ShowGift(gift);
                    });
                }
                else if (item is BiliLiveJsonParser.SuperChat sc)
                {
                    Logger.LogInfo($"[SC] {sc.User.Name} (￥{sc.Price}): {sc.Message}");

                    _mainThreadActions.Enqueue(() =>
                    {
                        DanmakuUI.ShowSuperChat(sc);
                    });
                }
            }
        }

        public void Update()
        {
            while (_mainThreadActions.TryDequeue(out var action))
            {
                try { action?.Invoke(); }
                catch (Exception ex) { Logger.LogError($"执行主线程 UI 任务时出错: {ex}"); }
            }
        }

        private void OnDestroy()
        {
            // 游戏退出或插件卸载时，也是“主动断开”
            _isIntentionalDisconnect = true;

            if (_biliListener != null)
            {
                _biliListener.Disconnect();
                Logger.LogInfo("[BiliLive] 插件销毁，已断开连接。");
            }
        }
    }
}