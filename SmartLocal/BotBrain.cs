using System;
using UnityEngine;

namespace SmartLocal
{
    /// <summary>
    /// 单个假人的大脑 —— 纯数据类，由 BotManager 统一驱动。
    ///
    /// 刻意**不做成 MonoBehaviour**：
    ///   1) 每个假人挂一个组件需要逐个 ClassInjector 注册，开销大且易出错
    ///   2) AI 需要跨帧保存大量状态（目标点、路径、记忆），集中管理更好扩展
    ///
    /// 阶段 7a 的目标只有一个：**证明假人能独立移动**，不再共享玩家输入。
    ///
    /// 关键约束（实测得出）：假人必须用本机 ClientId 做 ownerId，否则服务端
    /// 会等不到 owner 数据而超时断连。因此不能靠改归属来摆脱输入劫持 ——
    /// 改为由 BotInputOverridePatch 在物理步前覆盖速度。
    /// </summary>
    internal class BotBrain
    {
        public readonly PlayerControl Player;
        public readonly int Index;

        /// <summary>当前行进方向（单位向量）</summary>
        private Vector2 _dir;

        private static readonly System.Random Rng = new System.Random(20261003);

        public BotBrain(PlayerControl player, int index, int total)
        {
            Player = player;
            Index = index;

            try { PhysicsId = player.MyPhysics != null ? player.MyPhysics.GetInstanceID() : 0; }
            catch { PhysicsId = 0; }

            // ★ 关键设计：初始方向按圆周均匀分布。
            // 如果速度覆盖生效，14 个假人会像烟花一样朝 14 个方向散开 ——
            // 这个视觉效果是「独立」与否的零歧义判据。
            float angle = total > 0 ? (index / (float)total) * Mathf.PI * 2f : 0f;
            _dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            _total = total;
        }

        /// <summary>撞墙时立刻换一个方向（避免顶着墙不动）</summary>
        private void OnBlocked()
        {
            // 直接反向偏转，比纯随机的反应更快
            _dir = new Vector2(-_dir.y, _dir.x);   // 旋转 90°
            if ((float)Rng.NextDouble() < 0.5f) _dir = -_dir;
            _phaseTimer = 1.0f;
        }

