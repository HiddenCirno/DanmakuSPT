using BiliAPI;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.UI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DanmakuSPT
{
    public static class DanmakuUI
    {
        public static class SCColor
        {
            // 定义一个私有的转换器
            public static Color CNY_30 => FromHex("#2a60b2");
            public static Color CNY_50 => FromHex("#427d9e");
            public static Color CNY_100 => FromHex("#e2b52b");
            public static Color CNY_500 => FromHex("#e09443");
            public static Color CNY_1000 => FromHex("#e54d4d");
            public static Color CNY_2000 => FromHex("#ab1a32");
        }
        public static class MedalColor
        {
            public static Color Tier0 => FromHex("#5c968e");
            public static Color Tier1 => FromHex("#5d7b9e");
            public static Color Tier2 => FromHex("#8d7ca6");
            public static Color Tier3 => FromHex("#be6686");
            public static Color Tier4 => FromHex("#c79d24");
            public static Color Tier5 => FromHex("#2c6c62");
            public static Color Tier6 => FromHex("#122360");
            public static Color Tier7 => FromHex("#3c1b6c");
            public static Color Tier8 => FromHex("#891537");
            public static Color Tier9 => FromHex("#ff7924");
        }
        // 继承塔科夫的通知基类
        public class BiliNotification : NotificationAbstractClass
        {
            private string _description;
            private ENotificationIconType _icon;
            private Color? _textColor;
            private Color? _backgroundColor;

            // 必须实现的抽象属性
            public override string Description => _description;

            // 重写虚属性
            public override ENotificationIconType Icon => _icon;
            public override Color? TextColor => _textColor;
            public override Color? BackgroundColor => _backgroundColor;

            // 构造函数
            public BiliNotification(
                string description,
                ENotificationDurationType duration,
                ENotificationIconType icon = ENotificationIconType.Default,
                Color? textColor = null,
                Color? backgroundColor = null)
            {
                _description = description;
                Duration = duration;
                _icon = icon;
                _textColor = textColor;
                _backgroundColor = backgroundColor;
            }
        }
        private static Color FromHex(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out Color color);
            return color;
        }
        public static void ShowSuperChat(BiliLiveJsonParser.SuperChat sc)
        {
            string formattedMessage = $"<b>【SC ￥{sc.Price}】 {sc.User.Name}</b>\n{sc.Message}";
            var price = sc.Price;
            var color = Color.white;
            switch (price)
            {
                case 2000:
                    {
                        color = SCColor.CNY_2000;
                    }
                    break;
                case 1000:
                    {
                        color = SCColor.CNY_1000;
                    }
                    break;
                case 500:
                    {
                        color = SCColor.CNY_500;
                    }
                    break;
                case 100:
                    {
                        color = SCColor.CNY_100;
                    }
                    break;
                case 50:
                    {
                        color = SCColor.CNY_50;
                    }
                    break;
                case 30:
                    {
                        color = SCColor.CNY_30;
                    }
                    break;
            }
            // 使用我们之前继承的自定义类，设置背景色和 Infinite 周期
            var customSC = new BiliNotification(
                formattedMessage,
                ENotificationDurationType.Infinite,
                ENotificationIconType.Quest,
                color,
                null
            );
            NotificationManagerClass.DisplayNotification(customSC);
        }

        public static Color GetMedalColor(long level)
        {
            if (level > 36)
            {
                return MedalColor.Tier9;
            }
            else if (level > 32)
            {
                return MedalColor.Tier8;
            }
            else if (level > 28)
            {
                return MedalColor.Tier7;
            }
            else if (level > 24)
            {
                return MedalColor.Tier6;
            }
            else if (level > 20)
            {
                return MedalColor.Tier5;
            }
            else if (level > 16)
            {
                return MedalColor.Tier4;
            }
            else if (level > 12)
            {
                return MedalColor.Tier3;
            }
            else if (level > 8)
            {
                return MedalColor.Tier2;
            }
            else if (level > 4)
            {
                return MedalColor.Tier1;
            }
            else
            {
                return MedalColor.Tier0;
            }
        }


        // 渲染普通弹幕
        public static void ShowDanmaku(BiliLiveJsonParser.Danmaku danmaku)
        {
            string prefix = "";

            // 1. 舰队前缀判断 (1总督, 2提督, 3舰长)
            if (danmaku.Sender.GuardLevel == 3)
            {
                prefix += $"<b><color=#4464e9>[舰长]</color></b> ";
            }
            else if (danmaku.Sender.GuardLevel == 2)
            {
                prefix += $"<b><color=#9819e2>[提督]</color></b> ";
            }
            else if (danmaku.Sender.GuardLevel == 1)
            {
                prefix += $"<b><color=#FF5300>[总督]</color></b> ";
            }

            // 2. 粉丝牌判断
            if (danmaku.Sender.MedalLevel > 0)
            {
                // 塔科夫 UI 里加个小牌子，比如: [纸鸢|21]
                prefix += $"<color=#{ColorUtility.ToHtmlStringRGB(GetMedalColor(danmaku.Sender.MedalLevel))}>[{danmaku.Sender.MedalName}|{danmaku.Sender.MedalLevel}]</color> ";
            }

            // 3. 最终组合！
            // 格式: [舰长] [纸鸢|21] 用户名: 弹幕内容
            string formattedMessage = $"{prefix}<b><color=#C0C0C0>{danmaku.Sender.Name}</color></b>: {danmaku.Message}";

            NotificationManagerClass.DisplayMessageNotification(
                formattedMessage,
                ENotificationDurationType.Default,
                ENotificationIconType.Friend,
                null
            );
        }

        // 渲染礼物
        public static void ShowGift(BiliLiveJsonParser.Gift gift)
        {
            // 礼物通知用橙色高亮名字，金色高亮礼物名
            string formattedMessage = $"<b><color=#FFD700>{gift.Sender.Name}</color></b> 送出了 <color=#FFD700>{gift.GiftName}</color> x{gift.Number}";

            NotificationManagerClass.DisplayMessageNotification(
                formattedMessage,
                ENotificationDurationType.Long, // 礼物可以考虑用 Long 增加停留时间
                ENotificationIconType.WishlistOther,       // 给个显眼的图标
                new Color(1f, 0.84f, 0f)           // 整体文字基调偏金黄
            );
        }
    }
}