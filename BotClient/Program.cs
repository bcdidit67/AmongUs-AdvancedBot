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
        private static int _stuckTicks;               // 连续「无处可站」的次数
        private static bool _resending;               // 正在重发 SceneChange
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
            new Thread(SceneWatchLoop) { IsBackground = true }.Start();  // ★ 阶段变化时重发 SceneChange

            {
                string g2 = Environment.GetEnvironmentVariable("AMONGUS_DIR");
                if (string.IsNullOrEmpty(g2))
                    g2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                      ".local/share/Steam/steamapps/common/Among Us");
                string lp = Path.Combine(g2, "BepInEx/LogOutput.log");
                TryLoadPolys(lp);
                if (!TryLoadGeometry(lp))
                    Console.WriteLine("      ⚠️ 未解析到碰撞几何 —— 寻路会原地不动（等场景变化后会重试）");
            }

            // 1.8 ★ 用插件报出来的**真实** GameId 覆盖命令行参数
            //     （写死 32 会导致 IncorrectGame，反复重试还会把房主弄掉线）
            {
                string gd = Environment.GetEnvironmentVariable("AMONGUS_DIR");
                if (string.IsNullOrEmpty(gd))
                    gd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                      ".local/share/Steam/steamapps/common/Among Us");
                string lp2 = Path.Combine(gd, "BepInEx/LogOutput.log");
                int realGid = -1;
                // ★ 等一下：插件每秒才检查一次，而且 0（不在游戏中）要跳过
                for (int w = 0; w < 20 && realGid < 0; w++)
                {
                    realGid = TryLoadGameId(lp2);
                    if (realGid < 0) Thread.Sleep(500);
                }
                if (realGid >= 0 && realGid != gameId)
                {
                    Console.WriteLine($"      ★ GameId 用日志里的 {realGid}（命令行给的是 {gameId}）");
                    gameId = realGid;
                    _gameId = realGid;
                }
                else if (realGid < 0)
                {
                    Console.WriteLine($"      ⚠️ 日志里没有 GameId（插件没重载？）—— 沿用命令行的 {gameId}");
                }
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
                // ★★★ 0x0B = Fragment（可靠分片）—— 必须 ACK！★★★
                //
                // Hazel 把超过 MTU 的可靠消息切成多个分片，每个分片都是 Reliable 语义，
                // **必须逐个 ACK**。我们之前只 ACK 0x01(Reliable) 和 0x0c(Ping)，
                // 把 0x0b 漏了 —— 于是房主发来的大消息（新人加入时的 SendInitialData
                // 要把房主当前所有对象序列化一遍）永远收不到确认，
                // 重发 9 次 / 7.5 秒后房主断开。
                //
                // 实测症状完全吻合：
                //   · 每多一个人 SendInitialData 就更大 → 分片更多
                //   · 「前 5 台顺利，第 6 台起 IncorrectGame 级联失败」
                //   · 用户截图里的 "Reliable packet N was not ack'd after 7506ms (9 resends)"
                //
                // 分片格式与 Reliable 相同： [0x0b][id BE16][数据...]
                if (so == 0x0b && d.Length >= 3)          // Fragment（可靠分片）→ 回 Ack
                {
                    ushort fn = (ushort)((d[1] << 8) | d[2]);
                    SendPos(BuildAck(fn));                // 静默 ACK（分片量可能很大）
                }
                else if (so == 0x0c && d.Length >= 3)     // Ping → 回 Ack
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
                if (tag == 0x04)                                    // ★ 房主发来的 Spawn
                {
                    TryExtractOurNetId(d, pos, len);
                    World.OnSpawn(d, pos, len);                     // ★ 感知：记录 netId ↔ ownerId
                }
                if (tag == 0x01) World.OnData(d, pos, len);         // ★ 感知：别人的位置

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
        private class Box { public float x0, y0, x1, y1; public bool Room; public int Layer; }

        private static readonly System.Collections.Generic.List<Box> _boxes
            = new System.Collections.Generic.List<Box>();
        private static string _geoScene = "";
        private static int _geoBeginLine = -1;   // 上一段 [COL] 的起始行号（用来判断是否换了一段）
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

                    // ★★★ 判据按**实测数据**写，不是照大厅类推 ★★★
                    //
                    // 大厅（SCENE LOBBY）：
                    //   layer 9  Lobby(Clone) 21.0    ← 地板（真正可走）
                    //   layer 9  ShipRoom     35.3    ← 包住地板的墙圈（靠嵌套剔除）
                    //   layer 9/0 小箱子 area<5       ← 障碍
                    //
                    // 地图（SCENE SHIP，241 个碰撞体，实测层分布）：
                    //   layer  2  21 个 AreaCollider 总面积 757  ← ★ 这才是「可行走区域」
                    //   layer  9  44 个 Ground       总面积 680  ← 只是地板贴图，别当房间
                    //   layer 10  37 个 Room                     ← 房间触发区
                    //   layer 12  89 个 桌子/栏杆等               ← ★ 障碍
                    //   layer  8  玩家                            ← 必须排除
                    var b = new Box { x0 = x0, y0 = y0, x1 = x1, y1 = y1, Room = false, Layer = layer };

                    // ★ 两个阶段用**完全不同**的规则 —— 实测层分布差异极大
                    bool isShip = (scene == "SHIP");
                    if (isShip)
                    {
                        // 地图：可走 = layer 2 的 AreaCollider（21 个，总面积 757）
                        //       ★ 绝不能把 layer 9 的 Ground 当地板 —— 那只是贴图，
                        //         它们散落在各处，放行后会走到墙外（实测 42 个"房间"时越界）
                        // ★★★ 可走 = AreaCollider 与 Ground 的**交集** ★★★
                        //
                        // 实测（1226 个轨迹点统计）：
                        //   在 AreaCollider 内且踩在 Ground 上   约 75%
                        //   在 AreaCollider 内但**不在** Ground 上 约 25%  ← ★ 这些位置在墙里
                        //     （AreaCollider 是矩形包围盒，把不规则房间补成了直角，
                        //       补出来的那些角在真实游戏里是墙）
                        //
                        // 所以两层都要满足：AreaCollider 划定大范围，Ground 给出真实地板形状。
                        if (layer == 2)                     b.Room = true;    // 区域
                        else if (layer == 9)                b.Room = true;    // 地板（与上面取交集）
                        else if (layer == 12 && area < 20f) b.Room = false;   // 桌椅栏杆
                        else continue;
                    }
                    else
                    {
                        // 大厅：可走 = layer 9 的大块（Lobby(Clone)），墙圈靠嵌套剔除
                        if (layer == 9 && area > 15f)       b.Room = true;
                        else if (area < 5f && (layer == 9 || layer == 0)) b.Room = false;
                        else continue;
                    }

                    tmp.Add(b);
                    if (x0 < mnx) mnx = x0; if (y0 < mny) mny = y0;
                    if (x1 > mxx) mxx = x1; if (y1 > mxy) mxy = y1;
                }
                if (tmp.Count == 0) return false;
                // ★ 和插件一样：大厅与地图都叫 "OnlineGame"，
                //   所以不能靠场景名判断要不要重新载入。
                //   插件在换阶段时会输出**新的一段 [COL]**，
                //   我们只在「日志里最后一段 [COL] 的起始行号」变化时才重新载入。
                if (begin == _geoBeginLine) return false;

                // ★★★ 排除「包住其它房间的外圈」★★★
                //
                // 实测数据：大厅里有两个 Ship 层的框，而且是**嵌套**的 ——
                //     Lobby(Clone)  x[-2.73, 2.81]   ← 地板，真正可行走
                //     ShipRoom      x[-3.45, 3.46]   ← 包住地板的一圈墙
                // 之前的判据是「在任一房间内即可」，于是 ShipRoom 放行了墙的厚度，
                // 人机走到 x=3.10（超过地板的 2.81）—— 看起来就是穿墙。
                //
                // 规则：若某个房间框**完全包含**另一个房间框，那它是墙圈，剔除。
                // ⚠️ 这个剔除规则只适用于**大厅**：
                //    大厅里 Lobby(Clone)（地板）被 ShipRoom（墙圈）完全包住，都是 layer 9。
                //    而地图的 AreaCollider(layer 2) 之间有重叠但都是**合法的可走区域** ——
                //    对它们套用「包住别人就剔除」会误删大片区域（实测剔掉了三块）。
                //    所以只在 layer 9 上做这件事。
                var roomList = tmp.FindAll(b => b.Room && b.Layer == 9);
                var inner = new System.Collections.Generic.List<Box>();
                foreach (var a in roomList)
                {
                    bool containsOther = false;
                    foreach (var b in roomList)
                    {
                        if (ReferenceEquals(a, b)) continue;
                        // b 是否完全落在 a 内部（留 0.1 容差）
                        if (b.x0 > a.x0 - 0.1f && b.x1 < a.x1 + 0.1f &&
                            b.y0 > a.y0 - 0.1f && b.y1 < a.y1 + 0.1f)
                        { containsOther = true; break; }
                    }
                    if (!containsOther) inner.Add(a);
                    else Console.WriteLine($"      · 剔除墙圈 {a.x0:F1},{a.y0:F1} ~ {a.x1:F1},{a.y1:F1}（它包住了别的房间）");
                }
                if (inner.Count > 0)
                {
                    tmp.RemoveAll(b => b.Room && b.Layer == 9 && !inner.Contains(b));
                }

                lock (_boxes)
                {
                    _boxes.Clear(); _boxes.AddRange(tmp);
                    _mapMinX = mnx; _mapMinY = mny; _mapMaxX = mxx; _mapMaxY = mxy;
                }
                _geoScene = scene;
                _geoBeginLine = begin;
                int rooms = tmp.FindAll(b => b.Room).Count;
                Console.WriteLine($"      ★★★ 几何载入 [{scene}]：房间 {rooms} 个，障碍 {tmp.Count - rooms} 个，" +
                                  $"范围 x[{mnx:F1},{mxx:F1}] y[{mny:F1},{mxy:F1}]");
                return true;
            }
            catch (Exception e) { Console.WriteLine($"      [几何] 解析失败: {e.Message}"); return false; }
        }

        /// <summary>
        /// 可走判定。
        ///
        /// 大厅：在 layer 9 的房间内，且不在障碍内。
        /// 地图：必须**同时**满足两类 ——
        ///       ① 在某个 layer 2 的 AreaCollider 内（划定大范围）
        ///       ② 踩在某个 layer 9 的 Ground 上（真实地板形状）
        ///   只满足①会在墙里（AreaCollider 是矩形包围盒，把不规则房间补成了直角）——
        ///   实测 1226 个轨迹点里有约 25% 落在这种地方。
        /// </summary>
        /// <summary>SendChat(13) —— 发言。载荷是「长度前缀字符串」。</summary>
        private static byte[] BuildChat(int gameId, int pcNetId, string msg)
        {
            var body = new System.Collections.Generic.List<byte>();
            var utf = System.Text.Encoding.UTF8.GetBytes(msg);
            // 长度用压缩整数写法（长度 < 128 时就是一个字节）
            if (utf.Length < 128) body.Add((byte)utf.Length);
            else { body.Add((byte)((utf.Length & 0x7F) | 0x80)); body.Add((byte)(utf.Length >> 7)); }
            body.AddRange(utf);
            return BuildRpc(gameId, pcNetId, 13, body.ToArray());
        }

        /// <summary>RepairSystem(28) —— 内鬼用它**破坏**系统。
        /// 载荷: [byte SystemID][packed PlayerControl netId][byte Amount]</summary>
        private static byte[] BuildSabotage(int gameId, int shipNetId, byte systemId, int pcNetId)
        {
            var body = new System.Collections.Generic.List<byte> { systemId };
            body.AddRange(PackUInt32((uint)pcNetId));
            body.Add(0x00);                      // Amount=0（对破坏而言）
            return BuildRpc(gameId, shipNetId, 28, body.ToArray());
        }

        /// <summary>CloseDoorsOfType(27) —— 关门。载荷: [byte SystemID]</summary>
        private static byte[] BuildCloseDoors(int gameId, int shipNetId, byte systemId)
            => BuildRpc(gameId, shipNetId, 27, new byte[] { systemId });

        /// <summary>游戏日志路径（多处要用，统一一处避免环境变量读法不一致）</summary>
        internal static string GameLogPath()
        {
            string gd = Environment.GetEnvironmentVariable("AMONGUS_DIR");
            if (string.IsNullOrEmpty(gd))
                gd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                  ".local/share/Steam/steamapps/common/Among Us");
            return Path.Combine(gd, "BepInEx/LogOutput.log");
        }

        /// <summary>给 Ai.cs 用的可走判定（AI 不直接碰几何细节）</summary>
        internal static bool WalkablePublic(float x, float y) => Walkable(x, y);

        private static bool Walkable(float x, float y)
        {
            // ★ 有真实形状就用真实形状（插件已导出 [POLY]）
            lock (_polys) { if (_polys.Count > 0) return WalkableBody(x, y); }

            const float R = 0.35f;
            bool inArea = false, onGround = false, hasArea = false;
            lock (_boxes)
            {
                foreach (var b in _boxes)
                {
                    if (b.Layer == 2) hasArea = true;              // 本场景有没有区域层
                    if (!b.Room) continue;
                    if (x > b.x0 + R && x < b.x1 - R && y > b.y0 + R && y < b.y1 - R)
                    {
                        if (b.Layer == 2) inArea = true;
                        else if (b.Layer == 9) onGround = true;
                    }
                }
                // 地图（有 layer 2）：必须在区域**且**踩在地板上
                // 大厅（只有 layer 9）：在地板范围内即可
                bool ok = hasArea ? (inArea && onGround) : onGround;
                if (!ok) return false;
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
            // ★★★ 行为 AI 初始化 ★★★
            var ai = new Ai(Environment.ProcessId);
            Console.WriteLine($"      ★ 性格: 停留 {ai.PauseMin:F1}~{ai.PauseMax:F1}s  " +
                              $"游荡半径 {ai.WanderRadius:F1}  下刀距离 {ai.KillReach:F2}  怕人程度 {ai.CrowdFear:F2}");
            World.MyNetId = -1;              // 由主循环更新

            int _aiTick = 0;
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
                        TryLoadPolys(Path.Combine(gd, "BepInEx/LogOutput.log"));
                        TryLoadGeometry(Path.Combine(gd, "BepInEx/LogOutput.log"));
                    }

                    // ★★★ 刀后掩饰优先于一切（用户在意的「别秒刀自爆」）★★★
                    if (ai.StepCover(_posX, _posY, out float cvx, out float cvy))
                    {
                        var cp = FindPath(_posX, _posY, cvx, cvy);
                        if (cp != null && cp.Count > 0) { path = cp; idx = 0; tx = cvx; ty = cvy; }
                    }
                    else if (ai.WantsToReport && _ourNetId >= 0)
                    {
                        // ★ 走回尸体旁了 → 拍桌报告，然后发一句话装作刚发现
                        Console.WriteLine($"      ★★★ 报告尸体（装作刚发现）[{ai.State}]");
                        SendRaw("ReportDeadBody(尸体)", BuildRpc(_gameId, _ourNetId, 11, new byte[] { 0xFF }));
                        ai.ReportDone();
                        var ct = new Thread(() =>
                        {
                            try
                            {
                                Thread.Sleep(1200 + _rng.Next(1500));
                                string[] msgs = {
                                    "刚才路过看到的…",
                                    "这谁干的",
                                    "我在附近做任务，一转头就看到了",
                                    "有人看到谁在这边吗"
                                };
                                string m = msgs[_rng.Next(msgs.Length)];
                                Console.WriteLine($"      → 发言: {m}");
                                SendRaw("SendChat", BuildChat(_gameId, _ourNetId, m));
                            }
                            catch { }
                        }) { IsBackground = true };
                        ct.Start();
                    }

                    // ★★★ 若自己站在墙里（出生点、或上次被挤出去），
                    //     先朝最近的合法格走 —— 否则 A* 起点非法，永远规划不出路径，
                    //     表现出来就是「站在原地一动不动」（实测踩过）。
                    if (!Walkable(_posX, _posY))
                    {
                        // ★★ 连续 10 次（约 2 秒）都无处可站 → 说明「我在这个世界里
                        //    根本没有立足之地」：多半是换了场景而房主还没给我建角色
                        //    （位置还是上一个场景的坐标，用新场景的几何判定自然全不可走）。
                        //    此时重发 ClientInfo + SceneChange，让房主重建角色。
                        if (++_stuckTicks >= 10 && !_resending)
                        {
                            _stuckTicks = 0;
                            _resending = true;
                            Console.WriteLine("      ★★★ 连续无处可站 —— 判断为换场景未重建角色，重发 ClientInfo + SceneChange");
                            var tr = new Thread(() =>
                            {
                                try
                                {
                                    Thread.Sleep(300);
                                    SendRaw("ClientInfo(自救)", BuildClientInfo(_gameId, _myClientId, 2));
                                    Thread.Sleep(300);
                                    SendRaw("SceneChange(自救)", BuildSceneChange(_gameId, _myClientId, "OnlineGame"));
                                    Thread.Sleep(1500);
                                }
                                catch { }
                                _resending = false;
                            }) { IsBackground = true };
                            tr.Start();
                        }
                        float ex = 0f, ey = 0f; bool found = false;
                        for (int r = 1; r <= 12 && !found; r++)
                            for (int dj = -r; dj <= r && !found; dj++)
                                for (int di = -r; di <= r && !found; di++)
                                {
                                    if (Math.Abs(di) != r && Math.Abs(dj) != r) continue;
                                    float wx = _posX + di * 0.5f, wy = _posY + dj * 0.5f;
                                    if (Walkable(wx, wy)) { ex = di * 0.5f; ey = dj * 0.5f; found = true; }
                                }
                        if (found)
                        {
                            float l = MathF.Sqrt(ex * ex + ey * ey);
                            if (l > 0.001f) { _posX += ex / l * Step; _posY += ey / l * Step; }
                            path.Clear(); idx = 0;
                            _seq++;
                            var esc = BuildPosition(_gameId, _cntNetId, _seq, _posX, _posY);
                            if (_seq % 5 == 0) SendRaw($"脱困({_posX:F1},{_posY:F1})", esc);
                            else SendPos(esc);
                        }
                        continue;
                    }

                    _stuckTicks = 0;      // 能站住 → 清零

                    if (idx >= path.Count)
                    {
                        // ★★★ 目标点改由 AI 决定（不再纯随机）★★★
                        //
                        // AI 会考虑：内鬼找人下刀、船员装作做任务、
                        //           到达后停留几秒（外面看起来像在做事）。
                        // 找不到路时退回随机点，避免卡死。
                        bool okPath = false;
                        var desire = ai.Decide(_posX, _posY, true);

                        if (desire == null)
                        {
                            // AI 说「原地待着」（正在装做任务）——
                            // 但仍要发位置包，否则在别人眼里我们会「消失」。
                            _seq++;
                            var idlePkt = BuildPosition(_gameId, _cntNetId, _seq, _posX, _posY);
                            if (_seq % 5 == 0) SendRaw($"原地({_posX:F1},{_posY:F1}) [{ai.State}]", idlePkt);
                            else SendPos(idlePkt);

                            // 顺手看看要不要下刀
                            int kt = ai.KillTargetNetId(_posX, _posY);
                            if (kt > 0 && _ourNetId >= 0)
                            {
                                Console.WriteLine($"      ★★★ AI 下刀 → netId={kt} [{ai.State}]");
                                SendRaw($"MurderPlayer(netId={kt})",
                                        BuildRpc(_gameId, _ourNetId, 12, PackUInt32((uint)kt)));
                                // ★ 刀完不要立刻报 —— 开始掩饰，等几秒再装作刚发现
                                ai.OnKilled(_posX, _posY);
                            }
                            continue;
                        }

                        var aiPath = FindPath(_posX, _posY, desire.Value.x, desire.Value.y);
                        if (aiPath != null && aiPath.Count > 0)
                        { path = aiPath; idx = 0; okPath = true; tx = desire.Value.x; ty = desire.Value.y; }

                        for (int attempt = 0; attempt < 15 && !okPath; attempt++)
                        {
                            float gx = _mapMinX + (float)_rng.NextDouble() * (_mapMaxX - _mapMinX);
                            float gy = _mapMinY + (float)_rng.NextDouble() * (_mapMaxY - _mapMinY);
                            if (!Walkable(gx, gy)) continue;
                            var p2 = FindPath(_posX, _posY, gx, gy);
                            if (p2 == null || p2.Count == 0) continue;
                            path = p2; idx = 0; okPath = true; tx = gx; ty = gy;
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
                        float nx, ny;
                        if (dist < Step) { nx = path[idx].x; ny = path[idx].y; }
                        else { nx = _posX + dx / dist * Step; ny = _posY + dy / dist * Step; }

                        // ★★ 避墙：这一步的**终点**和**中点**都必须可走。
                        //    只查终点不够 —— 起点和终点都在合法区、中间却隔着墙角时，
                        //    直线走过去就会蹭墙/穿角（实测出现过）。
                        bool clear = Walkable(nx, ny) &&
                                     Walkable((_posX + nx) / 2f, (_posY + ny) / 2f);
                        if (clear)
                        {
                            if (dist < Step) idx++;      // 到达这个路点
                            _posX = nx; _posY = ny;
                        }
                        else
                        {
                            // 前面被挡 → 丢掉当前路径，下一轮重新规划（A* 会绕开）
                            path.Clear(); idx = 0;
                        }
                    }

                    // ★ 内鬼定期破坏（关灯/氧气/反应堆）——制造混乱，也是战术
                    if (ai.ShouldSabotage() && World.ShipStatusNetId >= 0 && _ourNetId >= 0)
                    {
                        byte sys = ai.PickSabotage();
                        string nm = sys == 7 ? "关灯" : sys == 8 ? "氧气" : "反应堆";
                        Console.WriteLine($"      ★★★ 内鬼破坏 → {nm}(SystemType={sys})");
                        SendRaw($"RepairSystem(破坏 {nm})",
                                BuildSabotage(_gameId, World.ShipStatusNetId, sys, _ourNetId));
                    }

                    World.MyNetId = _ourNetId;
                    World.MyOwner = _myClientId;
                    World.NoteOwnPosition(_posX, _posY);
                    if (++_aiTick % 10 == 0)
                        World.RefreshFromLog(GameLogPath());

                    _seq++;
                    // ★ 速度 = 朝当前路点的方向 × 步速（1 单位/秒），接收方靠它算走路动画
                    float vx = 0f, vy = 0f;
                    if (idx < path.Count)
                    {
                        float vdx = path[idx].x - _posX, vdy = path[idx].y - _posY;
                        float vd = MathF.Sqrt(vdx * vdx + vdy * vdy);
                        if (vd > 0.001f) { vx = vdx / vd * 1.0f; vy = vdy / vd * 1.0f; }
                    }
                    var pkt = BuildPosition(_gameId, _cntNetId, _seq, _posX, _posY, vx, vy);
                    if (_seq % 5 == 0)
                        SendRaw($"位置({_posX:F1},{_posY:F1})→目标({tx:F1},{ty:F1}) v=({vx:F1},{vy:F1})", pkt);
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
            // ★ 标志 0x01（只含位置）—— 这是**唯一实测可用**的取值。
            //
            // 试过的失败组合（两次都把正在进行的对局弄坏）：
            //   · 0x03 + 速度(4)，同时把坐标范围改成 ±40  → 人全消失 + 连接中断
            //   · 0x03 + 速度(4)，坐标范围保持 ±50        → 人全消失
            //
            // 结论：那个字节在 v19 里**不是**「脏位掩码」，
            //       不能按 SetDirtyBit(3) 的语义去填 0x03。
            //       在拿到 v19 的准确语义前，固定用 0x01。
            ms.WriteByte(0x01);
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


        /// <summary>
        /// 从游戏日志里读真实 GameId（插件输出的 [GAME] ★ GameId = N）。
        ///
        /// ★ 为什么必须这样：本地游戏的 gameId **不是固定的 32**，每次开房可能不同。
        ///   写错时服务端回 IncorrectGame；反复重试还会把房主弄掉线
        ///   （实测：每次都是 5/14 或 13/14 就位之后房主连接中断）。
        /// </summary>
        private static int TryLoadGameId(string logPath)
        {
            try
            {
                if (!File.Exists(logPath)) return -1;
                var lines = File.ReadAllLines(logPath);
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(lines[i], @"\[GAME\] ★ GameId = (-?\d+)");
                    if (!m.Success) continue;
                    if (!int.TryParse(m.Groups[1].Value, out int g)) continue;
                    // ★ 跳过 0 和负数：GameId=0 表示「不在任何游戏里」，
                    //   它是房主离开房间时的瞬时值，日志里经常夹在中间。
                    //   实测踩过这个坑：读到 0 → JoinGame 被拒 → 只有 2/14 就位。
                    if (g <= 0) continue;
                    return g;
                }
            }
            catch { }
            return -1;
        }


        // ═══════════════════════════════════════════════════════════
        // ★★★ 真实多边形可走判定 ★★★
        //
        // 插件导出的 [POLY] 段给出每个碰撞体的**真实顶点**（不再用包围盒）：
        //     [POLY] BEGIN <总数>
        //     <层> <点数> x1 y1 x2 y2 ...
        //     [POLY] END
        //
        // 对照用户的示意图：
        //     绿色（正常人可走） = layer 2 的 AreaCollider 轮廓（不规则）
        //     红色（有墙、内部可走）= layer 9 的 Ground 之外 / layer 12 的家具
        //     紫色（谁都过不去）   = 最外层，无所谓，绿色区离它很远
        //
        // 判据：在任一 layer2 多边形内 **且** 在任一 layer9 多边形内 **且** 不在任何 layer12 内
        // ═══════════════════════════════════════════════════════════
        private class Poly { public int Layer; public float[] X, Y; }

        private static readonly System.Collections.Generic.List<Poly> _polys
            = new System.Collections.Generic.List<Poly>();
        private static int _polyBeginLine = -1;

        private static bool TryLoadPolys(string logPath)
        {
            try
            {
                if (!File.Exists(logPath)) return false;
                var lines = File.ReadAllLines(logPath);
                int begin = -1;
                for (int i = lines.Length - 1; i >= 0; i--)
                    if (lines[i].Contains("[POLY] BEGIN")) { begin = i; break; }
                if (begin < 0) return false;
                if (begin == _polyBeginLine) return false;      // 没换段就不重复解析

                var tmp = new System.Collections.Generic.List<Poly>();
                for (int i = begin + 1; i < lines.Length; i++)
                {
                    var L = lines[i];
                    if (L.Contains("[POLY] END")) break;
                    var f = L.Split(' ');
                    if (f.Length < 4) continue;
                    if (!int.TryParse(f[0], out int layer)) continue;
                    if (!int.TryParse(f[1], out int n)) continue;
                    if (f.Length < 2 + n * 2) continue;
                    if (layer != 2 && layer != 9 && layer != 12) continue;   // 只关心这三层
                    var poly = new Poly { Layer = layer, X = new float[n], Y = new float[n] };
                    for (int k = 0; k < n; k++)
                    {
                        float.TryParse(f[2 + k * 2], System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out poly.X[k]);
                        float.TryParse(f[3 + k * 2], System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out poly.Y[k]);
                    }
                    tmp.Add(poly);
                }
                if (tmp.Count == 0) return false;
                lock (_polys) { _polys.Clear(); _polys.AddRange(tmp); }
                _polyBeginLine = begin;
                int c2 = tmp.FindAll(q => q.Layer == 2).Count;
                int c9 = tmp.FindAll(q => q.Layer == 9).Count;
                int c12 = tmp.FindAll(q => q.Layer == 12).Count;
                Console.WriteLine($"      ★★★ 形状已载入：区域 {c2} 个、地板 {c9} 个、障碍 {c12} 个");
                return true;
            }
            catch (Exception e) { Console.WriteLine($"      [形状] 解析失败: {e.Message}"); return false; }
        }

        /// <summary>射线法：点是否在多边形内</summary>
        private static bool InPoly(Poly p, float x, float y)
        {
            bool inside = false;
            int n = p.X.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if (((p.Y[i] > y) != (p.Y[j] > y)) &&
                    (x < (p.X[j] - p.X[i]) * (y - p.Y[i]) / (p.Y[j] - p.Y[i]) + p.X[i]))
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>
        /// ★★ 带半径的可走判定 —— 判**一圈点**，不只是一个点。
        ///
        /// 实测（4336 个轨迹点）：只判中心点时，8.5% 的位置虽然在区域外/障碍内
        /// 却仍被判为可走 —— 因为角色有半径（约 0.36），
        /// 中心点合法但**身体已经压在墙上或家具上**。
        ///
        /// 这里取中心 + 半径上 8 个方向共 9 个点，**全部**合法才算可走。
        /// </summary>
        private const float BodyR = 0.36f;

        private static bool WalkableBody(float x, float y)
        {
            if (!WalkablePoly(x, y)) return false;
            for (int k = 0; k < 8; k++)
            {
                float a = k * MathF.PI / 4f;
                if (!WalkablePoly(x + MathF.Cos(a) * BodyR, y + MathF.Sin(a) * BodyR)) return false;
            }
            return true;
        }

        /// <summary>
        /// 可走：在任一 layer2 区域内 且 在任一 layer9 地板上 且 不在任何 layer12 障碍内。
        /// 三层都用**真实顶点**判定，不再用包围盒（包围盒会把不规则房间补成直角，
        /// 补出来的部分正好是红区 —— 表现为「穿墙后在墙内活动」）。
        /// </summary>
        private static bool WalkablePoly(float x, float y)
        {
            bool inArea = false, onGround = false, hasArea = false, hasGround = false;
            lock (_polys)
            {
                if (_polys.Count == 0) return true;      // 还没载入 → 不做限制
                foreach (var p in _polys)
                {
                    if (p.Layer == 2) { hasArea = true; if (!inArea && InPoly(p, x, y)) inArea = true; }
                    else if (p.Layer == 9) { hasGround = true; if (!onGround && InPoly(p, x, y)) onGround = true; }
                    else if (p.Layer == 12 && InPoly(p, x, y)) return false;   // 撞到家具
                }
                if (hasArea && hasGround) return inArea && onGround;
                if (hasArea) return inArea;
                return onGround;
            }
        }


        /// <summary>
        /// ★★★ 阶段变化时重发 SceneChange ★★★
        ///
        /// 房主的流程（源码）：
        ///     OnPlayerChangedScene(client, scene)
        ///         → client.InScene = true
        ///         → SendInitialData(client.Id)     // 把当前所有对象发给这个客户端
        ///         → CreatePlayer(client)           // 给他在**当前场景**里建角色
        ///
        /// 真客户端每次进新场景都会自己发一次 SceneChange ——
        /// 而我们的人机只在加入时发过一次大厅那次，
        /// 于是进地图后房主**没有**为我们建角色：
        ///   netId 还是大厅的、位置还是大厅的、发出去的位置落在过期对象上，
        ///   表现出来就是「站着不动 / 位置停在大厅」。
        ///
        /// 这里监听插件输出的 [GRID] 阶段 = SHIP/LOBBY，
        /// 一旦发现阶段变了就重发 ClientInfo + SceneChange。
        /// </summary>
        private static void SceneWatchLoop()
        {
            string last = "";
            int dbg = 0;
            Console.WriteLine("      [场景监听] 线程已启动");
            while (true)
            {
                try
                {
                    Thread.Sleep(1000);
                    string gd = Environment.GetEnvironmentVariable("AMONGUS_DIR");
                    if (string.IsNullOrEmpty(gd))
                        gd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                          ".local/share/Steam/steamapps/common/Among Us");
                    string lp = Path.Combine(gd, "BepInEx/LogOutput.log");
                    if (!File.Exists(lp)) continue;

                    // ★★★ 用「[POLY] 段的行号」判断换场景，**不要用阶段名** ★★★
                    //
                    // 踩过的坑：日志里最后一条 [GRID] 阶段往往是**上一局**留下的
                    // （比如上局进过地图，值就是 SHIP），人机一启动就读到 SHIP，
                    // 等它真的进地图时值还是 SHIP —— 检测不到变化，永远不会重发。
                    // 而行号一定会随插件重导几何而变化，是可靠的判据。
                    var lines = File.ReadAllLines(lp);
                    int polyLine = -1;
                    for (int i = lines.Length - 1; i >= 0; i--)
                        if (lines[i].Contains("[POLY] BEGIN")) { polyLine = i; break; }

                    string phase = "";
                    for (int i = lines.Length - 1; i >= 0; i--)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(lines[i], @"\[GRID\] 阶段 = (\w+)");
                        if (m.Success) { phase = m.Groups[1].Value; break; }
                    }
                    if (dbg < 5) { dbg++; Console.WriteLine($"      [场景监听] polyLine={polyLine} 阶段='{phase}' last={last}"); }
                    if (polyLine < 0) continue;
                    string key = polyLine.ToString();
                    if (key == last) continue;
                    if (last == "") { last = key; continue; }        // 第一次只记录
                    last = key;

                    if (_myClientId < 0 || _gameId <= 0) continue;
                    Console.WriteLine($"      ★★★ 检测到换场景（阶段={phase}）—— 重发 ClientInfo + SceneChange，让房主在新场景建角色");
                    _ourNetId = -1; _cntNetId = -1;
                    Thread.Sleep(1500);
                    SendRaw("ClientInfo(换场景)", BuildClientInfo(_gameId, _myClientId, 2));
                    Thread.Sleep(300);
                    SendRaw("SceneChange(换场景)", BuildSceneChange(_gameId, _myClientId, "OnlineGame"));
                }
                catch (Exception e) { Console.WriteLine($"      [场景监听] {e.Message}"); }
            }
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
