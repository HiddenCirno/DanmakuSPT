using Newtonsoft.Json.Linq; // 引入 Linq 命名空间以使用 JToken
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;

namespace BiliAPI
{
    public class BiliLiveJsonParser
    {
        public enum Cmds
        {
            UNKNOW,
            LIVE,
            PREPARING,
            DANMU_MSG,
            SEND_GIFT,
            SPECIAL_GIFT,
            USER_TOAST_MSG,
            GUARD_MSG,
            GUARD_BUY,
            GUARD_LOTTERY_START,
            WELCOME,
            WELCOME_GUARD,
            ENTRY_EFFECT,
            SYS_MSG,
            ROOM_BLOCK_MSG,
            COMBO_SEND,
            ROOM_RANK,
            TV_START,
            NOTICE_MSG,
            SYS_GIFT,
            ROOM_REAL_TIME_MESSAGE_UPDATE,
            SUPER_CHAT_ENTRANCE,
            SUPER_CHAT_MESSAGE,
            SUPER_CHAT_MESSAGE_DELETE,
            INTERACT_WORD
        }

        [Serializable]
        public class User
        {
            public long Id;
            public string Name;

            // 【新增】粉丝牌信息
            public long MedalLevel;
            public string MedalName;

            // 【新增】大航海等级 (0: 无, 1: 总督, 2: 提督, 3: 舰长)
            public long GuardLevel;

            // 【新增】用户等级 (User Level)
            public long UserLevel;

            public User(long id, string name)
            {
                Id = id;
                Name = name;
                // 初始化默认值
                MedalLevel = 0;
                MedalName = "";
                GuardLevel = 0;
                UserLevel = 0;
            }
        }

        public interface IItem
        {
            Cmds Cmd { get; }
        }

        public interface ITimeStampedItem : IItem
        {
            DateTime TimeStamp { get; }
        }


        [Serializable]
        public class Raw : IItem
        {
            public Cmds Cmd { get; }
            public JToken Value { get; }

            public Raw(Cmds cmd, JToken value)
            {
                Cmd = cmd;
                Value = value;
            }
        }

        [Serializable]
        public class Danmaku : ITimeStampedItem
        {
            public Cmds Cmd => Cmds.DANMU_MSG;
            public DateTime TimeStamp { get; private set; }

            public User Sender { get; private set; }
            public string Message { get; private set; }
            public long Type { get; private set; }

