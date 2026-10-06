using System;
using System.IO;
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
        private static int _cntNetId = -1;
        private static float _posX, _posY;
        private static ushort _seq;
        private static bool _moving;
        private static string _userName = "BotTest";   // 名字（多个人机时各不相同）
        private static int _voteFor = -1;             // ★ 投票目标 playerId（-1=不投）
        private static bool _rejoining;               // 正在重返大厅
        private static int _dir = 1;   // 1=右, -1=左
        // 步长 0.20（5Hz × 0.20 = 1 单位/秒）—— 实测可用的配置，别乱动
        private const float Step = 0.20f;
        private static float _dx = 1f, _dy;   // 当前游走方向
        private static int _dirFrames;        // 还有几帧换方向
        private static readonly Random _rng = new Random();
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
            _userName = user;
            if (args.Length > 9 && int.TryParse(args[9], out var vf)) _voteFor = vf;
            SendRaw("Hello(发起)", BuildHello(NextNonce(), version, user));
            Thread.Sleep(300);

            // 1.2 ★ 启动「拍桌」监听线程（外部建触发文件即可让它按紧急按钮）
            new Thread(PressLoop) { IsBackground = true }.Start();
            new Thread(KillLoop) { IsBackground = true }.Start();   // ★ 刀人监听

            {
                string g2 = Environment.GetEnvironmentVariable("AMONGUS_DIR");
                if (string.IsNullOrEmpty(g2))
                    g2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                      ".local/share/Steam/steamapps/common/Among Us");
                string lp = Path.Combine(g2, "BepInEx/LogOutput.log");
                if (!TryLoadGeometry(lp))
                    Console.WriteLine("      ⚠️ 未解析到碰撞几何 —— 寻路会原地不动（等场景变化后会重试）");
            }

            // 2. JoinGame
            SendRaw("JoinGame", BuildJoinGame(gameId));

            // 2.5 ★ SetActivePodType —— 真实客户端在 JoinGame 之后立刻发这个，
            //     之前漏了，房主最终以 "Timeout while waiting for other player data" 踢人。
            Thread.Sleep(120);
            SendRaw("SetActivePodType", BuildSetActivePodType("pods/empty"));

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
                if (tag == 0x08 && !_rejoining)                // ★ EndGame —— 对局结束
                {
                    // ═══════════════════════════════════════════════════
                    // ★★★ 对局结束后回到大厅 ★★★
                    //
                    // 对局结束时房主会重建大厅对象，但**不会主动通知已有客户端重新进场景**。
                    // 真客户端会自己重发一遍 SceneChange（所以用户点「继续」能回房间），
                    // 而我们的人机原来不处理 EndGame —— 于是只剩心跳、不再收到任何数据。
                    //
                    // 修法：收到 EndGame 就重走一遍「报到 + 场景切换」，
                    //       房主会因此重新执行 SendInitialData + CreatePlayer。
                    // ═══════════════════════════════════════════════════
                    _rejoining = true;
                    _ourNetId = -1; _cntNetId = -1;             // 旧 netId 作废
                    Console.WriteLine("      ★★★ 收到 EndGame —— 对局结束，准备重返大厅");
                    var t2 = new Thread(() =>
                    {
                        try
                        {
                            Thread.Sleep(2500);                  // 等房主重建大厅
                            Console.WriteLine("      → 重发 ClientInfo + SceneChange");
                            SendRaw("ClientInfo(重返)", BuildClientInfo(_gameId, _myClientId, 2));
                            Thread.Sleep(300);
                            SendRaw("SceneChange(重返大厅)", BuildSceneChange(_gameId, _myClientId, "OnlineGame"));
                            Thread.Sleep(500);
                            _rejoining = false;                  // 允许处理下一次 EndGame
                        }
                        catch (Exception e) { Console.WriteLine($"      [重返失败] {e.Message}"); _rejoining = false; }
                    }) { IsBackground = true };
                    t2.Start();
                }
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

                        // ═══════════════════════════════════════════════════════
                        // ★★★ 时序就是答案 ★★★
                        //
                        // 来源：Impostor（第三方 Among Us 服务端实现）的源码
                        //   Constants.cs:        SpawnTimeout = 2500   （毫秒）
                        //   Game.Incoming.cs:192 sender.InitializeSpawnTimeout();   ← 玩家一加入就启动
                        //   ClientPlayer.cs:44   DisableSpawnTimeout();             ← 唯一解除方式
                        //   Game.Data.cs:84-90   if (!TryGetPlayer(control.OwnerId, out var player)) throw;
                        //                        player.Character = control;
                        //                        player.DisableSpawnTimeout();
                        //
                        // 机制：**玩家加入后服务端启动 2.5 秒倒计时，只有收到
                        // 「该玩家的 InnerPlayerControl（SpawnType=4）被 Spawn」才解除；
                        // 消息里的 OwnerId 还必须能对上这个玩家。超时就断开 —— 报错正是
                        // 「Timeout while waiting for other player data」。**
                        //
                        // 我们之前的自建 Spawn 是**等房主先发 Spawn 给我们之后**才发的，
                        // 那可能早就超过 2.5 秒了。所以改成：一拿到 JoinedGame
                        // （此时已知自己的 clientId）就**立刻**把自己的角色 Spawn 出去。
                        // ═══════════════════════════════════════════════════════

                        // 1) 报到 + 场景切换（尽快，但不能再等房主）
                        SendRaw("ClientInfo(报到)", BuildClientInfo(gid, cid, 2));
                        Thread.Sleep(120);
                        SendRaw("SceneChange(OnlineGame)", BuildSceneChange(gid, cid, "OnlineGame"));
                        Thread.Sleep(120);

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
                        // 2) ★★★ 绝不自建角色 ★★★
                        //
                        // 源码证据（InnerNetClient.Spawn）：
                        //     public void Spawn(InnerNetObject netObjParent, int ownerId, SpawnFlags flags)
                        //     {
                        //         if (this.AmHost) { ...正常... return; }
                        //         if (!this.AmClient) return;
                        //         Debug.LogError("Tried to spawn while not host:" + netObjParent);
                        //     }
                        //   → **只有主机能 Spawn，客户端发 Spawn 是无效操作。**
                        //
                        // 而且游戏日志里抓到了这一行：
                        //     Double spawn character: 3 already has 9
                        //   → 房主通过 CreatePlayer 早就给我们建好了角色（netId=9），
                        //     我们又自己发一个 Spawn，于是被 double-spawn 保护丢弃。
                        //
                        // 更糟的是：我们之前写死 netId=_netBase(8)，
                        // 而房主实际分配的是 9 —— 于是 CheckName 和位置包
                        // 全部打在**不存在的对象**上（名字设不上、位置不动）。
                        //
                        // 正确做法：什么都不发，**等房主把它的 Spawn 发过来**，
                        // 从里面解出真实的 netId，再用那个 netId 发 RPC。
                        // 房主的链路是：我们的 SceneChange → InScene=true
                        //              → SendInitialData(给我们发所有对象的 Spawn)
                        //              → CreatePlayer(建房主的角色并把 Character 指过去)
                        Console.WriteLine("      → 不自建角色（源码确认客户端无权 Spawn），等待房主分配 netId");
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

                // ★ 记录 MeetingHud（SpawnType=1）的 netId —— 投票 RPC 要打给它
                if (st == 1)
                {
                    int mhNet = (int)ReadPacked(d, ref i);
                    Console.WriteLine($"      ★★★ MeetingHud netId={mhNet} → 开始投票（目标 pid={_voteFor}）");
                    if (_voteFor >= 0)
                    {
                        // ★ 不要一出现就投 —— 用户反馈「一拍桌就全投了」，太机械。
                        //   真人会犹豫几秒，而且各人时机不同。
                        //   这里每台随机等 3~10 秒，且彼此错开。
                        int delayMs = 3000 + _rng.Next(7000);
                        Console.WriteLine($"      → 犹豫 {delayMs / 1000.0:F1} 秒后投票…");
                        var tv = new Thread(() =>
                        {
                            try
                            {
                                Thread.Sleep(delayMs);
                                SendRaw($"CastVote(我={_playerId} → 投{_voteFor})",
                                        BuildRpc(_gameId, mhNet, 24, new byte[] { (byte)_playerId, (byte)_voteFor }));
                            }
                            catch (Exception ex) { Console.WriteLine($"      [投票失败] {ex.Message}"); }
                        }) { IsBackground = true };
                        tv.Start();
                    }
                    return;
                }

                // ★ 记录**所有**玩家的 Spawn（owner + 首个组件 netId）
                //   刀的载荷需要「目标 PlayerControl 的 netId」，必须能查到。
                if (st == 4)
                {
                    int save = i;
                    int pcNet = (int)ReadPacked(d, ref save);
                    Console.WriteLine($"      · 玩家 Spawn: clientId={owner} PlayerControl netId={pcNet}");
                }

                if (st != 4 || owner != (uint)_myClientId) return;

                Console.WriteLine($"      ★★★ 我的玩家 Spawn: components={ncomp}");
                if (ncomp < 1 || i >= end) return;

                int[] ids = new int[ncomp];
                for (int c = 0; c < ncomp && i < end; c++)
                {
                    ids[c] = (int)ReadPacked(d, ref i);
                    if (i + 2 > end) break;
                    int mlen = d[i] | (d[i + 1] << 8); i += 2;
                    if (i >= end) break;
                    byte mtag = d[i]; i++;             // 组件消息 tag
                    int mstart = i;

                    Console.WriteLine($"        组件[{c}] netId={ids[c]} msgLen={mlen} tag=0x{mtag:X2}");

                    // 第 3 个组件是 CustomNetworkTransform，里面带着初始坐标
                    if (c == 2 && mlen >= 6)
                    {
                        int seq = d[mstart] | (d[mstart + 1] << 8);
                        // 之后是 flags(1) + 位置(4) —— 与抓包一致
                        int off = mstart + 2;
                        if (off < d.Length && (d[off] == 0x01 || d[off] == 0x00)) off++;
                        if (off + 4 <= d.Length)
                        {
                            int rx = d[off] | (d[off + 1] << 8);
                            int ry = d[off + 2] | (d[off + 3] << 8);
                            _posX = (float)(rx / 65535.0 * 100.0 - 50.0);
                            _posY = (float)(ry / 65535.0 * 100.0 - 50.0);
                            _seq = (ushort)seq;
                            Console.WriteLine($"        ★ 初始坐标: ({_posX:F2}, {_posY:F2})  seq={_seq}  [raw {rx},{ry}]");
                        }
                    }
                    i = mstart + mlen;
                }

                if (ncomp >= 3)
                {
                    _ourNetId = ids[0];
                    _cntNetId = ids[2];
                    Console.WriteLine($"      ★ 玩家 netId={_ourNetId}  位置组件(CNT) netId={_cntNetId}");

                    Thread.Sleep(150);
                    SendRaw($"CheckName/CheckColor(netId={_ourNetId})",
                            BuildCheckNameColor(_gameId, _ourNetId, _playerId, _userName, _color));

                    // ★ 名字设完之后，补上真实客户端序列里的其余部分（装扮 / Ready）
                    //   全部打在**房主分配的** netId 上，而不是我们猜测的编号。
                    Thread.Sleep(120);
                    SendRaw("装扮 RPC 批次", BuildCosmetics(_gameId, _ourNetId));
                    Thread.Sleep(120);
                    SendRaw("Ready(0x07)", BuildReady(_gameId, _myClientId));

                    // 起一个后台线程持续移动
                    if (!_moving)
                    {
                        _moving = true;
                        var t = new Thread(MoveLoop) { IsBackground = true };
                        t.Start();
                    }
                }
            }
            catch (Exception e) { Console.WriteLine($"      [Spawn 解析失败] {e.Message}"); }
        }


        // ═══════════════════════════════════════════════════════════
        // ★★★ 可行走网格 —— 让人机不穿墙 ★★★
        //
        // 人机是外置进程，没有游戏的物理引擎，算不出哪里是墙。
        // 解决办法：由游戏内的 ProtoDump 插件用 Physics2D.OverlapCircle
        // 预计算一张网格并写进 BepInEx 日志，人机直接读日志里的网格。
        //
        // 格式（与插件 PacketWatch.TickGrid 严格一致）：
        //   [GRID] BEGIN <宽> <高> <格子边长>
        //   <每行一个 0/1 字符串，'1'=可走 '0'=墙>
        //   [GRID] END
        //
        // 坐标映射：格 (i,j) 的世界坐标 =
        //   x = -W*CELL/2 + i*CELL + CELL/2
        //   y = -H*CELL/2 + j*CELL + CELL/2


        // ═══════════════════════════════════════════════════════════
        // ★★★ 房间边界 —— 来自插件导出的 ShipRoom 包围盒 ★★★
        //
        // 教训：采样网格抓不到**空心的墙圈**（采样点落进房间内部自然探不到墙），
        //       所以机器人一路走出大厅（实测跑到 x=22.8）。
        //
        // 而插件导出的碰撞体几何里，ShipRoom 就是那圈墙：
        //       35.3  L9  x[-3.45, 3.46] y[-1.63, 3.46]  ShipRoom
        //       （实测大厅只有 7×5 单位）
        //
        // 所以：**边界用几何（不会漏），障碍物用网格（实心物体很准）**。
        // ═══════════════════════════════════════════════════════════
        private static bool _haveRoom;
        private static float _roomMinX, _roomMinY, _roomMaxX, _roomMaxY;

        // ═══════════════════════════════════════════════════════════
        // ★★★ 地图几何 + A* 寻路 ★★★
        //
        // 插件每次场景切换都会导出当前场景的全部 Collider2D 包围盒：
        //     [COL] BEGIN <数量> SCENE <场景名>
        //     <层> <minX> <minY> <maxX> <maxY> <类型> <名字>
        //     [COL] END
        //
        // 分类判据（来自实测数据）：
        //     房间 = Ship 层(9) 且面积 > 15     （大厅里是 Lobby(Clone) 21.0）
        //     障碍 = 面积 < 5 的实体             （箱子、按钮、设备）
        //     地板 / 摇杆区等大面积非房间物体 → 忽略
        //
        // 可走 = **在任一房间内** 且 **不在任何障碍内**
        // ═══════════════════════════════════════════════════════════
        private class Box { public float x0, y0, x1, y1; public bool Room; }

        private static readonly System.Collections.Generic.List<Box> _boxes
            = new System.Collections.Generic.List<Box>();
        private static string _geoScene = "";
        private static float _mapMinX, _mapMinY, _mapMaxX, _mapMaxY;

        /// <summary>读日志里**最后一段** [COL]（= 当前场景）并按房间/障碍分类</summary>
        private static bool TryLoadGeometry(string logPath)
        {
            try
            {
                if (!File.Exists(logPath)) return false;
                var lines = File.ReadAllLines(logPath);
                int begin = -1;
                for (int i = lines.Length - 1; i >= 0; i--)
                    if (lines[i].Contains("[COL] BEGIN")) { begin = i; break; }
                if (begin < 0) return false;

                var mm = System.Text.RegularExpressions.Regex.Match(lines[begin], @"SCENE (\S+)");
                string scene = mm.Success ? mm.Groups[1].Value : "?";

                var tmp = new System.Collections.Generic.List<Box>();
                float mnx = float.MaxValue, mny = float.MaxValue, mxx = float.MinValue, mxy = float.MinValue;

                for (int i = begin + 1; i < lines.Length; i++)
                {
                    var L = lines[i];
                    if (L.Contains("[COL] END")) break;
                    var f = L.Split(' ');
                    if (f.Length < 7) continue;
                    if (L.Contains("Player") || L.Contains("Deadzone")) continue;
                    if (!int.TryParse(f[0], out int layer)) continue;
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    if (!float.TryParse(f[1], System.Globalization.NumberStyles.Float, ci, out var x0)) continue;
                    float.TryParse(f[2], System.Globalization.NumberStyles.Float, ci, out var y0);
                    float.TryParse(f[3], System.Globalization.NumberStyles.Float, ci, out var x1);
                    float.TryParse(f[4], System.Globalization.NumberStyles.Float, ci, out var y1);

                    float area = (x1 - x0) * (y1 - y0);
                    if (area <= 0f) continue;

                    var b = new Box { x0 = x0, y0 = y0, x1 = x1, y1 = y1, Room = false };
                    if (layer == 9 && area > 15f) b.Room = true;                        // 房间
                    else if (area < 5f && (layer == 9 || layer == 0)) b.Room = false;   // 障碍
                    else continue;                                                       // 其它忽略

                    tmp.Add(b);
                    if (x0 < mnx) mnx = x0; if (y0 < mny) mny = y0;
                    if (x1 > mxx) mxx = x1; if (y1 > mxy) mxy = y1;
                }
                if (tmp.Count == 0) return false;
                if (scene == _geoScene) return false;        // 同一场景不重复载入

                lock (_boxes)
                {
                    _boxes.Clear(); _boxes.AddRange(tmp);
                    _mapMinX = mnx; _mapMinY = mny; _mapMaxX = mxx; _mapMaxY = mxy;
                }
                _geoScene = scene;
                int rooms = tmp.FindAll(b => b.Room).Count;
                Console.WriteLine($"      ★★★ 几何载入 [{scene}]：房间 {rooms} 个，障碍 {tmp.Count - rooms} 个，" +
                                  $"范围 x[{mnx:F1},{mxx:F1}] y[{mny:F1},{mxy:F1}]");
                return true;
            }
            catch (Exception e) { Console.WriteLine($"      [几何] 解析失败: {e.Message}"); return false; }
        }

        /// <summary>可走：在任一房间内，且不在任何障碍内</summary>
        private static bool Walkable(float x, float y)
        {
            const float R = 0.25f;
            bool inRoom = false;
            lock (_boxes)
            {
                foreach (var b in _boxes)
                    if (b.Room && x > b.x0 + R && x < b.x1 - R && y > b.y0 + R && y < b.y1 - R) { inRoom = true; break; }
                if (!inRoom) return false;
                foreach (var b in _boxes)
                    if (!b.Room && x > b.x0 - R && x < b.x1 + R && y > b.y0 - R && y < b.y1 + R) return false;
            }
            return true;
        }

        // ── A*（网格 0.5 单位）──
        private const float CELL = 0.5f;
        private static (int i, int j) ToCell(float x, float y) =>
            ((int)MathF.Round((x - _mapMinX) / CELL), (int)MathF.Round((y - _mapMinY) / CELL));
        private static (float x, float y) ToWorld(int i, int j) =>
            (_mapMinX + i * CELL, _mapMinY + j * CELL);

        private static System.Collections.Generic.List<(float x, float y)> FindPath(float sx, float sy, float tx, float ty)
        {
            if (!Walkable(tx, ty)) return null;
            var s = ToCell(sx, sy);
            var t = ToCell(tx, ty);

            // 起点落在非法位置（被挤进墙里）→ 先找最近的合法格
            if (!Walkable(sx, sy))
            {
                bool got = false;
                for (int r = 1; r <= 10 && !got; r++)
                    for (int dj = -r; dj <= r && !got; dj++)
                        for (int di = -r; di <= r && !got; di++)
                        {
                            if (Math.Abs(di) != r && Math.Abs(dj) != r) continue;
                            var (wx, wy) = ToWorld(s.i + di, s.j + dj);
                            if (Walkable(wx, wy)) { s = (s.i + di, s.j + dj); got = true; }
                        }
                if (!got) return null;
            }

            var open = new System.Collections.Generic.List<(int i, int j)> { s };
            var came = new System.Collections.Generic.Dictionary<(int, int), (int, int)>();
            var g = new System.Collections.Generic.Dictionary<(int, int), float> { [s] = 0f };

            int guard = 0;
            while (open.Count > 0 && guard++ < 30000)
            {
                int bi = 0; float bf = float.MaxValue;
                for (int k = 0; k < open.Count; k++)
                {
                    float f = g[open[k]] + MathF.Abs(open[k].i - t.i) + MathF.Abs(open[k].j - t.j);
                    if (f < bf) { bf = f; bi = k; }
                }
                var cur = open[bi];

                if (Math.Abs(cur.i - t.i) <= 1 && Math.Abs(cur.j - t.j) <= 1)
                {
                    var path = new System.Collections.Generic.List<(float x, float y)>();
                    var c = cur; int lim = 0;
                    while (came.ContainsKey(c) && lim++ < 5000)
                    {
                        var (wx, wy) = ToWorld(c.i, c.j);
                        path.Add((wx, wy));
                        c = came[c];
                    }
                    path.Reverse();
                    return path;
                }
                open.RemoveAt(bi);

                for (int d = 0; d < 4; d++)
                {
                    int ni = cur.i + (d == 0 ? 1 : d == 1 ? -1 : 0);
                    int nj = cur.j + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    var (wx, wy) = ToWorld(ni, nj);
                    if (!Walkable(wx, wy)) continue;
                    float ng = g[cur] + 1f;
                    var nb = (ni, nj);
                    if (g.TryGetValue(nb, out var old) && old <= ng) continue;
                    g[nb] = ng; came[nb] = cur;
                    if (!open.Contains(nb)) open.Add(nb);
                }
            }
            return null;
        }

        /// <summary>
        /// 移动：A* 选一个随机目标、沿路径走，到了就换下一个目标。
        ///
        /// ⚠️ 发送格式（19 字节）与频率（5Hz）**保持不变** ——
        ///    那是反复验证过的可用配置，寻路只改「往哪走」。
        /// </summary>
        private static void MoveLoop()
        {
            var path = new System.Collections.Generic.List<(float x, float y)>();
            int idx = 0;
            float tx = 0, ty = 0;
            int geoCheck = 0;

            while (true)
            {
                try
                {
                    Thread.Sleep(200);                          // 5Hz —— 别动

                    // 每 5 秒看看有没有换场景（插件会重导几何）
                    if (++geoCheck >= 25)
                    {
                        geoCheck = 0;
                        string gd = Environment.GetEnvironmentVariable("AMONGUS_DIR");
                        if (string.IsNullOrEmpty(gd))
                            gd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                              ".local/share/Steam/steamapps/common/Among Us");
                        TryLoadGeometry(Path.Combine(gd, "BepInEx/LogOutput.log"));
                    }

                    if (idx >= path.Count)
                    {
                        bool okPath = false;
                        for (int attempt = 0; attempt < 15 && !okPath; attempt++)
                        {
                            float gx = _mapMinX + (float)_rng.NextDouble() * (_mapMaxX - _mapMinX);
                            float gy = _mapMinY + (float)_rng.NextDouble() * (_mapMaxY - _mapMinY);
                            if (!Walkable(gx, gy)) continue;
                            var p = FindPath(_posX, _posY, gx, gy);
                            if (p == null || p.Count == 0) continue;
                            path = p; idx = 0; okPath = true; tx = gx; ty = gy;
                        }
                        if (!okPath)
                        {
                            _seq++;
                            var idle = BuildPosition(_gameId, _cntNetId, _seq, _posX, _posY);
                            if (_seq % 5 == 0) SendRaw($"原地({_posX:F1},{_posY:F1})", idle);
                            else SendPos(idle);
                            continue;
                        }
                    }

                    if (idx < path.Count)
                    {
                        float dx = path[idx].x - _posX, dy = path[idx].y - _posY;
                        float dist = MathF.Sqrt(dx * dx + dy * dy);
                        if (dist < Step) { _posX = path[idx].x; _posY = path[idx].y; idx++; }
                        else { _posX += dx / dist * Step; _posY += dy / dist * Step; }
                    }

                    _seq++;
                    var pkt = BuildPosition(_gameId, _cntNetId, _seq, _posX, _posY);
                    if (_seq % 5 == 0)
                        SendRaw($"位置({_posX:F1},{_posY:F1})→目标({tx:F1},{ty:F1})", pkt);
                    else
                        SendPos(pkt);
                }
                catch (Exception e) { Console.WriteLine($"      [移动失败] {e.Message}"); return; }
            }
        }


        // ═══════════════════════════════════════════════════════════
        // ★ 拍桌（按紧急按钮）
        // 源码依据：EmergencyMinigame.cs:88  PlayerControl.LocalPlayer.CmdReportDeadBody(null)
        // 而 CmdReportDeadBody 发 RPC(11)，载荷单字节 0xFF（target==null → byte.MaxValue）
        // 触发：/tmp/press-<pid>.trigger 出现即拍
        // ═══════════════════════════════════════════════════════════
        private static void PressLoop()
        {
            string trig = $"/tmp/press-{Environment.ProcessId}.trigger";
            while (true)
            {
                try
                {
                    Thread.Sleep(500);
                    if (!File.Exists(trig)) continue;
                    File.Delete(trig);
                    if (_ourNetId < 0) { Console.WriteLine("      [拍桌] 还没有角色，跳过"); continue; }
                    Console.WriteLine($"      ★★★ 拍桌！给自己的角色(netId={_ourNetId})发 ReportDeadBody(0xFF)");
                    SendRaw("ReportDeadBody(紧急按钮)", BuildRpc(_gameId, _ourNetId, 11, new byte[] { 0xFF }));
                }
                catch (Exception e) { Console.WriteLine($"      [拍桌失败] {e.Message}"); }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // ★ 刀人（只有内鬼能成功 —— 原版 MurderPlayer 会校验 Data.IsImpostor）
        // 源码依据：PlayerControl.RpcMurderPlayer → RPC(12)，载荷 = 目标 netId
        // 触发：/tmp/kill-<pid>.trigger 的内容 = 目标 PlayerControl 的 netId
        // ═══════════════════════════════════════════════════════════
        private static void KillLoop()
        {
            string trig = $"/tmp/kill-{Environment.ProcessId}.trigger";
            while (true)
            {
                try
                {
                    Thread.Sleep(500);
                    if (!File.Exists(trig)) continue;
                    string txt = File.ReadAllText(trig).Trim();
                    File.Delete(trig);
                    if (_ourNetId < 0) { Console.WriteLine("      [刀] 还没有角色，跳过"); continue; }
                    if (!int.TryParse(txt, out int targetNet))
                    { Console.WriteLine($"      [刀] 触发文件内容不是 netId: '{txt}'"); continue; }
                    Console.WriteLine($"      ★★★ 刀！目标 netId={targetNet}");
                    SendRaw($"MurderPlayer(目标 netId={targetNet})",
                            BuildRpc(_gameId, _ourNetId, 12, PackUInt32((uint)targetNet)));
                }
                catch (Exception e) { Console.WriteLine($"      [刀失败] {e.Message}"); }
            }
        }


        /// <summary>
        /// 构造 CustomNetworkTransform 的更新包（Normal 包）。
        ///
        /// ★★★ 格式来自源码（CustomNetworkTransform.Serialize 的增量路径）：
        ///     this.lastSequenceId += 1;
        ///     writer.Write(this.lastSequenceId);              // uint16 序列号
        ///     this.WriteVector2(this.body.position, writer);  // 位置 (2×uint16)
        ///     this.WriteVector2(this.body.velocity, writer);  // ★ 速度 (2×uint16)
        ///
        /// 我们之前发的格式是错的：
        ///     错误: netId | seq | flags(0x01) | x | y          ← 多了 flags、少了速度
        ///     正确: netId | seq | x | y | vx | vy              ← 没有 flags
        ///
        /// 少了速度的后果：接收方从**错误的偏移**去读速度，读到的是垃圾值，
        /// 于是插值乱套 —— 表现出来就是「滑行、抖动、没有走路动画」。
        /// 而走路动画本来就是由速度驱动的。
        ///
        /// 另外坐标范围是 FloatRange(-40, 40)，不是公开文档写的 ±50。
        /// </summary>
        private static byte[] BuildPosition(int gameId, int cntNetId, ushort seq, float x, float y, float vx = 0f, float vy = 0f)
        {
            var nid = PackUInt32((uint)cntNetId);

            // ═══════════════════════════════════════════════════════════
            // ★ 已回滚到**实测可用**的格式（发 0x03 会让人全部消失 + 掉线）
            //
            // 可用格式：netId | seq(2) | 标志(1)=0x01 | 位置(4)
            //   实测效果：人可见 ✓  15/15 ✓  移动正常 ✓  稳定不掉线 ✓
            //   缺点：没有速度 → 接收方算不出走路动画（看起来像滑行）
            //
            // 试过但失败的：把标志改成 0x03 并补上速度(4)
            //   → 人全部消失、房主连接中断 ✗
            //   说明 v19 的这个字段不是「脏位掩码」，语义与旧源码不同。
            //   在拿到 v19 的准确格式之前，先保留可用版本。
            // ═══════════════════════════════════════════════════════════
            int subLen = nid.Length + 2 + 1 + 4;
            int gdLen = 4 + 2 + 1 + subLen;

            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(0x00);                         // Normal 包（与真实抓包一致）
            ms.WriteByte((byte)(gdLen & 0xFF));
            ms.WriteByte((byte)((gdLen >> 8) & 0xFF));
            ms.WriteByte(0x05);                         // GameData
            var g = BitConverter.GetBytes(gameId); ms.Write(g, 0, 4);
            ms.WriteByte((byte)(subLen & 0xFF));
            ms.WriteByte((byte)((subLen >> 8) & 0xFF));
            ms.WriteByte(0x01);                         // tag = Data
            ms.Write(nid, 0, nid.Length);
            ms.WriteByte((byte)(seq & 0xFF));
            ms.WriteByte((byte)(seq >> 8));
            ms.WriteByte(0x01);                         // 标志：只含位置（实测可用）
            WriteVec(ms, x);  WriteVec(ms, y);          // 位置
            return ms.ToArray();
        }

        private static void WriteVec(System.IO.MemoryStream ms, float v)
        {
            int raw = EncX(v);
            ms.WriteByte((byte)(raw & 0xFF));
            ms.WriteByte((byte)((raw >> 8) & 0xFF));
        }

        /// <summary>
        /// 坐标/速度 → uint16。
        /// 范围 ±50（协议文档对 v19 是对的）。
        /// ⚠️ 我们手上的反编译源码是旧版本，写的是 FloatRange(-40,40)，
        ///    但实测按 ±50 编码时人是**显示正常**的，按 ±40 反而全部消失 ——
        ///    所以 v19 用的是 ±50。
        /// </summary>
        private static int EncX(float v)
        {
            float t = (v + 50f) / 100f;
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return (int)(t * 65535f);
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

            // ── 以下为 v19 新增字段 ──
            //
            // ★ 这 4 个字节：真实在线客户端填的是随机值（抓包实测 52 F8 41 3D），
            //   而本地抓包恰好是 00 00 00 00 —— 我们照抄了本地那份，于是一直填 0。
            //
            //   怀疑它与「Timeout while waiting for other player data」有关：
            //   房主手里我们的 ClientData 里 ProductUserId / FriendCode 都是空的，
            //   「等玩家数据」很可能就是在等这类账号标识。
            //   先按真实形态填随机值试一次。
            var rnd4 = new byte[4];
            new System.Random().NextBytes(rnd4);
            ms.Write(rnd4, 0, 4);
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
        /// ★ SetActivePodType (0x15) —— 顶层消息，**不包在 GameData 里**。
        ///
        /// 实测真实客户端的加入序列里有它，而我们一直没发：
        ///   01 00 02 0B 00 15 0A "pods/empty"
        ///
        /// 这是本轮排查「房主报 Timeout while waiting for other player data 并踢人」时
        /// 通过对比真实序列找到的**唯一完全缺失的顶层消息**。
        ///
        /// 讽刺的是：这个报错正是路线 A（假人）当年卡了 17 轮的那一个 ——
        /// 区别在于，假人方案根本没有真实连接可查，而人机方案能看到服务端在等什么。
        /// </summary>
        private static byte[] BuildSetActivePodType(string pod)
        {
            var nm = Encoding.UTF8.GetBytes(pod);
            int hlen = 1 + 1 + nm.Length;              // tag + 长度字节 + 字符串

            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(0x01);                        // Reliable
            var n = NextNonce();
            ms.WriteByte((byte)(n >> 8));
            ms.WriteByte((byte)(n & 0xFF));
            ms.WriteByte((byte)(hlen & 0xFF));         // Hazel 长度（小端）
            ms.WriteByte((byte)((hlen >> 8) & 0xFF));
            ms.WriteByte(0x15);                        // tag = SetActivePodType
            ms.WriteByte((byte)nm.Length);
            ms.Write(nm, 0, nm.Length);
            return ms.ToArray();
        }

        /// <summary>
        /// ★★★ Ready (0x07) —— 协议里字面意思就是「客户端已同步、准备好」。
        ///
        /// 房主手里的 ClientData 实测：
        ///   IsReady = False   ← 我们从来没发过这个包
        ///   InScene = False
        ///   Character = null  ← 所以角色一直没生成
        /// 而服务端最终以 'Timeout while waiting for other player data' 断开。
        ///
        /// 格式（协议文档 0x07 Ready, Client-to-Host）：
        ///   packed int32  Ready Client ID
        ///   包在 GameData (0x05) 里
        /// </summary>
        private static byte[] BuildReady(int gameId, int clientId)
        {
            var cid = PackUInt32((uint)clientId);
            int subLen = cid.Length;
            int gdLen = 4 + 2 + 1 + subLen;

            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(0x01);                        // Reliable
            var n = NextNonce();
            ms.WriteByte((byte)(n >> 8));
            ms.WriteByte((byte)(n & 0xFF));
            ms.WriteByte((byte)(gdLen & 0xFF));
            ms.WriteByte((byte)((gdLen >> 8) & 0xFF));
            ms.WriteByte(0x05);                        // GameData
            var g = BitConverter.GetBytes(gameId); ms.Write(g, 0, 4);
            ms.WriteByte((byte)(subLen & 0xFF));
            ms.WriteByte((byte)((subLen >> 8) & 0xFF));
            ms.WriteByte(0x07);                        // tag = Ready
            ms.Write(cid, 0, cid.Length);              // 就绪的 clientId
            return ms.ToArray();
        }

        /// <summary>
        /// ★ 装扮 RPC 批次 —— 真实客户端在 CheckName/CheckColor 之后紧接着发这一组。
        ///
        /// 抓包实测（在线加入序列 #77）：
        ///   04 00 02 0A 29 00 01     netId=0A  RpcCalls=0x29(41)
        ///   04 00 02 0A 27 00 01     RpcCalls=0x27(39)
        ///   04 00 02 0A 28 00 01     RpcCalls=0x28(40)
        ///   04 00 02 0A 2A 00 01     RpcCalls=0x2A(42)
        ///   04 00 02 0A 2B 00 01     RpcCalls=0x2B(43)
        ///   04 00 02 0A 26 ...       RpcCalls=0x26(38)
        ///
        /// 这些是帽子/皮肤/宠物/名牌等外观槽位（值 00 01 = 无装扮）。
        /// 房主手里的 ClientData 里我们的 Character 一直是 null，
        /// 而「等玩家数据」很可能就包含这类外观数据 —— 所以这一组一直没发是可疑的。
        /// </summary>
        private static byte[] BuildCosmetics(int gameId, int netId)
        {
            int[] calls = { 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B };
            var nid = PackUInt32((uint)netId);

            int subs = 0;
            foreach (var _ in calls) subs += 2 + 1 + (nid.Length + 1 + 2);

            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(0x01);
            var n = NextNonce();
            ms.WriteByte((byte)(n >> 8));
            ms.WriteByte((byte)(n & 0xFF));
            int gdLen = 4 + subs;
            ms.WriteByte((byte)(gdLen & 0xFF));
            ms.WriteByte((byte)((gdLen >> 8) & 0xFF));
            ms.WriteByte(0x05);
            var g = BitConverter.GetBytes(gameId); ms.Write(g, 0, 4);

            foreach (var call in calls)
            {
                int sl = nid.Length + 1 + 2;            // netId + RpcCalls + 值(2B)
                ms.WriteByte((byte)(sl & 0xFF));
                ms.WriteByte((byte)((sl >> 8) & 0xFF));
                ms.WriteByte(0x02);                     // tag = RPC
                ms.Write(nid, 0, nid.Length);
                ms.WriteByte((byte)call);
                ms.WriteByte(0x00); ms.WriteByte(0x01);
            }
            return ms.ToArray();
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



        /// <summary>通用 RPC 包（Reliable + GameData + RpcFlag）</summary>
        private static byte[] BuildRpc(int gameId, int netId, byte callId, byte[] payload)
        {
            var nid = PackUInt32((uint)netId);
            int subLen = nid.Length + 1 + payload.Length;
            int gdLen = 4 + 2 + 1 + subLen;
            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(0x01);
            var n = NextNonce();
            ms.WriteByte((byte)(n >> 8)); ms.WriteByte((byte)(n & 0xFF));
            ms.WriteByte((byte)(gdLen & 0xFF)); ms.WriteByte((byte)((gdLen >> 8) & 0xFF));
            ms.WriteByte(0x05);
            var g = BitConverter.GetBytes(gameId); ms.Write(g, 0, 4);
            ms.WriteByte((byte)(subLen & 0xFF)); ms.WriteByte((byte)((subLen >> 8) & 0xFF));
            ms.WriteByte(0x02);                       // RpcFlag
            ms.Write(nid, 0, nid.Length);
            ms.WriteByte(callId);
            ms.Write(payload, 0, payload.Length);
            return ms.ToArray();
        }

        /// <summary>静默发送（不打印日志）—— 位置包频率高，全打会刷屏</summary>
        private static void SendPos(byte[] pkt)
        {
            try { lock (_udp) _udp.Send(pkt, pkt.Length); } catch { }
        }

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