        /// <summary>
        /// 射线避墙。
        ///
        /// 位置接管绕过了游戏物理，所以碰撞必须自己做 ——
        /// 这正是选择方案A 时明确接受的代价：「穿墙我能修，身份争抢我修不动」。
        ///
        /// 忽略玩家自身的碰撞体（否则假人会互相挡住），也忽略触发器。
        /// </summary>
        private static bool Blocked(Vector2 from, Vector2 dir, float dist)
        {
            if (dir == Vector2.zero) return false;

            try
            {
                var hits = Physics2D.RaycastAll(from, dir.normalized, dist);
                if (hits == null) return false;

                for (int i = 0; i < hits.Length; i++)
                {
                    var col = hits[i].collider;
                    if (col == null || col.isTrigger) continue;

                    // 忽略玩家（含其他假人）的碰撞体
                    try { if (col.GetComponent<PlayerControl>() != null) continue; }
                    catch { }

                    return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>所属 PlayerPhysics 的实例 ID，供物理步快速反查。</summary>
        public readonly int PhysicsId;

        /// <summary>本批假人总数（用于开局按圆周摆位）</summary>
        private readonly int _total;

        /// <summary>当前是否处于「行走」阶段（走/停交替，避免动画永远卡在行走态）</summary>
        private bool _moving = true;

        /// <summary>当前阶段剩余时间</summary>
        private float _phaseTimer = 1.5f;

        /// <summary>
        /// 当前应该施加的移动方向。
        ///
        /// ★ 静止时返回**零向量**而不是保持最后方向 ——
        /// 这是为了修掉「动画卡死」：之前的实现每个物理步都写入非零速度，
        /// 游戏因此认为假人一直在走，行走动画永远不切回待机，
        /// 撞墙时看起来就像动画定住。
        /// </summary>
        public Vector2 Direction => _moving ? _dir : Vector2.zero;

        /// <summary>
        /// 只推进「思考和计时」，**不直接移动**。
        /// 移动由两个速度补丁在物理步前后完成。
        /// </summary>
        public void Tick(float dt)
        {
            if (Player == null) return;

            _phaseTimer -= dt;
            if (_phaseTimer > 0f) return;

            // 走 / 停 交替
            _moving = !_moving;
            if (_moving)
            {
                PickRandomDirection();
                _phaseTimer = 1.5f + (float)Rng.NextDouble() * 2.5f;
            }
            else
            {
                _phaseTimer = 0.8f + (float)Rng.NextDouble() * 1.5f;
            }
        }

        private void PickRandomDirection()
        {
            // 保持大致朝向，加一个随机偏转，避免纯随机导致的抖动
            float turn = (float)(Rng.NextDouble() * 2.0 - 1.0) * (Mathf.PI / 2f);

            float cos = Mathf.Cos(turn);
            float sin = Mathf.Sin(turn);
            _dir = new Vector2(_dir.x * cos - _dir.y * sin,
                               _dir.x * sin + _dir.y * cos).normalized;
        }

        /// <summary>当前位置（用于诊断）</summary>
        public Vector3 Position
        {
            get
            {
                try { return Player != null ? Player.transform.position : Vector3.zero; }
                catch { return Vector3.zero; }
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  位置层接管
        // ═══════════════════════════════════════════════════════════

        /// <summary>我们自己维护的权威位置。游戏怎么写都会被我们覆盖回去。</summary>
        private Vector3 _own;

        /// <summary>上一帧比赛是否已开始（用于在地图加载瞬间重新对齐）</summary>
        private bool _wasRunning;

        /// <summary>是否已经开始「冻结保持」（进入冻结前需要先对齐一次当前位置）</summary>
        private bool _holding;

        /// <summary>出生后经过的秒数。入座需要时间，太早冻结会顶掉入座流程。</summary>
        private float _age;

        /// <summary>入座宽限期：这段时间内完全不碰位置，让游戏把假人安排到座位上</summary>
        private const float SeatGraceSeconds = 2.0f;

        /// <summary>假人移动速度（玩家实测速度约 2.5）</summary>
        private const float Speed = 2.5f;

        /// <summary>拴绳半径：离出生锚点超过这个距离就往回走（防止跑出地图）</summary>
        private const float LeashRadius = 16f;

        /// <summary>出生锚点（本机玩家开局位置），拴绳的中心</summary>
        private Vector3 _anchor;

        private bool _loggedSelf;

        /// <summary>
        /// 由 BotPositionOverridePatch 在 PlayerPhysics.LateUpdate 之后调用。
        ///
        /// 这是渲染前的最后一层，所以这里写入的位置当帧不会被覆盖。
        ///
        /// 三个状态：
        ///   出生后 2 秒内   → **完全不碰位置**，让游戏完成入座
        ///   大厅/过场       → 位置冻结在原地（抵消游戏往玩家身上同步）
        ///   比赛进行中       → 用我们自己的方向和速度积分移动
        /// </summary>
        public void ApplyPosition()
        {
            if (Player == null) return;

            // ★ 绝不对玩家本体做任何位置写入。
            // 写到本体上会导致：被固定、交互失效、地图键失灵（实测症状）。
            if (BotManager.IsLocalPlayerBody(Player))
            {
                if (!_loggedSelf)
                {
                    _loggedSelf = true;
                    Plugin.Logger.LogError($"[位置接管] 假人#{Index} 的引用指向了**本机玩家本体**，已跳过全部位置写入！");
                }
                return;
            }

            try
            {
                Vector3 pos = Player.transform.position;
                bool running = BotManager.CanBotsMove();

                // 地图刚加载完：把假人摆到本机玩家周围的环上。
                //
                // 为什么不沿用当前位置：大厅和地图是**两个不同的场景**，坐标空间不通用。
                // 游戏只会传送真实玩家，不会管我们的假人 ——
                // 所以不主动摆放的话，它们会顶着大厅坐标进地图（实测：保留大厅队形）。
                if (running && !_wasRunning)
                {
                    var lp = PlayerControl.LocalPlayer;
                    _anchor = lp != null ? lp.transform.position : pos;

                    float ang = _total > 0 ? (Index / (float)_total) * Mathf.PI * 2f : 0f;
                    _own = _anchor + new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f) * 0.9f;

                    Plugin.Logger.LogInfo($"[位置接管] 假人#{Index} 开局摆位 ({_own.x:F2},{_own.y:F2})");
                }

                if (running)
                {
                    Vector2 d = Direction;

                    // ★ 拴绳：离出生锚点太远就往回走。
                    // 实测反馈：有假人跑出地图、或卡在箱子下面 ——
                    // 原因是射线在场景刚切换的那几秒可能检测不到碰撞体。
                    // 拴绳作为兜底，保证它们始终待在玩家附近的可玩区域内。
                    float fromAnchor = Vector2.Distance(new Vector2(_own.x, _own.y),
                                                        new Vector2(_anchor.x, _anchor.y));
                    if (fromAnchor > LeashRadius)
                    {
                        Vector2 back = (new Vector2(_anchor.x, _anchor.y) - new Vector2(_own.x, _own.y)).normalized;
                        d = back;
                    }

                    if (d != Vector2.zero)
                    {
                        Vector2 cur = new Vector2(_own.x, _own.y);
                        Vector2 step = d * (Speed * Time.deltaTime);

                        // 撞墙就换向，而不是硬顶上去
                        if (Blocked(cur, d, step.magnitude + 0.32f))
                        {
                            OnBlocked();
                            d = Direction;
                            step = d * (Speed * Time.deltaTime);
                        }

                        if (d != Vector2.zero && !Blocked(cur, d, step.magnitude + 0.32f))
                            _own += new Vector3(step.x, step.y, 0f);
                    }

                    Player.transform.position = _own;
                    _holding = false;
                }
                else if (_age > SeatGraceSeconds)
                {
                    // 进入冻结前先对齐一次当前位置，否则会把入座后的假人
                    // 拽回出生点（上一版就是这个 bug，导致全部叠在一个坐标）
                    if (!_holding)
                    {
                        _own = pos;
                        _holding = true;
                        Plugin.Logger.LogInfo($"[位置接管] 假人#{Index} 开始冻结于 ({_own.x:F2},{_own.y:F2})");
                    }
                    Player.transform.position = _own;
                }
                // else: 入座宽限期内什么都不做

                _wasRunning = running;
            }
            catch (Exception e)
            {
                if (!_loggedPos)
                {
                    _loggedPos = true;
                    Plugin.Logger.LogError($"[位置接管] 假人#{Index} 失败: {e}");
                }
            }
        }

        private bool _loggedPos;

        /// <summary>由 BotManager 推进年龄</summary>
        public void Age(float dt) => _age += dt;

        /// <summary>PlayerId，用于诊断 pid 碰撞</summary>
        public int PlayerId
        {
            get
            {
                try { return Player != null ? Player.PlayerId : -1; }
                catch { return -1; }
            }
        }
    }
}