            public Danmaku(JToken json)
            {
                // 1. 基础信息解析
                Sender = new User((long)json["info"][2][0], Regex.Unescape((string)json["info"][2][1]));

                string rawMessage = (string)json["info"][1];
                try
                {
                    Message = Regex.Unescape(rawMessage);
                }
                catch (Exception)
                {
                    Message = rawMessage;
                }

                Type = (long)json["info"][0][9];
                TimeStamp = new DateTime(1970, 01, 01).AddMilliseconds((double)json["info"][0][4]);

                // ----------------------------------------------------
                // 2. 【新增】深度解析：提取粉丝牌 (info[3])
                // ----------------------------------------------------
                try
                {
                    JToken medalInfo = json["info"][3];
                    // 如果数组里有数据，说明佩戴了粉丝牌
                    if (medalInfo != null && medalInfo.HasValues && medalInfo.Type == JTokenType.Array && medalInfo.Count() >= 2)
                    {
                        Sender.MedalLevel = (long)medalInfo[0];
                        Sender.MedalName = (string)medalInfo[1];
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[警告] 粉丝牌解析失败: {ex.Message}"); }

                // ----------------------------------------------------
                // 3. 【新增】深度解析：提取用户等级 (info[4])
                // ----------------------------------------------------
                try
                {
                    JToken levelInfo = json["info"][4];
                    if (levelInfo != null && levelInfo.HasValues && levelInfo.Type == JTokenType.Array)
                    {
                        Sender.UserLevel = (long)levelInfo[0];
                    }
                }
                catch (Exception) { }

                // ----------------------------------------------------
                // 4. 【新增】深度解析：提取大航海舰队等级 (info[7])
                // ----------------------------------------------------
                try
                {
                    JToken guardInfo = json["info"][7];
                    if (guardInfo != null && guardInfo.Type != JTokenType.Null)
                    {
                        Sender.GuardLevel = (long)guardInfo;
                    }
                }
                catch (Exception) { }
            }
        }

        [Serializable]
        public class SuperChat : ITimeStampedItem
        {
            public Cmds Cmd => Cmds.SUPER_CHAT_MESSAGE;
            public DateTime TimeStamp { get; private set; }

            public long Price { get; private set; }
            public string Message { get; private set; }
            public bool TransMark { get; private set; }
            public string MessageTrans { get; private set; }
            public Color BackgroundColor { get; private set; }
            public Color PriceColor { get; private set; }
            public Color BottomColor { get; private set; }
            public User User { get; private set; }
            public string Face { get; private set; }
            public TimeSpan Duration { get; private set; }

            public SuperChat(JToken json)
            {
                var colorConverter = new ColorConverter();
                TimeStamp = new DateTime(1970, 01, 01).AddMilliseconds((double)json["data"]["ts"]);

                Price = (long)json["data"]["price"];

                string rawMessage = (string)json["data"]["message"];
                try
                {
                    Message = Regex.Unescape(rawMessage);
                }
                catch (Exception)
                {
                    Message = rawMessage;
                }

                TransMark = (int)json["data"]["trans_mark"] != 0;
                MessageTrans = (string)json["data"]["message_trans"];
                BackgroundColor = (Color)colorConverter.ConvertFromString((string)json["data"]["background_color"]);
                PriceColor = (Color)colorConverter.ConvertFromString((string)json["data"]["background_price_color"]);
                BottomColor = (Color)colorConverter.ConvertFromString((string)json["data"]["background_bottom_color"]);
                User = new User((long)json["data"]["uid"], Regex.Unescape((string)json["data"]["user_info"]["uname"]));
                Face = (string)json["data"]["user_info"]["face"];
                Duration = TimeSpan.FromSeconds((double)json["data"]["time"]);
            }
        }

        [Serializable]
        public class Gift : ITimeStampedItem
        {
            public Cmds Cmd => Cmds.SEND_GIFT;
            public DateTime TimeStamp { get; private set; }

            public string GiftName { get; private set; }
            public long Number { get; private set; }
            public User Sender { get; private set; }
            public string FaceUri { get; private set; }
            public long GiftId { get; private set; }
            public string Action { get; private set; }
            public string CoinType { get; private set; }

            public Gift(JToken json)
            {
                GiftName = Regex.Unescape((string)json["data"]["giftName"]);
                Number = (long)json["data"]["num"];
                Sender = new User((long)json["data"]["uid"], Regex.Unescape((string)json["data"]["uname"]));
                FaceUri = (string)json["data"]["face"];
                GiftId = (long)json["data"]["giftId"];
                Action = (string)json["data"]["action"];
                CoinType = (string)json["data"]["coin_type"];

                TimeStamp = new DateTime(1970, 01, 01).AddSeconds((double)json["data"]["timestamp"]);
            }
        }

        [Serializable]
        public class ComboSend : IItem
        {
            public Cmds Cmd => Cmds.COMBO_SEND;

            public User Sender { get; private set; }
            public string GiftName { get; private set; }
            public long Number { get; private set; }
            public long GiftId { get; private set; }
            public string Action { get; private set; }

            public ComboSend(JToken json)
            {
                Sender = new User((long)json["data"]["uid"], Regex.Unescape((string)json["data"]["uname"]));
                GiftName = Regex.Unescape((string)json["data"]["gift_name"]);
                Number = (long)json["data"]["total_num"];
                GiftId = (long)json["data"]["gift_id"];
                Action = (string)json["data"]["action"];
            }
        }

        [Serializable]
        public class Welcome : IItem
        {
            public Cmds Cmd => Cmds.WELCOME;

            public User User { get; private set; }
            public bool Svip { get; private set; }

            public Welcome(JToken json)
            {
                User = new User((long)json["data"]["uid"], Regex.Unescape((string)json["data"]["uname"]));
                Svip = (int)json["data"]["svip"] != 0;
            }
        }

        [Serializable]
        public class WelcomeGuard : IItem
        {
            public Cmds Cmd => Cmds.WELCOME_GUARD;

            public User User { get; private set; }
            public long GuardLevel { get; private set; }

            public WelcomeGuard(JToken json)
            {
                User = new User((long)json["data"]["uid"], Regex.Unescape((string)json["data"]["username"]));
                GuardLevel = (long)json["data"]["guard_level"];
            }
        }

        [Serializable]
        public class InteractWord : ITimeStampedItem
        {
            public enum Identities
            {
                Unknown = 0,
                Normal,
                Manager,
                Fans,
                Vip,
                SVip,
                GuardJian,
                GuardTi,
                GuardZong
            }

            public enum MessageTypes
            {
                Unknown = 0,
                Entry,
                Attention,
                Share,
                SpecialAttention,
                MutualAttention
            }

            public Cmds Cmd => Cmds.INTERACT_WORD;
            public DateTime TimeStamp { get; private set; }

            public User User { get; private set; }
            public ICollection<Identities> Identity { get; private set; }
            public MessageTypes MessageType { get; private set; }

            public InteractWord(JToken json)
            {
                User = new User((long)json["data"]["uid"], Regex.Unescape((string)json["data"]["uname"]));

                List<Identities> identities = new List<Identities>();
                foreach (JToken i in json["data"]["identities"])
                {
                    switch ((int)i)
                    {
                        case 1: identities.Add(Identities.Normal); break;
                        case 2: identities.Add(Identities.Manager); break;
                        case 3: identities.Add(Identities.Fans); break;
                        case 4: identities.Add(Identities.Vip); break;
                        case 5: identities.Add(Identities.SVip); break;
                        case 6: identities.Add(Identities.GuardJian); break;
                        case 7: identities.Add(Identities.GuardTi); break;
                        case 8: identities.Add(Identities.GuardZong); break;
                        default: identities.Add(Identities.Unknown); break;
                    }
                }
                Identity = identities.ToArray();

                int msgTypeId = (int)json["data"]["msg_type"];
                switch (msgTypeId)
                {
                    case 1: MessageType = MessageTypes.Entry; break;
                    case 2: MessageType = MessageTypes.Attention; break;
                    case 3: MessageType = MessageTypes.Share; break;
                    case 4: MessageType = MessageTypes.SpecialAttention; break;
                    case 5: MessageType = MessageTypes.MutualAttention; break;
                    default: MessageType = MessageTypes.Unknown; break;
                }

                TimeStamp = new DateTime(1970, 01, 01).AddSeconds((double)json["data"]["timestamp"]);
            }
        }

        [Serializable]
        public class RoomBlock : IItem
        {
            public Cmds Cmd => Cmds.ROOM_BLOCK_MSG;

            public User User { get; private set; }
            public long Operator { get; private set; }

            public RoomBlock(JToken json)
            {
                User = new User((long)json["data"]["uid"], Regex.Unescape((string)json["data"]["uname"]));
                Operator = (long)json["data"]["operator"];
            }
        }

        [Serializable]
        public class GuardBuy : ITimeStampedItem
        {
            public Cmds Cmd => Cmds.GUARD_BUY;
            public DateTime TimeStamp { get; private set; }

            public User User { get; private set; }
            public long GuardLevel { get; private set; }
            public string GiftName { get; private set; }

            public GuardBuy(JToken json)
            {
                User = new User((long)json["data"]["uid"], Regex.Unescape((string)json["data"]["username"]));
                GuardLevel = (long)json["data"]["guard_level"];
                GiftName = (string)json["data"]["gift_name"];

                TimeStamp = new DateTime(1970, 01, 01).AddSeconds((double)json["data"]["start_time"]);
            }
        }

        // 新增的便利方法：直接从字符串解析
        public static IItem Parse(string jsonString)
        {
            try
            {
                JToken json = JToken.Parse(jsonString);
                return Parse(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JSON Parse Error]: {ex.Message}");
                return null;
            }
        }

        public static IItem Parse(JToken json)
        {
            // 如果需要调试打印，JToken 提供了原生支持
            // Console.WriteLine(json.ToString(Newtonsoft.Json.Formatting.None)); 
            try
            {
                string cmdRaw = (string)json["cmd"];
                if (string.IsNullOrEmpty(cmdRaw))
                {
                    return new Raw(Cmds.UNKNOW, json);
                }

                string[] cmd = cmdRaw.Split(':');
                switch (cmd[0])
                {
                    case "DANMU_MSG":
                        return new Danmaku(json);
                    case "SUPER_CHAT_MESSAGE":
                        return new SuperChat(json);
                    case "SEND_GIFT":
                        return new Gift(json);
                    case "COMBO_SEND":
                        return new ComboSend(json);
                    case "WELCOME":
                        return new Welcome(json);
                    case "WELCOME_GUARD":
                        return new WelcomeGuard(json);
                    case "GUARD_BUY":
                        return new GuardBuy(json);
                    case "INTERACT_WORD":
                        return new InteractWord(json);
                    case "ROOM_BLOCK_MSG":
                        return new RoomBlock(json);
                    case "LIVE":
                    case "PREPARING":
                    case "SPECIAL_GIFT":
                    case "USER_TOAST_MSG":
                    case "GUARD_MSG":
                    case "GUARD_LOTTERY_START":
                    case "ENTRY_EFFECT":
                    case "SYS_MSG":
                    case "ROOM_RANK":
                    case "TV_START":
                    case "NOTICE_MSG":
                    case "SYS_GIFT":
                    case "ROOM_REAL_TIME_MESSAGE_UPDATE":
                    case "SUPER_CHAT_ENTRANCE":
                    case "SUPER_CHAT_MESSAGE_DELETE":
                        // 优化枚举解析，防止未记录的新类型导致程序崩溃
                        if (Enum.TryParse<Cmds>(cmd[0], out var parsedCmd))
                        {
                            return new Raw(parsedCmd, json);
                        }
                        return new Raw(Cmds.UNKNOW, json);
                    default:
                        return new Raw(Cmds.UNKNOW, json);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }
    }
}