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
        private static int _gameId = 32;
        private static bool _clientInfoSent;
        private static int _rpcNetId = 4;
        private static int _netBase = 8;
        private static int _forcePid = -1;
        private static int _rpcTarget = -1;   // >=0 时 SetName/SetColor 打这个 netId
        private static int _ourNetId = -1;
        private static int _myClientId = -1;
        private static int _playerId = -1;   // >=0 时强制使用该 playerId
        private static byte _color = 0x02;   // 2 = GREEN，避开红色

        private static ushort NextNonce() => _nonce++;

        private static int Main(string[] args)
        {
            int gameId = args.Length > 0 ? int.Parse(args[0]) : 32;
            _gameId = gameId;
            int version = args.Length > 1 ? int.Parse(args[1]) : 50663600;
            string user = args.Length > 2 ? args[2] : "BotClient";
            int seconds = args.Length > 3 ? int.Parse(args[3]) : 0;   // 0 = 一直运行
            if (args.Length > 4) _rpcNetId = int.Parse(args[4]);
            if (args.Length > 5) _netBase = int.Parse(args[5]);
            if (args.Length > 6) _color = byte.Parse(args[6]);
            if (args.Length > 7) _forcePid = int.Parse(args[7]);
            if (args.Length > 8) _rpcTarget = int.Parse(args[8]);

            Console.WriteLine("════════ BotClient 持久连接版 ════════");
            Console.WriteLine($"目标 {Host}:{Port}  GameId={gameId}  Version={version}  User={user}  颜色={_color}  "
                            + (seconds > 0 ? $"运行 {seconds}s" : "持续运行（Ctrl+C 停止）"));
            Console.WriteLine();

            _udp = new UdpClient();
            _udp.Client.ReceiveTimeout = 500;
            _udp.Connect(Host, Port);

            // ★ 优雅退出：被 kill / Ctrl+C 时先发一个 Disconnect，
            //   否则房主会留下一个「没有名字的幽灵条目」，
            //   累积几次就会把房主自己的状态机搞崩（实测被踢出游戏）。
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                try
                {
                    // SendOption 0x09 = Disconnect
                    var bye = new byte[] { 0x09 };
                    lock (_udp) _udp.Send(bye, bye.Length);
                    Console.WriteLine("[退出] 已发送 Disconnect，房主会清理该条目");
                    Thread.Sleep(300);
                }
                catch { }
            };

            // 后台接收线程
            var rx = new Thread(ReceiveLoop) { IsBackground = true };
            rx.Start();

            // 1. Hello
            SendRaw("Hello(发起)", BuildHello(NextNonce(), version, user));
            Thread.Sleep(300);

            // 2. JoinGame
            SendRaw("JoinGame", BuildJoinGame(gameId));

            // 3. 保活循环
            //    ★ 已加入后不再重发 JoinGame —— 之前每重发一次，房主就当成一个新客户端，
            //      导致玩家列表被污染（残留了一堆 id=3,4,5,6 的 BotTest）。
            var deadline = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
            int resend = 0;
            Console.WriteLine();
            Console.WriteLine("── 进入保活循环（Ctrl+C 退出）──");
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(4000);
                if (DateTime.UtcNow >= deadline) break;

                // 只在「还没加入成功」时才重试 JoinGame
                if (!_clientInfoSent && ++resend <= 3)
                {
                    SendRaw($"JoinGame(重试{resend})", BuildJoinGame(gameId));
                }
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

                // ★ 把非保活包的原生字节单独打一份，便于逐字段分析
                //   （之前只打 tag/len，看不出解析错位在哪）
                if (so != 0x0C && so != 0x0A)
                {
                    Console.WriteLine($"      [RAW-IN] {Hex(d, 0, Math.Min(d.Length, 120))}");
                }

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
                    HandleSubMessages(d, 3);              // ★ 找 JoinedGame 并报到
                }
                else if (so == 0x00)                      // Normal
                {
                    AppendMessages(sb, d, 1);
                    HandleSubMessages(d, 1);              // ★ Normal 包也要解析！
                                                          //   之前只在 Reliable 上跑，
                                                          //   而房主很可能把 Spawn 放在 Normal 里发，
                                                          //   导致我们一直看不到自己角色的 netId。
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

        /// <summary>
        /// 解析外层 Hazel 消息。
        ///
        /// ★★★ 关键：外层包类型里有两个容易混淆的 0x06 ★★★
        ///   InnerNet.Tags.GameDataTo = 0x06   （包类型：定向发给某客户端，后面多一个目标 clientId）
        ///   GameData 子消息 SceneChange = 0x06 （子消息类型）
        ///
        /// 之前把外层 0x06 当成「SceneChange 子消息」解析，导致后续全部错位 ——
        /// 而房主正是用 GameDataTo 把「分配给你的角色」的 Spawn 发给我们的，
        /// 所以一直看不到自己角色的 netId，名字也就永远设不上。
        /// </summary>
        private static void AppendMessages(StringBuilder sb, byte[] d, int pos, bool isGameDataTo = false)
        {
            while (pos + 2 <= d.Length)
            {
                ushort len = (ushort)(d[pos] | (d[pos + 1] << 8));
                pos += 2;
                if (pos >= d.Length) break;
                byte tag = d[pos]; pos++;

                string tn = tag switch
                {
                    0x05 => "★GameData", 0x06 => "★GameDataTo", _ => TagName(tag)
                };
                sb.Append($"\n      └ tag=0x{tag:X2} ({tn}) len={len}");

                if (tag == 0x05 || tag == 0x06)
                {
                    // GameData 内容: gameId(4) [+ 目标clientId(1，仅 GameDataTo)] + 子消息序列
                    int inner = pos + 4 + (tag == 0x06 ? 1 : 0);
                    if (tag == 0x06)
                        sb.Append($"  target={d[pos + 4]}");
                    sb.Append("  子消息→");
                    ParseSubs(sb, d, inner, pos + len, tag == 0x06);
                }
                else if (len > 0 && pos + len <= d.Length)
                {
                    sb.Append($" payload={Hex(d, pos, Math.Min((int)len, 48))}");
                }
                pos += len;
            }
        }

        /// <summary>解析 GameData / GameDataTo 内部的子消息序列</summary>
        private static void ParseSubs(StringBuilder sb, byte[] d, int pos, int end, bool isTo)
        {
            if (end > d.Length) end = d.Length;
            while (pos + 2 <= end)
            {
                ushort slen = (ushort)(d[pos] | (d[pos + 1] << 8));
                pos += 2;
                if (pos >= end) break;
                byte stag = d[pos]; pos++;
                string sn = stag switch
                {
                    0x01 => "Data", 0x02 => "RPC", 0x04 => "★Spawn", 0x05 => "Despawn",
                    0x06 => "SceneChange", 0x07 => "Ready", 0x08 => "ChangeSettings",
                    0xCD => "ClientInfo", _ => "?"
                };
                sb.Append($" [{sn}(0x{stag:X2}) {slen}B]");

                // ★ Spawn 里带的就是「房主分配给我们角色的 netId」
                if (stag == 0x04)
                {
                    TryExtractOurNetId(d, pos, slen);
                }
                pos += slen;
            }
        }


        /// <summary>
        /// 扫描 GameData 里的子消息，发现 JoinedGame (0x07) 就发 ClientInfo 报到。
        ///
        /// JoinedGame 载荷: gameId(int32 LE) | 自己的 clientId(int32 LE) | hostId(int32 LE) | ...
        /// 实测样例: 20 00 00 00 | 03 00 00 00 | 02 00 00 00 | ...
        /// </summary>
        private static void HandleSubMessages(byte[] d, int pos)
        {
            while (pos + 2 <= d.Length)
            {
                ushort len = (ushort)(d[pos] | (d[pos + 1] << 8));
                pos += 2;
                if (pos >= d.Length) return;
                byte tag = d[pos]; pos++;

                // ★ 记录房主发来的一切（含 Spawn）——之前过滤太严，什么都没看到
                try
                {
                    string tn = tag switch { 0x01=>"Data",0x02=>"RPC",0x04=>"★Spawn",0x05=>"Despawn",
                                             0x06=>"SceneChange",0x07=>"Ready",0x08=>"ChangeSettings",
                                             0xCD=>"ClientInfo",_=>"?" };
                    Console.WriteLine($"      ← 房主发来: tag=0x{tag:X2}({tn}) len={len}");
                }
                catch { }
                if (tag == 0x04) TryExtractOurNetId(d, pos, len);   // ★ 房主发来的 Spawn

                if (tag == 0x07 && len >= 12 && pos + 12 <= d.Length)
                {
                    int gid  = BitConverter.ToInt32(d, pos);
                    int cid  = BitConverter.ToInt32(d, pos + 4);
                    int hid  = BitConverter.ToInt32(d, pos + 8);

                    // ★ 关键：从玩家列表里读出现有 playerId，挑一个空闲的。
                    //   绝不能直接用 clientId —— 实测房主的 playerId 恰好也是 9，
                    //   于是我们的 SetName 把房主改名了（截图里 "房主: BotTest"）。
                    int pid = _forcePid >= 0 ? _forcePid : PickFreePlayerId(d, pos + 12, len - 12);
                    _myClientId = cid; _playerId = pid; _gameId = gid;
                    Console.WriteLine($"      ★ JoinedGame: gameId={gid} 我的clientId={cid} hostId={hid} → 选用playerId={pid}");

                    if (!_clientInfoSent)
                    {
                        _clientInfoSent = true;
                        Thread.Sleep(200);
                        SendRaw("ClientInfo(报到)", BuildClientInfo(gid, cid, 2));
                        Thread.Sleep(200);
                        SendRaw("SceneChange(OnlineGame)", BuildSceneChange(gid, cid, "OnlineGame"));
                        Thread.Sleep(200);

                        // ★★★ 只操作自己的对象 —— 顺序很重要：
                        //   1) 先 Spawn 自己的玩家（netId 从 _netBase 起）
                        //   2) 再对自己的 netId 发 RPC
                        //
                        // ⚠️ 曾经有一个「探针」用 netId=4（房主的玩家对象）发 SetName，
                        //    结果把房主改了名，并在后续把房主的状态机搞崩
                        //    （表现为「正在等待主持人」+ 大厅设置重置）。
                        //    永久移除 —— 机器人绝不触碰不属于自己的对象。
                        // ★★★ 不再自建角色 ★★★
                        //
                        // 修好 SceneChange 之后，房主收到「客户端进入 OnlineGame 场景」
                        // 就会**自己**生成 PlayerControl 并建 PlayerInfo（观测已证实：
                        // CoOnPlayerChangedScene → AddPlayer → AllPlayers.Count 1→2）。
                        //
                        // 我们再自建一个就是**重复对象**：它没有对应的 PlayerInfo，
                        // 于是在大厅里显示成绿色 ??? 占位符，而真正的玩家条目是空的。
                        //
                        // 正确做法：等房主把它生成的 Spawn 发给我们，从里面读出
                        // 它分配的 netId，再用那个 netId 发 SetName/SetColor。
                        // ★★★ 不自建角色 ★★★
                        //
                        // 抓取「真实客户端加入在线房间」的序列后确认：
                        //   真实客户端**从不发 Spawn** —— 角色由服务器生成，
                        //   客户端拿到服务器分配的 netId 后，只用那个 netId 发 RPC。
                        //
                        //   我们的自建 Spawn 造出了一个房主不认识的对象（netId 8），
                        //   于是所有 RPC 都打在它上面 → 房主 FindObjectByNetId 查不到
                        //   → 全部丢弃 → 名字永远是空 → 显示 ???。
                        //
                        // 正确做法：等房主把「分配给你的角色」的 Spawn 发过来，
                        // 读出里面的 netId，再对它发 CheckName/CheckColor。
                        Console.WriteLine("      → 不自建角色（与真实客户端一致），等待房主分配 netId");

                        int tgt = _rpcTarget >= 0 ? _rpcTarget : _netBase;
                        SendRaw($"CheckName/CheckColor(netId={tgt})",
                                BuildCheckNameColor(gid, tgt, pid, "BotTest", _color));
                    }
                }
                pos += len;
            }
        }

        /// <summary>
        /// 从 JoinedGame 的玩家列表里读出现有 playerId，返回一个空闲的。
        ///
        /// 实测条目结构（每个 18 字节平台块）：
        ///   &lt;playerId&gt; &lt;flag&gt; &lt;namelen&gt; &lt;name&gt; &lt;18 字节平台信息&gt;
        ///
        /// ⚠️ 绝不能直接用 clientId 当 playerId —— 实测两者会撞车
        /// （房主 playerId=9、我们 clientId=9），结果把房主改了名。
        /// </summary>
        private static int PickFreePlayerId(byte[] d, int pos, int len)
        {
            var used = new System.Collections.Generic.HashSet<int>();
            int end = Math.Min(d.Length, pos + len);
            int i = pos;
            while (i + 3 <= end)
            {
                int pid = d[i];
                int nl  = d[i + 2];
                if (nl == 0 || i + 3 + nl > end) break;
                used.Add(pid);
                i += 3 + nl + 18;      // + 平台信息块
            }
            Console.WriteLine($"      已有 playerId: [{string.Join(",", used)}]");
            // ★ 从 0 起挑最小的空闲 id。
            //
            // 实测对比（干净状态下）：
            //   playerId=0 → 游戏接受，角色带自定义颜色和名字（真成功）
            //   playerId=5 → 游戏不认，显示为绿色 ??? 占位符
            // 结论：**必须用房主会分配的编号（最小空闲），不能自己挑高位**。
            //
            // 至于「房主: BotTest（你）」的错乱 —— 那是反复测试的残留污染造成的，
            // 与 playerId 无关（干净状态下用 0 也不会错乱）。
            for (int cand = 1; cand < 16; cand++)   // 0 是房主的本机 id，避开
                if (!used.Contains(cand)) return cand;
            return 12;
        }



        /// <summary>
        /// 从房主发来的 Spawn 里找出「分配给我们自己的玩家对象」的组件 netId。
        ///
        /// 房主生成的玩家 Spawn 结构（实测抓包）：
        ///   SpawnType=4 (PLAYER_CONTROL)  OwnerId=我们的clientId  Flags=1
        ///   Components=3: PlayerControl / PlayerPhysics / CustomNetworkTransform
        ///
        /// 我们只需要第一个组件的 netId —— 那是 SetName/SetColor 的目标。
        /// </summary>
        private static void TryExtractOurNetId(byte[] d, int pos, int len)
        {
            try
            {
                if (len <= 3) return;
                int i = pos;
                int end = Math.Min(d.Length, pos + len);

                // Spawn 体: packed SpawnType | packed OwnerId | byte flags | packed 组件数 | 组件...
                uint st = ReadPacked(d, ref i);
                uint owner = ReadPacked(d, ref i);
                if (i >= end) return;
                i++;                                   // flags
                uint ncomp = ReadPacked(d, ref i);

                Console.WriteLine($"      ★★★ Spawn: type={st} owner={owner} components={ncomp}");

                // SpawnType=4 是玩家；但我们把所有权重都记下来，便于判断
                if (ncomp < 1 || i >= end) return;
                uint netId = ReadPacked(d, ref i);
                Console.WriteLine($"      ★★★ 该对象首个组件 netId = {netId}");

                // ★ 必须按 owner 匹配 —— 只认「第一个 type=4」会选中房主自己的角色！
                //   实测：房主的玩家 owner=8 netId=4；我们的玩家 owner=9 netId=9。
                if (_ourNetId < 0 && st == 4 && owner == (uint)_myClientId)
                {
                    _ourNetId = (int)netId;
                    Console.WriteLine($"      ★★★ 认定这是分配给我的玩家对象: owner={owner} netId={_ourNetId}");
                    Thread.Sleep(150);
                    SendRaw($"CheckName/CheckColor(netId={_ourNetId})",
                            BuildCheckNameColor(_gameId, _ourNetId, _playerId, "BotTest", _color));
                }
            }
            catch (Exception e) { Console.WriteLine($"      [Spawn 解析失败] {e.Message}"); }
        }


        private static uint ReadPacked(byte[] b, ref int i)
        {
            uint v = 0; int shift = 0;
            while (i < b.Length)
            {
                byte x = b[i++];
                v |= (uint)(x & 0x7F) << shift;
                if ((x & 0x80) == 0) break;
                shift += 7;
            }
            return v;
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

        /// <summary>
        /// ★★★ 玩家对象的 Spawn —— 这一步决定「有没有身体」。
        ///
        /// 字节结构完全来自实测抓包（房主本地局 #15 的子消息 4）：
        ///   04 02 01 03 | 04 02 00 01 01 00 | 05 00 00 01 | 06 06 00 01 01 00 ff 7f ff 7f
        ///   SpawnType=4  Owner=2  Flags=1  Components=3
        ///
        /// 解码：
        ///   SpawnType 4 = PLAYER_CONTROL
        ///   Flags 1     = IS_CLIENT_CHARACTER
        ///   3 个组件    = PlayerControl + PlayerPhysics + CustomNetworkTransform
        ///                 （消息 tag 都是 0x01）
        ///
        /// 三个组件的消息内容：
        ///   [0] 01 00                  isNew=1, playerId
        ///   [1] (空)
        ///   [2] 01 00 ff 7f ff 7f      isNew=1, playerId, 位置(-32767,-32767)=未初始化
        /// </summary>
        private static byte[] BuildPlayerSpawn(int gameId, int ownerId, int playerId, int netBase)
        {
            var body = new System.Collections.Generic.List<byte>();
            body.Add(0x04);                       // SpawnType = PLAYER_CONTROL
            body.Add((byte)ownerId);              // OwnerId
            body.Add(0x01);                       // Flags = IS_CLIENT_CHARACTER
            body.Add(0x03);                       // Components = 3

            // 组件 0: PlayerControl
            body.Add((byte)(netBase + 0));        // netId
            body.Add(0x02); body.Add(0x00);       // 消息长度 = 2
            body.Add(0x01);                       // 消息 tag
            body.Add(0x01); body.Add((byte)playerId);   // isNew=1, playerId

            // 组件 1: PlayerPhysics（空消息）
            body.Add((byte)(netBase + 1));
            body.Add(0x00); body.Add(0x00);
            body.Add(0x01);

            // 组件 2: CustomNetworkTransform
            body.Add((byte)(netBase + 2));
            body.Add(0x06); body.Add(0x00);
            body.Add(0x01);
            body.Add(0x01); body.Add((byte)playerId);
            body.Add(0xFF); body.Add(0x7F);       // x = -32767
            body.Add(0xFF); body.Add(0x7F);       // y = -32767

            int subLen = body.Count;
            int gdLen = 4 + 2 + 1 + subLen;

            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(0x01);
            var n = NextNonce();
            ms.WriteByte((byte)(n >> 8));
            ms.WriteByte((byte)(n & 0xFF));
            ms.WriteByte((byte)(gdLen & 0xFF));
            ms.WriteByte((byte)((gdLen >> 8) & 0xFF));
            ms.WriteByte(0x05);                   // GameData
            var g = BitConverter.GetBytes(gameId); ms.Write(g, 0, 4);
            ms.WriteByte((byte)(subLen & 0xFF));
            ms.WriteByte((byte)((subLen >> 8) & 0xFF));
            ms.WriteByte(0x04);                   // tag = Spawn
            ms.Write(body.ToArray(), 0, body.Count);
            return ms.ToArray();
        }

        /// <summary>
        /// ★ RPC 批次 —— SetName(6) + SetColor(8)。
        ///
        /// 实测样例（抓包 #19，一个 GameData 里塞多条 RPC）：
        ///   04 06 03 00 00 00 0A "Fellpillow"    netId=4 RpcCalls=6 SetName  playerId=3 名字
        ///   04 08 03 00 00 00 00                 netId=4 RpcCalls=8 SetColor playerId=3 颜色0
        ///
        /// 结合前面解出的 RpcCalls 顺序（0=PlayAnimation … 6=SetName, 7=CheckColor, 8=SetColor）
        /// 完全吻合。
        /// </summary>
        /// <summary>
        /// ★★★ 名字/颜色：必须发 CheckName(5) / CheckColor(7)，而不是 SetName(6) / SetColor(8) ★★★
        ///
        /// 来源：抓取「真实客户端加入在线房间」的完整出站序列
        ///   #69  RPC netId=0A  RpcCalls=5 (CheckName)   0A "Fellpillow"
        ///   #73  RPC netId=0A  RpcCalls=7 (CheckColor)  00
        ///
        /// 对照 RpcCalls 表：5=CheckName 6=SetName 7=CheckColor 8=SetColor
        ///
        /// 机制：**客户端发 Check*「请求校验」，房主校验通过后由房主自己设名字/颜色
        /// 并广播 Set*」** —— 我们之前直接发 SetName，属于越权改名，
        /// 房主不接受，所以 PlayerInfo 的名字一直是空的（显示 ???）。
        ///
        /// 这也解释了观测到的「UpdateName 从未被调用」：
        /// 房主只在处理 CheckName 成功之后才会调它。
        /// </summary>
        private static byte[] BuildCheckNameColor(int gameId, int netId, int playerId, string name, byte color)
        {
            var nm = Encoding.UTF8.GetBytes(name);
            var nid = PackUInt32((uint)netId);

            // 子消息1: CheckName —— netId + RpcCalls(5) + 1字节长度 + 名字
            var r1 = new System.Collections.Generic.List<byte>();
            r1.AddRange(nid); r1.Add(0x05);
            r1.Add((byte)nm.Length); r1.AddRange(nm);

            // 子消息2: CheckColor —— netId + RpcCalls(7) + 颜色
            var r2 = new System.Collections.Generic.List<byte>();
            r2.AddRange(nid); r2.Add(0x07);
            r2.Add(color);

            int subs = (2 + 1 + r1.Count) + (2 + 1 + r2.Count);
            int gdLen = 4 + subs;

            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(0x01);
            var n = NextNonce();
            ms.WriteByte((byte)(n >> 8));
            ms.WriteByte((byte)(n & 0xFF));
            ms.WriteByte((byte)(gdLen & 0xFF));
            ms.WriteByte((byte)((gdLen >> 8) & 0xFF));
            ms.WriteByte(0x05);                        // GameData
            var g = BitConverter.GetBytes(gameId); ms.Write(g, 0, 4);
            foreach (var payload in new[] { r1, r2 })
            {
                int sl = payload.Count;
                ms.WriteByte((byte)(sl & 0xFF));
                ms.WriteByte((byte)((sl >> 8) & 0xFF));
                ms.WriteByte(0x02);                    // tag = RPC
                ms.Write(payload.ToArray(), 0, sl);
            }
            return ms.ToArray();
        }

        /// <summary>
        /// ★ SceneChange (0x06) —— 客户端告诉房主「我进入了哪个场景」。
        ///
        /// ★★★ 这里曾有一个致命 bug ★★★
        ///
        /// 初版是照抄「房主自己的抓包」，结果**连它的 ClientId 一起抄了**：
        ///   抄到的: 02 0A "OnlineGame"    ← ClientId = 2 = 房主自己
        /// 于是房主收到后以为「是房主自己换了场景」，
        /// 真实客户端（id=3）从未被登记为「已进入场景」，
        /// 房主因此永远不生成它的角色、永远不调用 GameData.AddPlayer ——
        /// **这就是 1/15、绿色 ??? 占位符、几秒后掉线的总根因。**
        ///
        /// 观测证据：[GDP] CoOnPlayerChangedScene 只对 client=id=2 触发，
        /// 从来不是 id=3 —— 尽管我们的 SceneChange 包已被房主正常解析。
        ///
        /// 教训：照抄真实包时，必须区分「通用结构」与「对方特有的身份值」。
        ///
        /// 正确格式（协议文档 0x06 SceneChange, Client-to-Host）：
        ///   packed int32  Client ID   ← 必须是**我们自己的** clientId
        ///   String        Scene Name  ← 1 字节长度前缀
        /// </summary>
        private static byte[] BuildSceneChange(int gameId, int clientId, string scene)
        {
            var nm = Encoding.UTF8.GetBytes(scene);
            var cid = PackUInt32((uint)clientId);
            int subLen = cid.Length + 1 + nm.Length;  // clientId + 长度字节 + 场景名
            int gdLen  = 4 + 2 + 1 + subLen;         // gameId + 子长度 + tag + 内容

            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(0x01);                       // Reliable
            var n = NextNonce();
            ms.WriteByte((byte)(n >> 8));
            ms.WriteByte((byte)(n & 0xFF));
            ms.WriteByte((byte)(gdLen & 0xFF));       // GameData 长度（小端）
            ms.WriteByte((byte)((gdLen >> 8) & 0xFF));
            ms.WriteByte(0x05);                       // tag = GameData
            var g = BitConverter.GetBytes(gameId);
            ms.Write(g, 0, 4);
            ms.WriteByte((byte)(subLen & 0xFF));      // 子消息长度
            ms.WriteByte((byte)((subLen >> 8) & 0xFF));
            ms.WriteByte(0x06);                       // tag = SceneChange
            ms.Write(cid, 0, cid.Length);             // ★ 我们自己的 clientId（原来是写死的 0x02 = 房主）
            ms.WriteByte((byte)nm.Length);
            ms.Write(nm, 0, nm.Length);
            return ms.ToArray();
        }

        /// <summary>Among Us 的 packed 整数编码：每字节 7 位，最高位表示「还有后续」</summary>
        private static byte[] PackUInt32(uint v)
        {
            var list = new System.Collections.Generic.List<byte>();
            do
            {
                byte b = (byte)(v & 0x7F);
                v >>= 7;
                if (v != 0) b |= 0x80;
                list.Add(b);
            } while (v != 0);
            return list.ToArray();
        }

        /// <summary>
        /// ★ ClientInfo (0xCD) —— 加入后必须发的「报到」消息。
        ///
        /// 格式（协议文档 + 实测）：
        ///   01              Reliable
        ///   nonce 2B BE
        ///   09 00           GameData 子消息长度 = 9
        ///   05              tag = GameData
        ///   gameId int32 LE
        ///   02 00           ClientInfo 长度 = 2
        ///   CD              tag = 0xCD ClientInfo
        ///   clientId        packed
        ///   platformId      packed (2 = WindowsPlayer)
        ///
        /// 这是让房主「为该客户端创建玩家对象」的关键一步 ——
        /// 之前只发 JoinGame 时，房主记下了我们（OnPlayerJoined 触发），
        /// 却始终不生成玩家，缺的就是这条。
        /// </summary>
        private static byte[] BuildClientInfo(int gameId, int clientId, int platformId)
        {
            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(0x01);
            var n = NextNonce();
            ms.WriteByte((byte)(n >> 8));
            ms.WriteByte((byte)(n & 0xFF));
            ms.WriteByte(0x09); ms.WriteByte(0x00);   // GameData 长度 = 9
            ms.WriteByte(0x05);                       // tag = GameData
            var g = BitConverter.GetBytes(gameId);
            ms.Write(g, 0, 4);
            ms.WriteByte(0x02); ms.WriteByte(0x00);   // ClientInfo 长度 = 2
            ms.WriteByte(0xCD);                       // tag = ClientInfo
            ms.WriteByte((byte)clientId);
            ms.WriteByte((byte)platformId);
            return ms.ToArray();
        }


        private static byte[] BuildJoinGame(int gameId)
        {
            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(0x01);
            var n = NextNonce();
            ms.WriteByte((byte)(n >> 8));
            ms.WriteByte((byte)(n & 0xFF));
            // ★ 实测：真实客户端的 JoinGame 载荷是 **5 字节**（gameId + 1 字节），
            //   我们之前只发 4 字节。多出的那个字节很可能是房主决定
            //   「是否为该客户端生成玩家对象」的依据。
            //   真实样例: 01 00 02 05 00 01 20 00 00 00 00
            //                              └─gameId=32─┘ └─多出的 00
            ms.WriteByte(0x05); ms.WriteByte(0x00);   // 长度 = 5
            ms.WriteByte(0x01);                       // tag = JoinGame
            var g = BitConverter.GetBytes(gameId);
            ms.Write(g, 0, 4);
            ms.WriteByte(0x00);                       // ★ 第 5 字节
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
