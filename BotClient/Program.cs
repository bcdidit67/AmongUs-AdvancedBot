using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace BotClient
{
    /// <summary>
    /// Among Us v19s 外置 bot 客户端 —— 持久连接版。
    ///
    /// 上一版的教训：服务端已经开始 Ping 保活（说明**连接已建立**），
    /// 但我们一个 Ping 都不回，于是被超时踢掉（收到 0x09 Disconnect）。
    ///
    /// 本版补上 Hazel 的保活应答：
    ///   Ping   (0x0C) → 回 Acknowledgement
    ///   Reliable(0x01) → 回 Acknowledgement
    ///
    /// Acknowledgement 格式： [0x0A][uint16 BE nonce][byte flags]
    ///
    /// 实测版本号（从运行中的游戏读出，必须匹配否则被拒）：
    ///   BroadcastVersion = 50663600  (2026.7.20.0, build 7489)
    /// </summary>
    internal static class Program
    {
        private const string Host = "127.0.0.1";
        private const int Port = 22023;

        private static UdpClient _udp;
        private static ushort _nonce = 1;
        private static volatile bool _running = true;

        private static ushort NextNonce() => _nonce++;

        private static int Main(string[] args)
        {
            int gameId = args.Length > 0 ? int.Parse(args[0]) : 5;
            int version = args.Length > 1 ? int.Parse(args[1]) : 50663600;
            string user = args.Length > 2 ? args[2] : "BotClient";
            int seconds = args.Length > 3 ? int.Parse(args[3]) : 30;

            Console.WriteLine("════════ BotClient 持久连接版 ════════");
            Console.WriteLine($"目标 {Host}:{Port}  GameId={gameId}  Version={version}  User={user}  运行 {seconds}s");
            Console.WriteLine();

            _udp = new UdpClient();
            _udp.Client.ReceiveTimeout = 500;
            _udp.Connect(Host, Port);

            // 后台接收线程
            var rx = new Thread(ReceiveLoop) { IsBackground = true };
            rx.Start();

            // 1. Hello
            SendRaw("Hello(发起)", BuildHello(NextNonce(), version, user));
            Thread.Sleep(300);

            // 2. JoinGame
            SendRaw("JoinGame", BuildJoinGame(gameId));

            // 3. 保活：定期再发一次 JoinGame（防止丢失）
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            int resend = 0;
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(4000);
                if (DateTime.UtcNow >= deadline) break;
                if (++resend <= 3)
                    SendRaw($"JoinGame(重试{resend})", BuildJoinGame(gameId));
            }

            _running = false;
            Thread.Sleep(300);
            Console.WriteLine();
            Console.WriteLine("════════ 结束 ════════");
            return 0;
        }

        // ───────────────────────── 接收循环 ─────────────────────────
        private static void ReceiveLoop()
        {
            IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] d;
                try { d = _udp.Receive(ref from); }
                catch (SocketException) { continue; }
                catch { break; }

                if (d == null || d.Length < 1) continue;

                byte so = d[0];
                string name = so switch
                {
                    0x00 => "Normal", 0x01 => "Reliable", 0x08 => "Hello",
                    0x09 => "Disconnect", 0x0a => "Acknowledgement",
                    0x0b => "Fragment", 0x0c => "Ping", _ => "?"
                };

                var sb = new StringBuilder();
                sb.Append($"[收] 0x{so:X2} {name}  {d.Length}B: {Hex(d)}");

                // ── 保活应答 ──
                if (so == 0x0c && d.Length >= 3)          // Ping → 回 Ack
                {
                    ushort n = (ushort)((d[1] << 8) | d[2]);
                    SendRaw($"  ↳ Ack(ping {n})", BuildAck(n));
                }
                else if (so == 0x01 && d.Length >= 3)     // Reliable → 回 Ack
                {
                    ushort n = (ushort)((d[1] << 8) | d[2]);
                    SendRaw($"  ↳ Ack(rel {n})", BuildAck(n));
                    AppendMessages(sb, d, 3);
                }
                else if (so == 0x00)                      // Normal
                {
                    AppendMessages(sb, d, 1);
                }
                else if (so == 0x08)                      // 服务端 Hello
                {
                    sb.Append("  ★ 服务端 Hello");
                }
                else if (so == 0x09)                      // Disconnect
                {
                    sb.Append("  ❌ 被服务端断开");
                }
                else if (so == 0x0a)
                {
                    sb.Append("  (Ack)");
                }

                Console.WriteLine(sb.ToString());
            }
        }

        private static void AppendMessages(StringBuilder sb, byte[] d, int pos)
        {
            while (pos + 2 <= d.Length)
            {
                ushort len = (ushort)(d[pos] | (d[pos + 1] << 8));
                pos += 2;
                if (pos >= d.Length) break;
                byte tag = d[pos]; pos++;
                sb.Append($"\n      └ tag=0x{tag:X2} ({TagName(tag)}) len={len}");
                if (len > 0 && pos + len <= d.Length)
                    sb.Append($" payload={Hex(d, pos, len)}");
                pos += len;
            }
        }

        // ───────────────────────── 构造 ─────────────────────────
        /// <summary>
        /// 构造 Hello 载荷。
        ///
        /// ★ 结构来自**实测抓包**（ProtoDump hook 住 UnityUdpClientConnection.ConnectAsync
        /// 捕获的真实客户端载荷），不是文档 —— 2020 年的文档说「版本 + 用户名」只有
        /// 11 字节，而 v19 实际是 41 字节，多了 3 个字段和 1 个字符串。
        ///
        /// 我们最初发的短载荷会被服务端**静默忽略**（解析失败 → 不回 Hello 挑战 →
        /// 连接永远不完整 → 应用层永不派发）。这就是困扰多轮的根因。
        ///
        /// 实测抓到的原始字节：
        ///   B0 10 05 03 | 0A "Fellpillow" | 00 00 00 00 | 0D 00 00 00 |
        ///   01 | 09 00 02 | 08 "TESTNAME" | 00 00 00 00 00
        /// </summary>
        private static byte[] BuildHello(ushort nonce, int version, string username)
        {
            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(0x08);                                  // SendOption = Hello
            ms.WriteByte((byte)(nonce >> 8));                    // nonce 大端
            ms.WriteByte((byte)(nonce & 0xFF));
            ms.WriteByte(0x00);                                  // Hazel 版本指示字节

            var v = BitConverter.GetBytes(version);              // 客户端版本 int32 小端
            ms.Write(v, 0, 4);

            var nm = Encoding.UTF8.GetBytes(username);           // 用户名：1 字节长度前缀
            ms.WriteByte((byte)nm.Length);
            ms.Write(nm, 0, nm.Length);

            // ── 以下为 v19 新增字段（照抄实测结构）──
            ms.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 }, 0, 4);
            ms.Write(new byte[] { 0x0D, 0x00, 0x00, 0x00 }, 0, 4);
            ms.WriteByte(0x01);
            ms.Write(new byte[] { 0x09, 0x00, 0x02 }, 0, 3);

            var tag = Encoding.UTF8.GetBytes("TESTNAME");        // 第二个字符串
            ms.WriteByte((byte)tag.Length);
            ms.Write(tag, 0, tag.Length);

            ms.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00 }, 0, 5);

            return ms.ToArray();
        }

        private static byte[] BuildJoinGame(int gameId)
        {
            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(0x01);
            var n = NextNonce();
            ms.WriteByte((byte)(n >> 8));
            ms.WriteByte((byte)(n & 0xFF));
            ms.WriteByte(0x04); ms.WriteByte(0x00);
            ms.WriteByte(0x01);                       // tag = JoinGame
            var g = BitConverter.GetBytes(gameId);
            ms.Write(g, 0, 4);
            return ms.ToArray();
        }

        /// <summary>Acknowledgement: [0x0A][uint16 BE nonce][byte flags=0xFF]</summary>
        private static byte[] BuildAck(ushort nonce) =>
            new byte[] { 0x0A, (byte)(nonce >> 8), (byte)(nonce & 0xFF), 0xFF };

        private static void SendRaw(string label, byte[] pkt)
        {
            try
            {
                lock (_udp) _udp.Send(pkt, pkt.Length);
                Console.WriteLine($"[发] {label}  {pkt.Length}B: {Hex(pkt)}");
            }
            catch (Exception e) { Console.WriteLine($"[错] {label}: {e.Message}"); }
        }

        private static string TagName(byte t) => t switch
        {
            0x00 => "HostGame", 0x01 => "JoinGame", 0x02 => "StartGame",
            0x03 => "RemoveGame", 0x04 => "RemovePlayer", 0x05 => "GameData",
            0x06 => "GameDataTo", 0x07 => "JoinedGame", 0x08 => "EndGame",
            0x0a => "AlterGame", 0x0b => "KickPlayer", 0x0c => "WaitForHost",
            0x0d => "Redirect", 0x0e => "ReselectServer", 0x10 => "GetGameListV2",
            0x11 => "ReportPlayer", _ => "?"
        };

        private static string Hex(byte[] b) => Hex(b, 0, b.Length);
        private static string Hex(byte[] b, int off, int len)
        {
            var sb = new StringBuilder();
            for (int i = off; i < off + len && i < b.Length; i++) sb.Append(b[i].ToString("X2")).Append(' ');
            return sb.ToString().TrimEnd();
        }
    }
}
