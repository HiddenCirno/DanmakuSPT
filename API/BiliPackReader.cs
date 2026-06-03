using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

namespace BiliAPI
{
    class BiliPackReader
    {
        public enum PackTypes
        {
            Unknow = -1,
            Popularity = 3,
            Command = 5,
            Heartbeat = 8
        }

        public interface IPack
        {
            PackTypes PackType { get; }
        }

        public class PopularityPack : IPack
        {
            public PackTypes PackType => PackTypes.Popularity;
            public long Popularity { get; private set; }

            public PopularityPack(byte[] payload)
            {
                Popularity = BitConverter.ToUInt32(payload.Take(4).Reverse().ToArray(), 0);
            }
        }

        public class CommandPack : IPack
        {
            public PackTypes PackType => PackTypes.Command;
            public JToken Value { get; private set; }

            public CommandPack(byte[] payload)
            {
                string jstr = Encoding.UTF8.GetString(payload, 0, payload.Length);
                Value = JToken.Parse(jstr);
            }
        }

        public class HeartbeatPack : IPack
        {
            public PackTypes PackType => PackTypes.Heartbeat;
            public HeartbeatPack(byte[] payload) { }
        }

        private enum DataTypes
        {
            Unknow = -1,
            Plain = 0,
            Bin = 1,
            Gz = 2,
            Brotli = 3 // 支持 Brotli
        }

        // 我们不再把 BaseStream 当作一次性的碗，而是当作一个持久的蓄水池
        public MemoryStream DataBuffer { get; private set; }
        public ClientWebSocket BaseWebSocket { get; private set; }

        public BiliPackReader(Stream stream)
        {
            // 对于 TcpClient，传进来的直接就是网络流
            // 注意：这种写法在重构后主要服务于 WSS，如果用 TCP 可能需要额外处理
            // 但因为你指定了 Wss 协议，所以这里不会受到影响。
        }

        public BiliPackReader(ClientWebSocket webSocket)
        {
            BaseWebSocket = webSocket;
            DataBuffer = new MemoryStream();
        }

        // 这个方法现在只负责解包（从蓄水池或者解压后的流里读数据）
        // 不再负责接收 WebSocket 网络数据
        private IPack[] ExtractPacksFromStream(Stream stream)
        {
            List<IPack> packs = new List<IPack>();

            while (stream.Position < stream.Length)
            {
                long currentPos = stream.Position;
                long remaining = stream.Length - currentPos;

                // 如果连包头 (16字节) 都读不够，说明遇到了半包，跳出循环等下次
                if (remaining < 16)
                    break;

                // 尝试读取包长度 (不移动指针，只偷看一眼)
                byte[] lengthBuffer = new byte[4];
                stream.Read(lengthBuffer, 0, 4);
                stream.Position = currentPos; // 拨回指针

                int packLength = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBuffer, 0));

                // 如果整个包还没到齐，跳出循环等下次数据
                if (remaining < packLength)
                    break;

                // --- 开始正式拆解一个完整的包 ---

                stream.Position += 4; // 跳过包长度

                byte[] headerLengthBuffer = new byte[2];
                stream.Read(headerLengthBuffer, 0, 2);
                int headerLength = IPAddress.NetworkToHostOrder(BitConverter.ToInt16(headerLengthBuffer, 0));

                byte[] dataTypeBuffer = new byte[2];
                stream.Read(dataTypeBuffer, 0, 2);
                int dataTypeCode = IPAddress.NetworkToHostOrder(BitConverter.ToInt16(dataTypeBuffer, 0));
                DataTypes dataType = Enum.IsDefined(typeof(DataTypes), dataTypeCode) ? (DataTypes)dataTypeCode : DataTypes.Unknow;

                byte[] packTypeBuffer = new byte[4];
                stream.Read(packTypeBuffer, 0, 4);
                int packTypeCode = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(packTypeBuffer, 0));
                PackTypes packType = Enum.IsDefined(typeof(PackTypes), packTypeCode) ? (PackTypes)packTypeCode : PackTypes.Unknow;

                stream.Position += 4; // 跳过 Split

                int payloadLength = packLength - headerLength;
                byte[] payloadBuffer = new byte[payloadLength];
                stream.Read(payloadBuffer, 0, payloadLength);

                // 根据类型处理负载
                switch (dataType)
                {
                    case DataTypes.Plain:
                        if (packType == PackTypes.Command)
                            packs.Add(new CommandPack(payloadBuffer));  
                        break;
                    case DataTypes.Bin:
                        if (packType == PackTypes.Popularity)
                            packs.Add(new PopularityPack(payloadBuffer));
                        else if (packType == PackTypes.Heartbeat)
                            packs.Add(new HeartbeatPack(payloadBuffer));
                        break;
                    case DataTypes.Gz:
                        packs.AddRange(DecompressPacks(payloadBuffer, new DeflateStream(new MemoryStream(payloadBuffer, 2, payloadBuffer.Length - 2), CompressionMode.Decompress)));
                        break;
                    case DataTypes.Brotli:
                        packs.AddRange(DecompressPacks(payloadBuffer, new BrotliStream(new MemoryStream(payloadBuffer), CompressionMode.Decompress)));
                        break;
                }
            }
            return packs.ToArray();
        }

        // 获取并处理网络数据的主入口
        public IPack[] ReadPacksAsync()
        {
            if (BaseWebSocket == null) return new IPack[0];

            ArraySegment<byte> receiveBuffer = new ArraySegment<byte>(new byte[8192]);
            WebSocketReceiveResult result;

            try
            {
                // 1. 将新收到的数据灌入蓄水池 (尾部追加)
                DataBuffer.Position = DataBuffer.Length;
                do
                {
                    result = BaseWebSocket.ReceiveAsync(receiveBuffer, CancellationToken.None).GetAwaiter().GetResult();
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw new IOException("WebSocket 连接被服务器关闭");
                    }
                    DataBuffer.Write(receiveBuffer.Array, 0, result.Count);
                } while (!result.EndOfMessage);

                // 2. 将指针拨回读取起点，开始尝试拆包
                DataBuffer.Position = 0;
                IPack[] extractedPacks = ExtractPacksFromStream(DataBuffer);

                // 3. 整理蓄水池 (极其关键的一步！)
                // 如果池子里还有没处理完的半个包，把它们移到最前面
                long unreadLength = DataBuffer.Length - DataBuffer.Position;
                if (unreadLength > 0)
                {
                    byte[] leftover = new byte[unreadLength];
                    DataBuffer.Read(leftover, 0, (int)unreadLength);
                    DataBuffer.SetLength(0); // 清空
                    DataBuffer.Write(leftover, 0, leftover.Length); // 塞回去
                }
                else
                {
                    // 全处理完了，清空池子
                    DataBuffer.SetLength(0);
                }

                return extractedPacks;
            }
            catch (Exception)
            {
                // 发生异常时，为了防止死锁或坏数据残留，清空蓄水池
                DataBuffer.SetLength(0);
                throw;
            }
        }

        private IPack[] DecompressPacks(byte[] compressedBuffer, Stream decompressionStream)
        {
            using (decompressionStream)
            {
                using (MemoryStream decompressedStream = new MemoryStream())
                {
                    decompressionStream.CopyTo(decompressedStream);
                    decompressedStream.Position = 0;
                    // 解压出来的数据，直接用我们刚才写好的方法再拆分一次
                    return ExtractPacksFromStream(decompressedStream);
                }
            }
        }
    }
}