// ═══════════════════════════════════════════════════════════════════════════
//  Ai.cs —— 行为 AI（效用评分 + 受限视野）
//
//  设计要点（参考 AAAI 的 CSP4SDG 与 AiLibi 的两层架构，但**不依赖 LLM**）：
//
//   ① 受限视野：机器人只能「看见」视野半径内的玩家。
//      ★ 这不是为了拟真，而是为了**公平** —— 不偷看引擎状态，
//        每个机器人看到的世界不一样，才会产生信息差，
//        有信息差才有博弈（内鬼说谎才有意义）。
//
//   ② 效用评分：每个 tick 给候选行动打分，选最高分。
//      比一堆 if-else 强 —— 因为权重会随局势自然变化，
//      而且不同机器人可以用不同权重（= 不同性格）。
//
//   ③ 两层结构：
//        行为层（每 tick，规则/评分）  ← 本文件
//        推理层（会议时，约束求解）    ← 下一步
//
//  ⚠️ 本文件**不碰任何协议、包格式、发送频率** ——
//     那些是反复验证过的可用配置，AI 只决定「往哪走、做什么」。
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace BotClient
{
    /// <summary>一个玩家的快照 —— 我们知道的一切</summary>
    internal class PlayerSnap
    {
        public int NetId = -1;          // PlayerControl 的 netId
        public int OwnerId = -1;        // = clientId
        public int PlayerId = -1;
        public string Name = "?";
        public int Role;                // 1/7 = 内鬼，其余为船员系
        public bool Alive = true;

        public float X, Y;              // 最后已知位置
        public long LastSeenTicks;      // 什么时候看到的（Environment.TickCount64）
        public bool EverSeen;

        public bool IsImpostor => Role == 1 || Role == 7;
        /// <summary>多久没见过他了（毫秒）—— 太久没见说明记忆过期</summary>
        public long AgeMs => Environment.TickCount64 - LastSeenTicks;
    }

    internal static class World
    {
        // ── 世界模型 ──
        public static readonly Dictionary<int, PlayerSnap> ByNetId = new();
        public static readonly Dictionary<int, PlayerSnap> ByOwner = new();

        public static int MyNetId = -1;
        /// <summary>ShipStatus 的 netId（破坏/关门/修系统的 RPC 要打给它）</summary>
        public static int ShipStatusNetId = -1;
        public static int MyOwner = -1;
        public static float MyX, MyY;
        public static bool AmImpostor;

        /// <summary>视野半径 —— 只在这么远内才「看得见」别人</summary>
        public const float VisionRadius = 5.5f;

        /// <summary>记忆有效期：超过这么久没再看到，位置就不可信了</summary>
        public const long MemoryMs = 4000;

        private static readonly object _lock = new();

        // ═══════════════════════════════════════════════════════════
        //  感知 ①：从 Spawn 里学「netId ↔ ownerId」
        //  载荷: packed SpawnType | packed OwnerId | flags | packed 组件数 | 组件...
        // ═══════════════════════════════════════════════════════════
        public static void OnSpawn(byte[] d, int pos, int len)
        {
            try
            {
                int i = pos, end = Math.Min(d.Length, pos + len);
                uint st = ReadPacked(d, ref i);
                uint owner = ReadPacked(d, ref i);
                if (i >= end) return;
                i++;                       // flags
                uint ncomp = ReadPacked(d, ref i);
                if (ncomp == 0) return;
                int netId = (int)ReadPacked(d, ref i);
                if (netId <= 0) return;

                // SpawnType=0 是 ShipStatus（飞船本体）—— 破坏/关门都要打给它
                if (st == 0) { ShipStatusNetId = netId; return; }
                // SpawnType=4 是 PlayerControl；其余（1=MeetingHud 等）跳过
                if (st != 4) return;

                lock (_lock)
                {
                    if (!ByNetId.TryGetValue(netId, out var p))
                    {
                        p = new PlayerSnap { NetId = netId };
                        ByNetId[netId] = p;
                    }
                    p.OwnerId = (int)owner;
                    ByOwner[(int)owner] = p;
                    if (MyOwner >= 0 && (int)owner == MyOwner) OurObjectDespawned = false;
                }
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════
        //  感知 ②：从 Data(0x01) 子消息里读别人的位置
        //
        //  格式与我们自己发的完全一样（19~24 字节）：
        //     packed netId | uint16 seq | 字节标志 | 位置(4) [| 速度(4)]
        //  位置编码： raw = (v + 50) / 100 * 65535
        // ═══════════════════════════════════════════════════════════
        public static void OnData(byte[] d, int pos, int len)
        {
            try
            {
                int i = pos, end = Math.Min(d.Length, pos + len);
                uint netId = ReadPacked(d, ref i);
                if (i + 2 > end) return;
                i += 2;                                  // seq
                if (i + 1 > end) return;
                byte flags = d[i++];
                if ((flags & 0x01) == 0) return;         // 没有位置字段
                if (i + 4 > end) return;

                float x = DecVec(d[i], d[i + 1]);
                float y = DecVec(d[i + 2], d[i + 3]);

                lock (_lock)
                {
                    if (ByNetId.TryGetValue((int)netId, out var p))
                    {
                        p.X = x; p.Y = y;
                        p.LastSeenTicks = Environment.TickCount64;
                        p.EverSeen = true;
                    }
                }
            }
            catch { }
        }

        private static float DecVec(byte lo, byte hi)
        {
            int raw = lo | (hi << 8);
            return raw / 65535f * 100f - 50f;
        }

        internal static uint ReadPacked(byte[] d, ref int i)
        {
            uint v = 0; int shift = 0;
            while (i < d.Length)
            {
                byte b = d[i++];
                v |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift > 28) break;
            }
            return v;
        }

        // ═══════════════════════════════════════════════════════════
        //  感知 ③：从插件日志读「名字 / 职业 / 生死」
        //
        //  插件每 5 秒输出一行：
        //     [GDP] 玩家表(15): [pid=1 'Fellpillow' owner=0 ...] ...
        //     [GDP] ★★★ 内鬼: pid=14 'Bot07'
        //  ★ 这只用来补全「这个人叫什么、是不是内鬼」——
        //    位置仍然只能靠自己看见（受限视野）。
        // ═══════════════════════════════════════════════════════════
        private static long _lastLogRead;

        public static void RefreshFromLog(string logPath)
        {
            long now = Environment.TickCount64;
            if (now - _lastLogRead < 1000) return;
            _lastLogRead = now;
            try
            {
                if (!File.Exists(logPath)) return;
                var lines = File.ReadAllLines(logPath);
                int from = Math.Max(0, lines.Length - 600);

                for (int i = lines.Length - 1; i >= from; i--)
                {
                    var m = Regex.Match(lines[i],
                        @"pid=(\d+)\s+'([^']*)'\s*owner=(\d+)");
                    if (!m.Success) continue;
                    int pid = int.Parse(m.Groups[1].Value);
                    string nm = m.Groups[2].Value;
                    int own = int.Parse(m.Groups[3].Value);
                    lock (_lock)
                    {
                        if (ByOwner.TryGetValue(own, out var p))
                        {
                            p.PlayerId = pid;
                            if (!string.IsNullOrEmpty(nm)) p.Name = nm;
                        }
                    }
                }

                // 内鬼名单（插件单独打一行）
                for (int i = lines.Length - 1; i >= from; i--)
                {
                    if (!lines[i].Contains("★★★ 内鬼:")) continue;
                    foreach (Match m in Regex.Matches(lines[i], @"pid=(\d+)"))
                    {
                        int pid = int.Parse(m.Groups[1].Value);
                        lock (_lock)
                            foreach (var p in ByNetId.Values)
                                if (p.PlayerId == pid) p.Role = 1;
                    }
                    break;
                }
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════
        //  查询：我「看得见」的玩家
        // ═══════════════════════════════════════════════════════════
        public static List<PlayerSnap> Visible()
        {
            var list = new List<PlayerSnap>();
            lock (_lock)
            {
                foreach (var p in ByNetId.Values)
                {
                    if (p.NetId == MyNetId) continue;           // 自己不算
                    if (!p.EverSeen) continue;
                    if (p.AgeMs > MemoryMs) continue;           // 记忆过期
                    float dx = p.X - MyX, dy = p.Y - MyY;
                    if (dx * dx + dy * dy <= VisionRadius * VisionRadius) list.Add(p);
                }
            }
            return list;
        }

        /// <summary>我附近有人吗（用来决定「现在下刀安不安全」）</summary>
        public static int CountNear(float x, float y, float r)
        {
            int n = 0;
            lock (_lock)
            {
                foreach (var p in ByNetId.Values)
                {
                    if (p.NetId == MyNetId || !p.Alive) continue;
                    if (!p.EverSeen || p.AgeMs > MemoryMs) continue;
                    float dx = p.X - x, dy = p.Y - y;
                    if (dx * dx + dy * dy <= r * r) n++;
                }
            }
            return n;
        }

        public static void NoteOwnPosition(float x, float y) { MyX = x; MyY = y; }

        // ═══════════════════════════════════════════════════════
        //  ★★★ 感知 ①.5：Despawn —— 检测「我们自己的角色被销毁」★★★
        //
        //  进地图时房主会销毁大厅的所有对象，**包括我们的角色**。
        //  我们却还在往那个已失效的 netId 发位置包 → 游戏全部忽略
        //  → 角色停在原地不动。
        //  （用户实测「游戏大厅里都不动了」；日志证据：
        //    原地包 140 个 vs 位置包 7 个，且刷屏 Despawn。）
        //
        //  而「无处可站」自救当时没触发 —— 拿旧场景几何去判旧坐标是合法的。
        //  所以必须直接监听 Despawn，这是最准确的信号。
        //
        //  载荷: packed NetId（后面可能还有 flags/原因，取第一个即可）
        // ═══════════════════════════════════════════════════════
        public static volatile bool OurObjectDespawned;

        public static void OnDespawn(byte[] d, int pos, int len)
        {
            try
            {
                int i = pos;
                uint netId = ReadPacked(d, ref i);
                if (MyNetId > 0 && (int)netId == MyNetId) OurObjectDespawned = true;
                lock (_lock) { ByNetId.Remove((int)netId); }
            }
            catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  效用 AI —— 给每个行动打分，选最高的
    // ═══════════════════════════════════════════════════════════════════════
    internal class Ai
    {
        // ── 性格参数（每台随机，让它们行为不同）──
        private readonly Random _rng;
        public readonly float PauseMin, PauseMax;   // 在「任务点」停留多久
        public readonly float WanderRadius;         // 逛多远
        public readonly float KillReach;            // 下刀距离
        public readonly float CrowdFear;            // 多怕被人看见（越大越怂）

        private float _targetX, _targetY;
        private long _pauseUntil;
        private bool _targetSet;      // 目标是否已经设过（防止 (0,0) 默认值被当成目标）
        private string _state = "闲逛";

        public string State => _state;

        public Ai(int seed)
        {
            _rng = new Random(seed);
            PauseMin = 1.5f + (float)_rng.NextDouble() * 2f;        // 1.5~3.5 秒
            PauseMax = PauseMin + 1f + (float)_rng.NextDouble() * 3f;
            WanderRadius = 4f + (float)_rng.NextDouble() * 8f;
            KillReach = 1.2f + (float)_rng.NextDouble() * 0.6f;
            CrowdFear = 0.5f + (float)_rng.NextDouble() * 1.5f;
            // ★ 掩饰时长：急躁型 2~4 秒就报，谨慎型能等 6~12 秒
            CoverDelayMin = 2f + (float)_rng.NextDouble() * 4f;
            CoverDelayMax = CoverDelayMin + 2f + (float)_rng.NextDouble() * 6f;
            SabotageCooldown = 25f + (float)_rng.NextDouble() * 40f;
        }

        /// <summary>
        /// 决策：返回「下一步往哪走」。返回 null 表示原地不动（在假装做任务）。
        /// </summary>
        public (float x, float y)? Decide(float myX, float myY, bool walkableOk)
        {
            long now = Environment.TickCount64;

            // ── 正在「做任务」的停留中 ──
            if (now < _pauseUntil) { _state = "停留(装做任务)"; return null; }

            // ★★★ 目标合法性检查 —— 必须放在最前面 ★★★
            //
            // 踩过的坑：_targetX/_targetY 初始是 (0,0)，而 (0,0) 在大厅里
            // 可能正落在障碍物上 → FindPath 永远失败 → _hasAiTarget 被清空
            // → 再问 AI → 又返回 (0,0) → 死循环。
            // 表现就是机器人**永远站在原地发「原地」包**（实测连续 22 次）。
            //
            // 所以：没设过目标、或目标已不可走 → 立刻重选一个。
            if (!_targetSet || !Program.WalkablePublic(_targetX, _targetY))
            {
                PickNewTarget(myX, myY);
                _targetSet = true;
                if (!Program.WalkablePublic(_targetX, _targetY))
                {
                    // 连选都选不出来（几何没载入等）—— 随便朝一个方向挪，别卡死
                    _state = "无处可去(等几何)";
                    return null;
                }
            }

            // ── 内鬼：附近有落单的船员 → 下刀（这是最高优先级行动）──
            if (World.AmImpostor)
            {
                var vis = World.Visible();
                PlayerSnap best = null; float bestScore = 0f;
                foreach (var p in vis)
                {
                    if (p.IsImpostor || !p.Alive) continue;
                    float dx = p.X - myX, dy = p.Y - myY;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    if (dist > 3.5f) continue;

                    // 打分：越近越好；但周围人越多越危险
                    int crowd = World.CountNear(p.X, p.Y, 4f);
                    float score = 2f / (0.5f + dist) - crowd * CrowdFear;
                    if (score > bestScore) { bestScore = score; best = p; }
                }
                if (best != null && bestScore > 0.35f)
                {
                    float dx = best.X - myX, dy = best.Y - myY;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    if (dist <= KillReach)
                    {
                        _state = $"★下刀 {best.Name}";
                        return null;                        // 由外部执行刀人
                    }
                    _state = $"接近 {best.Name}";
                    return (best.X, best.Y);
                }
            }

            // ── 常规：走到目标点，到了就「停留」当作在做任务 ──
            float tdx = _targetX - myX, tdy = _targetY - myY;
            float td = MathF.Sqrt(tdx * tdx + tdy * tdy);

            if (td < 0.6f || !walkableOk)
            {
                // 到达 → 停留一会儿（假装做任务 / 观察）
                float pause = PauseMin + (float)_rng.NextDouble() * (PauseMax - PauseMin);
                _pauseUntil = now + (long)(pause * 1000);
                _state = World.AmImpostor ? "停留(装做任务)" : "停留(做任务)";
                PickNewTarget(myX, myY);
                _targetSet = true;
                return null;
            }

            _state = World.AmImpostor ? "假装前往任务点" : "前往任务点";
            return (_targetX, _targetY);
        }

        /// <summary>刀人的目标（如果此刻该动手）</summary>
        public int KillTargetNetId(float myX, float myY)
        {
            if (!World.AmImpostor) return -1;
            var vis = World.Visible();
            int best = -1; float bestScore = 0f;
            foreach (var p in vis)
            {
                if (p.IsImpostor || !p.Alive) continue;
                float dx = p.X - myX, dy = p.Y - myY;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist > KillReach) continue;
                int crowd = World.CountNear(p.X, p.Y, 4f);
                float score = 2f / (0.5f + dist) - crowd * CrowdFear;
                if (score > bestScore && score > 0.35f) { bestScore = score; best = p.NetId; }
            }
            return best;
        }


        // ═══════════════════════════════════════════════════════
        //  ★★★ 刀后掩饰：假装「刚发现尸体」而不是「秒刀自爆」★★★
        //
        //  用户的原话：
        //      「内鬼也可以刀完人后在原地等几秒再报告，
        //        这样可以假装自己是刚发现尸体的船员，
        //        而不是秒刀后自爆」
        //
        //  真人内鬼就是这么玩的 —— 秒刀秒报，一眼假。
        //  所以：刀完先在附近晃 3~8 秒（像在别处做事），
        //        再走回尸体旁拍桌，最后发一句「我在X发现了尸体」。
        // ═══════════════════════════════════════════════════════
        public float CoverDelayMin, CoverDelayMax;   // 掩饰等待时长
        private long _coverUntil;
        private float _bodyX, _bodyY;
        private bool _covering;
        private bool _pendingReport;

        public bool WantsToReport => _pendingReport;
        public (float x, float y) BodyPos => (_bodyX, _bodyY);

        /// <summary>刚刀了人 —— 开始掩饰</summary>
        public void OnKilled(float bodyX, float bodyY)
        {
            _bodyX = bodyX; _bodyY = bodyY;
            _covering = true;
            float d = CoverDelayMin + (float)_rng.NextDouble() * (CoverDelayMax - CoverDelayMin);
            _coverUntil = Environment.TickCount64 + (long)(d * 1000);
            _state = $"掩饰中({d:F1}s 后装作发现尸体)";
        }

        /// <summary>掩饰阶段的行为：先走开，时间到了再回尸体旁报告</summary>
        public bool StepCover(float myX, float myY, out float tx, out float ty)
        {
            tx = ty = 0f;
            if (!_covering) return false;
            long now = Environment.TickCount64;

            if (now < _coverUntil)
            {
                // 还在掩饰期 —— 朝一个「离尸体有点距离」的点走，显得像在忙别的
                float dx = myX - _bodyX, dy = myY - _bodyY;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                if (d < 2.0f)
                {
                    // 离尸体太近 → 往反方向走开一点
                    if (d < 0.01f) { dx = 1f; dy = 0f; d = 1f; }
                    tx = myX + dx / d * 2.5f;
                    ty = myY + dy / d * 2.5f;
                    _state = "掩饰中(走开一点)";
                    return true;
                }
                _state = "掩饰中(假装在忙)";
                return false;                 // 已经够远了，原地待着
            }

            // 掩饰够了 → 走向尸体去「发现」它
            float bd = MathF.Sqrt((_bodyX - myX) * (_bodyX - myX) + (_bodyY - myY) * (_bodyY - myY));
            if (bd > 0.9f)
            {
                tx = _bodyX; ty = _bodyY;
                _state = "★走向尸体(准备装作发现)";
                return true;
            }

            _covering = false;
            _pendingReport = true;
            _state = "★★★ 报告尸体（装作刚发现）";
            return false;
        }

        public void ReportDone() { _pendingReport = false; }

        // ═══════════════════════════════════════════════════════
        //  ★ 破坏：内鬼的另一个工具
        //     RepairSystem(28) 打给 ShipStatus，载荷:
        //        [byte SystemID][packed PlayerControl netId][byte Amount]
        //     The Skeld 常用: 7=关灯, 8=氧气, 3=反应堆
        // ═══════════════════════════════════════════════════════
        public float SabotageCooldown;      // 两次破坏之间至少隔多久
        private long _nextSabotage;

        public bool ShouldSabotage()
        {
            if (!World.AmImpostor) return false;
            if (World.ShipStatusNetId < 0) return false;
            if (Environment.TickCount64 < _nextSabotage) return false;
            _nextSabotage = Environment.TickCount64 + (long)(SabotageCooldown * 1000);
            return true;
        }

        /// <summary>随机挑一个破坏目标（关灯最常见，因为能制造混乱）</summary>
        public byte PickSabotage()
        {
            double r = _rng.NextDouble();
            if (r < 0.55) return 7;    // ELECTRICAL 关灯
            if (r < 0.85) return 8;    // O2
            return 3;                  // REACTOR
        }

        /// <summary>
        /// 选一个新的游荡目标。
        ///
        /// ★ 踩过的坑：原来是「以自己为中心、随机方向和 2~12 的距离」撒点。
        ///   大厅只有 5.5 × 3.8 大，绝大多数点都落在墙外 → 20 次全失败 →
        ///   兜底把目标设成**当前位置** → td &lt; 0.6 → 判定「已到达」→
        ///   暂停 + 重选 → 又选不到 → 无限暂停，机器人永远不动
        ///   （实测：连续 42 次「原地」包，跨距 0.0）。
        ///
        /// 改成：在**整张地图的可行走范围内**撒点（不依赖自己当前位置），
        ///       并要求离自己至少 1.5 —— 这样一定选得出「值得走一趟」的点。
        /// </summary>
        private void PickNewTarget(float myX, float myY)
        {
            float minX, maxX, minY, maxY;
            Program.WalkableBounds(out minX, out minY, out maxX, out maxY);

            float bestX = myX, bestY = myY; bool found = false;
            for (int i = 0; i < 120; i++)
            {
                float nx = minX + (float)_rng.NextDouble() * (maxX - minX);
                float ny = minY + (float)_rng.NextDouble() * (maxY - minY);
                if (!Program.WalkablePublic(nx, ny)) continue;
                float dx = nx - myX, dy = ny - myY;
                // 太近的没意义（会立刻判定到达）；太远的先收下当备选
                if (dx * dx + dy * dy < 1.5f * 1.5f)
                {
                    if (!found) { bestX = nx; bestY = ny; found = true; }
                    continue;
                }
                _targetX = nx; _targetY = ny;
                return;
            }
            if (found) { _targetX = bestX; _targetY = bestY; return; }
            // 真的一个点都找不到（几何还没载入）→ 保持原目标，别乱动
        }
    }
}
