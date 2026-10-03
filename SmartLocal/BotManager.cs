using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Injection;
using InnerNet;
using UnityEngine;

namespace SmartLocal
{
    /// <summary>
    /// 假人 AI 总驱动。整个 Mod 只需要注册这一个注入型 MonoBehaviour。
    ///
    /// 同时承担「独立移动」实验的观测职责：周期性统计 14 个假人相对玩家的
    /// 位置离散度，把「到底独不独立」变成能直接从日志读出的数字，
    /// 而不是靠肉眼看截图。
    /// </summary>
    public class BotManager : MonoBehaviour
    {
        public BotManager(IntPtr ptr) : base(ptr) { }

        private static BotManager _instance;
        private static bool _typeRegistered;

        private readonly List<BotBrain> _brains = new List<BotBrain>();

        /// <summary>PlayerPhysics 实例 ID → 对应的大脑。供物理步 O(1) 反查。</summary>
        private readonly Dictionary<int, BotBrain> _byPhysicsId = new Dictionary<int, BotBrain>();

        private float _sampleTimer;
        private int _sampleCount;

        /// <summary>是否已完成「开局后除名」（只做一次）</summary>
        private bool _reowned;
        private const float SampleInterval = 2f;

        /// <summary>确保 BotManager 类型已注册进 il2cpp（幂等）</summary>
        internal static void EnsureRegistered()
        {
            if (_typeRegistered) return;
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<BotManager>();
                _typeRegistered = true;
                Plugin.Logger.LogInfo("[AI] BotManager 类型已注册。");
            }
            catch (Exception e)
            {
                _typeRegistered = true;   // 已注册过也会抛，视为成功
                Plugin.Logger.LogWarning($"[AI] BotManager 注册提示: {e.Message}");
            }
        }

        /// <summary>接管一批假人。</summary>
        internal static void Attach(List<PlayerControl> bots)
        {
            if (bots == null || bots.Count == 0)
            {
                Plugin.Logger.LogWarning("[AI] 没有假人可接管。");
                return;
            }

            EnsureRegistered();

            if (_instance == null)
            {
                if (Plugin.Instance == null)
                {
                    Plugin.Logger.LogError("[AI] Plugin.Instance 为空，无法挂载 BotManager。");
                    return;
                }
                _instance = Plugin.Instance.AddComponent<BotManager>();
                if (_instance == null)
                {
                    Plugin.Logger.LogError("[AI] AddComponent<BotManager> 返回 null。");
                    return;
                }
            }

            _instance._brains.Clear();
            _instance._byPhysicsId.Clear();
            for (int i = 0; i < bots.Count; i++)
            {
                if (bots[i] == null) continue;
                var brain = new BotBrain(bots[i], i, bots.Count);
                _instance._brains.Add(brain);
                if (brain.PhysicsId != 0)
                    _instance._byPhysicsId[brain.PhysicsId] = brain;
            }

            Plugin.Logger.LogInfo(
                $"[AI] 已接管 {_instance._brains.Count} 个假人（可用物理步反查 {_instance._byPhysicsId.Count} 个），" +
                "方向按圆周均匀分布。");

            if (Plugin.EnableBotMovement)
                Plugin.Logger.LogInfo("[AI] 独立移动已启用 —— 由 PlayerPhysics.FixedUpdate 的 Prefix 覆盖速度。");
            else
                Plugin.Logger.LogWarning("[AI] 独立移动被关闭 —— 假人不会移动。");
        }

        /// <summary>
        /// 比赛是否真的在进行中。
        ///
        /// 门控依据（实测反馈：假人会在准备阶段/加载/分身份时乱动）：
        ///   ShipStatus.Instance != null   → 地图已加载（区别于大厅）
        ///   IntroCutscene.Instance == null → 过场动画已结束
        /// 两个条件同时满足才算「可以动」。
        /// </summary>
        internal static bool IsMatchRunning()
        {
            try
            {
                if (IntroCutscene.Instance != null) return false;

                // ★ 实测修正：仅靠 ShipStatus.Instance 判定会**永远为 false**
                // （地图里它也是 null），导致 CanBotsMove() 恒假、
                // 假人从未被驱动过 —— 表现为「打开设置也不乱走」（不是修好了，是整个停了）。
                // 加上 HudManager 作为兜底：大厅没有 HUD，地图里有。
                bool ship = ShipStatus.Instance != null;
                bool hud = HudManager.Instance != null;
                return ship || hud;
            }
            catch { return false; }
        }

        /// <summary>门控三要素的诊断串（排查「假人不动」时的第一手数据）</summary>
        internal static string GateDebug()
        {
            try
            {
                string scene = "?";
                try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
                return $"scene={scene} ShipStatus={(ShipStatus.Instance != null)} " +
                       $"Hud={(HudManager.Instance != null)} Intro={(IntroCutscene.Instance != null)} " +
                       $"→ 可动={CanBotsMove()}";
            }
            catch (Exception e) { return $"门控读取失败: {e.Message}"; }
        }

        /// <summary>比赛开始时刻（用于加载阶段的延迟闸）</summary>
        private static float _matchStartTime = -1f;

        /// <summary>
        /// 开局后的静默期（秒）。
        ///
        /// 实测反馈：加载界面和分身份期间假人就会乱走 ——
        /// 说明那时 IsMatchRunning() 已经为真了，只靠 ShipStatus/IntroCutscene 门控不够。
        /// 加一道时间闸，等场景彻底稳定再放行。
        /// </summary>
        private const float MatchMoveDelay = 3.5f;

        /// <summary>
        /// 假人现在是否可以移动。
        /// 与 IsMatchRunning() 的区别是多了开局静默期，
        /// 避免在加载界面 / 分身份阶段乱走（用户实测反馈）。
        /// </summary>
        internal static bool CanBotsMove()
        {
            if (!IsMatchRunning())
            {
                _matchStartTime = -1f;
                return false;
            }

            if (_matchStartTime < 0f) _matchStartTime = Time.time;
            return Time.time - _matchStartTime >= MatchMoveDelay;
        }

        /// <summary>重置 AI 的全部静态/实例状态（离开房间、返回主菜单时调用）</summary>
        internal static void ResetAll()
        {
            _matchStartTime = -1f;
            if (_instance == null) return;
            _instance._brains.Clear();
            _instance._byPhysicsId.Clear();
            _instance._reowned = false;
            _instance._localPlayerFixCount = 0;
            _instance._camFixCount = 0;
            _instance._sampleCount = 0;
            _instance._sampleTimer = 0f;
            Plugin.Logger.LogInfo("[清理] AI 状态已完全重置。");
        }

        /// <summary>
        /// 供速度补丁反查该 PlayerPhysics 对应的 AI 方向。
        /// 命中返回 true，并把方向写入 dir。
        /// </summary>
        internal static bool TryGetDirection(PlayerPhysics physics, out Vector2 dir)
        {
            dir = default;
            if (_instance == null || _instance._byPhysicsId.Count == 0) return false;

            int id;
            try { id = physics.GetInstanceID(); }
            catch { return false; }

            if (!_instance._byPhysicsId.TryGetValue(id, out var brain)) return false;

            dir = brain.Direction;
            return true;
        }

        /// <summary>
        /// 独立判定真正的本机角色。
        ///
        /// 判据：`OwnerId == AmongUsClient.Instance.ClientId` 且**不是我们造的假人**。
        /// 假人虽然也被我们用同一个 ClientId 生成，但 IsBot() 能把它们排除掉。
        /// </summary>
        private static PlayerControl FindTrueLocalPlayer(AmongUsClient auc)
        {
            try
            {
                var all = PlayerControl.AllPlayerControls;
                if (all == null) return null;

                int myClientId = auc.ClientId;
                for (int i = 0; i < all.Count; i++)
                {
                    var p = all[i];
                    if (p == null || IsBot(p)) continue;
                    if (p.OwnerId == myClientId) return p;
                }
            }
            catch { }
            return null;
        }

        /// <summary>该 PlayerControl 是否是我们创建的假人</summary>
        internal static bool IsBot(PlayerControl pc)
        {
            if (_instance == null || pc == null) return false;
            int id;
            try { id = pc.GetInstanceID(); } catch { return false; }

            for (int i = 0; i < _instance._brains.Count; i++)
            {
                var p = _instance._brains[i].Player;
                if (p == null) continue;
                try { if (p.GetInstanceID() == id) return true; } catch { }
            }
            return false;
        }

        /// <summary>
        /// ★ 绝对安全阀：这个 PlayerControl 是不是本机角色本体。
        ///
        /// 位置接管每帧强制写位置 —— 如果写到了玩家本体上，后果是：
        ///   玩家被「固定」（每帧被写回同一坐标）
        ///   碰撞与交互检测失效（位置被覆写，邻近判定拿不到真实位置）
        ///   地图键失灵
        /// 实测反馈的「无法跟任何东西交互」正指向这一类问题。
        ///
        /// 所以只要发现某条 Brain 指向的是本机角色，就**直接跳过它**，
        /// 绝不对玩家本体做任何位置写入。
        /// </summary>
        internal static bool IsLocalPlayerBody(PlayerControl pc)
        {
            if (pc == null) return false;
            try
            {
                var lp = PlayerControl.LocalPlayer;
                if (lp == null) return false;
                return lp.GetInstanceID() == pc.GetInstanceID();
            }
            catch { return false; }
        }

        /// <summary>反查该 PlayerPhysics 对应的大脑（位置接管补丁用）</summary>
        internal static bool TryGetBrain(PlayerPhysics physics, out BotBrain brain)
        {
            brain = null;
            if (_instance == null || _instance._byPhysicsId.Count == 0) return false;

            int id;
            try { id = physics.GetInstanceID(); }
            catch { return false; }

            return _instance._byPhysicsId.TryGetValue(id, out brain);
        }

        /// <summary>
        /// ★ 修复假人与本机玩家的 PlayerId 冲突。
        ///
        /// 实测现象：本机 pid=14，而假人 #13 的 pid 也是 14 ——
        /// Among Us 的相机与 HUD 按 LocalPlayer 定位，pid 撞了会解析到错误对象，
        /// 表现就是「视角被锁在某个假人身上，自己动不了」。
        ///
        /// 成因：15 人局正好 15 个 pid 名额，我们的 14 个假人把 1~14 全占了，
        /// 游戏随后给本机玩家也分了 14。
        ///
        /// 但冲突本身就意味着有 pid 空着（15 个玩家却只有 14 个不同 pid ⇒ 0 是空的），
        /// 把撞车的**假人**挪到那个空位即可 —— 玩家是本体，永远优先。
        /// </summary>
        private void FixPidCollisions()
        {
            try
            {
                var lp = PlayerControl.LocalPlayer;
                if (lp == null) { Plugin.Logger.LogWarning("[pid 修复] LocalPlayer 不可用。"); return; }

                // ★ 用 PlayerControl.AllPlayerControls 作为**唯一权威来源**。
                //
                // 上一版失败的原因：冲突检测读 pc.PlayerId（PlayerControl），
                // 空闲判断却读 info.PlayerId（NetworkedPlayerInfo）——
                // 这两个字段实测会不同步，拿它们互相比等于拿苹果比橘子，
                // 结果就是「明明有冲突却说无空闲 pid」。
                var all = PlayerControl.AllPlayerControls;
                if (all == null) { Plugin.Logger.LogWarning("[pid 修复] AllPlayerControls 不可用。"); return; }

                int myPid = lp.PlayerId;

                var used = new HashSet<int>();
                for (int i = 0; i < all.Count; i++)
                {
                    var p = all[i];
                    if (p != null) used.Add(p.PlayerId);
                }

                Plugin.Logger.LogWarning(
                    $"[pid 修复] 权威 pid 集合({used.Count} 个): {string.Join(",", used)}  本机 pid={myPid}");

                int fixedCount = 0;
                for (int i = 0; i < _brains.Count; i++)
                {
                    var brain = _brains[i];
                    var pc = brain.Player;
                    if (pc == null || pc.PlayerId != myPid) continue;

                    // 找空闲 pid
                    int free = -1;
                    for (int p = 0; p < 15; p++)
                    {
                        if (!used.Contains(p)) { free = p; break; }
                    }

                    if (free < 0)
                    {
                        Plugin.Logger.LogError($"[pid 修复] 假人#{i} pid={myPid} 冲突，且 0~14 已全部占用。");
                        continue;
                    }

                    used.Add(free);
                    used.Remove(myPid);   // 该 pid 现在归本机玩家独占

                    int oldPid = pc.PlayerId;
                    pc.PlayerId = (byte)free;

                    // 同步 PlayerId 到 NetworkedPlayerInfo，避免两个字段再次分叉
                    var info = pc.Data;
                    if (info != null) info.PlayerId = (byte)free;

                    fixedCount++;
                    Plugin.Logger.LogWarning(
                        $"[pid 修复] 假人#{i} pid {oldPid} -> {free}（与本机撞车，已让位；相机与控制应立即回到本机）");
                }

                Plugin.Logger.LogInfo(
                    fixedCount > 0
                        ? $"[pid 修复] 共修正 {fixedCount} 个冲突，本机 pid={myPid} 现已独占。"
                        : $"[pid 修复] 无冲突（本机 pid={myPid}）。");

                // 修复后立刻把相机拉回本机（冲突期间它可能已经被指到假人身上）
                if (fixedCount > 0) EnsureCameraTarget();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[pid 修复] 失败: {e}");
            }
        }

        /// <summary>
        /// ★★★★ 身份守卫：把 PlayerControl.LocalPlayer 抢回真正的本机角色 ★★★★
        ///
        /// 决定性证据（实测日志）：
        ///   [pid 修复] 权威 pid 集合(15 个): 0,1,...,14   本机 pid=14
        ///   [Error]   假人#13 pid=14 冲突，且 0~14 已全部占用
        ///
        /// AllPlayerControls 里 15 个玩家的 pid 是 0~14 **互不重复**的，
        /// 那就不该存在冲突。唯一自洽的解释是：
        ///   **PlayerControl.LocalPlayer 指向的是假人 #13，而不是玩家本体。**
        /// 集合里那个 14 是 #13 的，玩家本体是 pid 0（所以集合才凑得齐 0~14）。
        ///
        /// 这解释了全部症状：相机跟 LocalPlayer 走 → 锁在 #13；
        /// 控制给 LocalPlayer → 给到 #13；玩家本体反而不受控。
        ///
        /// 权威身份来源是 **ClientData.Character**（本机客户端的角色），
        /// 这也是 AmongUs.MultiClientInstancing 里切换控制时用的依据。
        /// </summary>
        private void EnsureLocalPlayer()
        {
            try
            {
                var auc = AmongUsClient.Instance;
                if (auc == null) return;

                var myClient = auc.GetClient(auc.ClientId);
                if (myClient == null) return;

                // ★ 不再用 ClientData.Character 当权威 —— 它恰恰是被污染的那个。
                //
                // 原因：我们用 Spawn(pc, auc.ClientId, ...) 生成假人，
                // 把假人的 ownerId 设成了本机客户端，于是本机客户端的 Character
                // 被最后一个假人（#14）占用了。
                //
                // 实测证据（用户）：**只有 14 号靠近物体时我的使用键才会亮** ——
                // 说明 HUD/交互系统绑的是 14 号，走的正是
                // ClientId → GetClient(ClientId).Character 这条解析路径。
                // （相机走的是 PlayerControl.LocalPlayer，是另一条路径，所以相机早就正常了。）
                //
                // 真正的本机角色有一个可判别的特征：OwnerId == ClientId 且**不是我们造的假人**。
                PlayerControl real = FindTrueLocalPlayer(auc);
                if (real == null) return;

                // 把本机客户端的角色绑定抢回来 —— HUD 与交互依赖它
                var bound = myClient.Character;
                if (bound == null || bound.GetInstanceID() != real.GetInstanceID())
                {
                    myClient.Character = real;
                    Plugin.Logger.LogWarning(
                        $"[身份守卫] 本机客户端的 Character 原为 '{(bound != null ? bound.name : "null")}'，已抢回玩家本体");
                }

                // ★★ 无条件的「本体可用性」维护 —— 这段必须在早退之前。
                //
                // 之前的逻辑漏洞：只有在**身份发生变化**时才调用 ReestablishLocalPlayer，
                // 而它才是启用输入接收器的地方。结果身份一旦修对就早退，
                // 之后再也不会检查「接收器是不是被关掉了」。
                //
                // 实测症状正是这个：视角在本体上（身份对），但脚动的是假人、
                // 本体一动不动 —— 说明本体的 inputHandler 是关着的，而我们不再管它。
                try
                {
                    var phys = real.MyPhysics;
                    if (phys != null && phys.inputHandler != null && !phys.inputHandler.enabled)
                    {
                        phys.inputHandler.enabled = true;
                        Plugin.Logger.LogWarning("[身份守卫] 本机输入接收器被关闭过，已重新开启（本体应恢复可动）");
                    }
                    if (!real.moveable)
                    {
                        real.moveable = true;
                        Plugin.Logger.LogWarning("[身份守卫] 本机 moveable 被置为 false，已恢复");
                    }
                }
                catch (Exception e)
                {
                    if (!_loggedLocal) { _loggedLocal = true; Plugin.Logger.LogError($"[身份守卫] 本体可用性维护失败: {e}"); }
                }

                var cur = PlayerControl.LocalPlayer;
                if (cur != null && cur.GetInstanceID() == real.GetInstanceID()) return;

                // 记录现场，便于确认到底被指到了谁身上
                string wasName = cur != null ? cur.name : "null";
                int wasPid = cur != null ? cur.PlayerId : -1;

                PlayerControl.LocalPlayer = real;

                _localPlayerFixCount++;
                Plugin.Logger.LogWarning(
                    $"[身份守卫] LocalPlayer 原指向 '{wasName}'(pid={wasPid})，已还原为本机角色 " +
                    $"(pid={real.PlayerId})（第 {_localPlayerFixCount} 次）");

                // ★ 抢回身份只做对了一半 —— 必须把「挂在身份上的附属物」一并接管。
                // 完整序列来自 AmongUs.MultiClientInstancing 的 InstanceControl.SwitchTo，
                // 那是经过实战验证的做法。
                ReestablishLocalPlayer(real);
            }
            catch (Exception e)
            {
                if (!_loggedLocal)
                {
                    _loggedLocal = true;
                    Plugin.Logger.LogError($"[身份守卫] 失败: {e}");
                }
            }
        }

        /// <summary>
        /// 把「本机玩家」这个身份所需的附属组件全部重建/接管。
        ///
        /// 依据 MCI 的 SwitchTo（逐条对应）：
        ///   lightSource  —— 灯光/视野锥是绑定在 LocalPlayer 身上的，
        ///                   不重建的话灯光会留在旧角色那里（实测：灯锁在 14 号）
        ///   moveable     —— 移动许可
        ///   inputHandler —— ★ 输入接收器，必须在 LocalPlayer 上开启，
        ///                   否则「控制不了自己」
        ///   相机 / HUD   —— 跟着身份重设
        /// </summary>
        private void ReestablishLocalPlayer(PlayerControl lp)
        {
            try
            {
                // ★ 安全阀：绝不能对假人做这些。
                // 上一版的回退就是这么来的 —— 开局时 LocalPlayer 可能还指向假人，
                // 于是给假人开了 inputHandler，玩家按键动的是假人。
                if (IsBot(lp))
                {
                    Plugin.Logger.LogError("[身份守卫] 目标是假人，拒绝重建（这会抢走玩家的输入）");
                    return;
                }

                // ③ 移动许可
                lp.moveable = true;

                // ④ 输入接收器：只在本机角色上开启
                try
                {
                    var phys = lp.MyPhysics;
                    if (phys != null && phys.inputHandler != null)
                    {
                        phys.inputHandler.enabled = true;
                        Plugin.Logger.LogInfo("[身份守卫] 本机 inputHandler 已启用");
                    }
                }
                catch (Exception e) { Plugin.Logger.LogWarning($"[身份守卫] inputHandler 启用失败: {e.Message}"); }

                // ① ② 灯光重建
                try
                {
                    if (lp.lightSource != null) UnityEngine.Object.Destroy(lp.lightSource);

                    var lightPrefab = lp.LightPrefab;
                    if (lightPrefab != null)
                    {
                        var light = UnityEngine.Object.Instantiate(lightPrefab);
                        light.transform.SetParent(lp.transform);
                        light.transform.localPosition = lp.Collider.offset;
                        light.Initialize(lp.Collider.offset * 0.5f);
                        lp.lightSource = light;
                        Plugin.Logger.LogInfo("[身份守卫] 灯光已在本机角色上重建");
                    }
                    else
                    {
                        Plugin.Logger.LogWarning("[身份守卫] LightPrefab 为空，灯光未重建");
                    }
                }
                catch (Exception e) { Plugin.Logger.LogWarning($"[身份守卫] 灯光重建失败: {e.Message}"); }

                // 注意：这里**不再**调用 SetHudActive(true) 和 ResetMoveState()。
                // 实测反馈：加上之后地图打不开、击杀键与跳管键来回切换。
                // 强行激活整个 HUD 会与游戏自己的 HUD 状态机打架。

                // ⑤ 相机
                EnsureCameraTarget();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[身份守卫] 附属组件接管失败: {e}");
            }
        }

        /// <summary>
        /// ★ 输入归属强制对齐（每帧执行，开销极小）。
        ///
        /// 输入接收器（MyPhysics.inputHandler）决定谁响应 WASD。
        /// 必须保证：**本机角色开着，14 个假人全关着**。
        ///
        /// 上一版的 bug：ReestablishLocalPlayer 被开局时仍指向假人的
        /// PlayerControl.LocalPlayer 调用，把假人的 inputHandler 打开了，
        /// 结果玩家按键动的是假人。这里用权威身份来源做双向强制，避免再出现这种现象。
        /// </summary>
        private void EnforceInputOwnership()
        {
            try
            {
                // 1) 所有假人：必须关闭
                for (int i = 0; i < _brains.Count; i++)
                {
                    var bp = _brains[i].Player;
                    if (bp == null) continue;
                    var phys = bp.MyPhysics;
                    if (phys == null || phys.inputHandler == null) continue;
                    if (phys.inputHandler.enabled)
                    {
                        phys.inputHandler.enabled = false;
                        Plugin.Logger.LogWarning($"[输入归属] 假人#{i} 的 inputHandler 被打开过，已重新关闭");
                    }
                }

                // 2) 本机角色：必须开启（身份来源用权威的 ClientData.Character）
                var auc = AmongUsClient.Instance;
                if (auc == null) return;
                var myClient = auc.GetClient(auc.ClientId);
                var real = myClient != null ? myClient.Character : null;
                if (real == null || IsBot(real)) return;

                var rp = real.MyPhysics;
                if (rp != null && rp.inputHandler != null && !rp.inputHandler.enabled)
                {
                    rp.inputHandler.enabled = true;
                    Plugin.Logger.LogWarning("[输入归属] 本机 inputHandler 被关闭过，已重新开启");
                }
            }
            catch (Exception e)
            {
                if (!_loggedInput) { _loggedInput = true; Plugin.Logger.LogError($"[输入归属] 失败: {e}"); }
            }
        }

        private bool _loggedInput;

        /// <summary>比赛开始时把本机身份的附属组件整体重建一次</summary>
        private void ReestablishOnMatchStart()
        {
            // ★ 必须用权威身份来源，不能用 PlayerControl.LocalPlayer ——
            // 开局那一刻它往往还指向假人，上一版的回退就是这么来的。
            var auc = AmongUsClient.Instance;
            var myClient = auc != null ? auc.GetClient(auc.ClientId) : null;
            var real = myClient != null ? myClient.Character : null;

            if (real == null || IsBot(real))
            {
                Plugin.Logger.LogWarning("[身份守卫] 开局时拿不到权威本机角色（或它指向假人），跳过重建。");
                return;
            }

            Plugin.Logger.LogInfo("[身份守卫] 开局，重建本机身份的附属组件（灯光/输入）");
            ReestablishLocalPlayer(real);
        }

        private int _localPlayerFixCount;
        private bool _loggedLocal;

        /// <summary>相机修复次数（诊断用）</summary>
        private int _camFixCount;

        /// <summary>
        /// ★ 相机目标守卫 —— 直接对症「视角被锁在假人身上，自己动不了」。
        ///
        /// 依据（确证的官方 API）：
        ///     FollowerCamera.Target          { get; set; }
        ///     FollowerCamera.SetTarget(MonoBehaviour)
        ///     FollowerCamera.SnapToTarget()
        ///     HudManager.PlayerCam
        ///
        /// 游戏把相机目标指到了某个假人身上。这里每帧校验一次，
        /// 一旦发现目标不是本机玩家就强制拉回来 —— 不管游戏是因 pid 冲突
        /// 还是别的原因指错的，都能覆盖掉。
        /// </summary>
        private void EnsureCameraTarget()
        {
            try
            {
                var lp = PlayerControl.LocalPlayer;
                if (lp == null) return;

                var hm = HudManager.Instance;
                if (hm == null) return;

                var cam = hm.PlayerCam;
                if (cam == null) return;

                var target = cam.Target;

                bool ok = target != null && target.GetInstanceID() == lp.GetInstanceID();
                if (ok) return;

                cam.SetTarget(lp);
                cam.SnapToTarget();

                _camFixCount++;
                if (_camFixCount <= 5 || _camFixCount % 60 == 0)
                {
                    Plugin.Logger.LogWarning(
                        $"[相机守卫] 目标原为 '{(target != null ? target.name : "null")}'，已强制拉回本机玩家" +
                        $"（第 {_camFixCount} 次）");
                }
            }
            catch (Exception e)
            {
                if (!_loggedCam)
                {
                    _loggedCam = true;
                    Plugin.Logger.LogError($"[相机守卫] 失败: {e}");
                }
            }
        }

        private bool _loggedCam;

        /// <summary>清空（返回主菜单时）</summary>
        internal static void Detach()
        {
            if (_instance != null) _instance._brains.Clear();
        }

        /// <summary>
        /// 把假人从本机客户端「除名」：OwnerId 改为 NoClientId。
        ///
        /// 必须在开局等待期之后执行，否则服务端会等不到 owner 数据而超时。
        /// 除名后游戏不再把假人当成本机玩家的分身，位置同步随之切断，
        /// 我们写入的速度才真正生效。
        /// </summary>
        private void ReOwnBots()
        {
            int changed = 0;
            for (int i = 0; i < _brains.Count; i++)
            {
                var pc = _brains[i].Player;
                if (pc == null) continue;

                try
                {
                    int before = pc.OwnerId;
                    pc.OwnerId = InnerNetClient.NoClientId;
                    changed++;

                    if (i < 3)
                    {
                        Plugin.Logger.LogInfo(
                            $"[除名] 假人#{i} OwnerId {before} -> {pc.OwnerId} " +
                            $"(NoClientId={InnerNetClient.NoClientId})");
                    }
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogError($"[除名] 假人#{i} 失败: {e.Message}");
                }
            }

            Plugin.Logger.LogInfo($"[除名] 已将 {changed} 个假人从本机客户端除名，" +
                                  "位置同步应已切断，速度驱动从此刻起才真正生效。");
        }

        private void Update()
        {
            try
            {
                if (_brains.Count == 0) return;

                // ★ 三个守卫改为**周期执行**（见下方 2 秒采样块）。
                //
                // 之前是每帧执行，与游戏自己的身份/HUD 状态机持续对抗 ——
                // 实测症状：地图打不开、击杀键 CD 卡在 1 秒、玩家自己不能动。
                // 方案A 的原则是「只拥有位置，不争抢身份」，
                // 所以把干预降到最低频率，只在真的被改坏时才纠正。

                // ★ 两段式归属：地图加载完成后把假人「除名」。
                //
                // 实测证据：假人速度为 (0,0) 却在向玩家靠拢（位置差 22.41 → 1.71），
                // 证明跟随是游戏**直接改写位置**、与速度无关。
                // 根因就是 ownerId = 本机 ClientId 让游戏把它们当成玩家的分身。
                //
                // 但开局前不能改 —— 服务端会等不到 owner 数据而超时断连。
                // 超时检查只发生在开局等待期，所以在地图加载完成后除名是安全的。
                if (!_reowned && IsMatchRunning())
                {
                    _reowned = true;
                    // 除名只在走 NoClientId 路线时才需要。
                    // 真凶（inputHandler）找到后，归属保持本机 ClientId 更安全：
                    // 网络层不会超时，位置同步问题也随输入劫持一同消失。
                    if (Plugin.UseNoClientOwner) ReOwnBots();

                    // 开局时把本机身份的附属组件（灯光/输入/HUD）整体重建一次 ——
                    // 实测反馈：进地图后灯光会留在假人身上，仅修身份不够。
                    ReestablishOnMatchStart();
                }

                float dt = Time.deltaTime;

                for (int i = 0; i < _brains.Count; i++)
                {
                    _brains[i].Age(dt);
                    if (Plugin.EnableBotMovement) _brains[i].Tick(dt);
                }

                // 周期性离散度采样
                _sampleTimer += dt;
                if (_sampleTimer >= SampleInterval)
                {
                    _sampleTimer = 0f;

                    // 周期执行的三个守卫（顺序重要：先身份，再相机，最后输入归属）
                    EnsureLocalPlayer();
                    EnsureCameraTarget();
                    // EnforceInputOwnership() 已移除：位置接管下不需要碰输入接收器，它只会和游戏打架

                    // 门控诊断：这是排查「假人不动」的第一手数据
                    Plugin.Logger.LogInfo($"[门控] {GateDebug()}");

                    // pid 冲突在大厅阶段就已经存在（实测），不能只在开局时修。
                    // 这个调用是幂等的：没有冲突时只是一次 15 元素的遍历。
                    // FixPidCollisions() 已停用：身份修复改用 OwnerId 判定，不再依赖 pid

                    LogSpread();
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[AI] BotManager.Update 异常: {e}");
            }
        }

        /// <summary>
        /// 计算所有假人相对玩家的距离，输出 min/max/平均。
        ///
        /// 判据非常直接：
        ///   max 距离 ≈ 0            → 假人黏在玩家身上（共享输入，ownerId 没生效）
        ///   max 距离增长且离散      → 独立移动成功
        /// </summary>
        private void LogSpread()
        {
            try
            {
                var local = PlayerControl.LocalPlayer;
                if (local == null) return;
                Vector3 origin = local.transform.position;

                float min = float.MaxValue, max = 0f, sum = 0f;
                int n = 0;
                for (int i = 0; i < _brains.Count; i++)
                {
                    var p = _brains[i].Position;
                    // 只算同场景的（换场景时坐标会突变，加个远距离过滤）
                    float d = Vector2.Distance(new Vector2(origin.x, origin.y), new Vector2(p.x, p.y));
                    if (d > 100f) continue;
                    if (d < min) min = d;
                    if (d > max) max = d;
                    sum += d;
                    n++;
                }
                if (n == 0) return;

                _sampleCount++;
                // 前两次全量打，之后每 5 次打一次，避免刷屏
                bool verbose = _sampleCount <= 2 || _sampleCount % 5 == 0;

                Plugin.Logger.LogInfo(
                    $"[AI] 采样#{_sampleCount} 假人数={n} 距玩家 min={min:F2} max={max:F2} avg={sum / n:F2} " +
                    $"玩家=({origin.x:F2},{origin.y:F2}) 首个假人=({_brains[0].Position.x:F2},{_brains[0].Position.y:F2})");

                Plugin.Logger.LogInfo($"[HUD] {DescribeHud()}");
                Plugin.Logger.LogInfo($"[速度诊断] {DescribeVelocity()}");

                if (verbose)
                {
                    var sb = new System.Text.StringBuilder("[AI]   各假人(pid@坐标): ");
                    for (int i = 0; i < _brains.Count && i < 15; i++)
                    {
                        var p = _brains[i].Position;
                        sb.Append($"#{i}/p{_brains[i].PlayerId}({p.x:F1},{p.y:F1}) ");
                    }
                    Plugin.Logger.LogInfo(sb.ToString());

                    // pid 碰撞检查：本机 pid 是否和某个假人重合
                    // （视角锁在假人身上很可能就是这里出了问题）
                    var lp2 = PlayerControl.LocalPlayer;
                    int myPid = lp2 != null ? lp2.PlayerId : -1;
                    var dup = new System.Text.StringBuilder();
                    for (int i = 0; i < _brains.Count; i++)
                        if (_brains[i].PlayerId == myPid) dup.Append($"#{i} ");
                    Plugin.Logger.LogWarning(
                        $"[AI] pid 检查: 本机 pid={myPid}  AllPlayerControls={(PlayerControl.AllPlayerControls != null ? PlayerControl.AllPlayerControls.Count : -1)}" +
                        (dup.Length > 0 ? $"  ⚠️ 与假人 [{dup}] 冲突!" : "  ✅ 无冲突"));
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[AI] 采样失败: {e.Message}");
            }
        }

        /// <summary>
        /// 问题 C 的诊断：击杀键缺失 / 击杀后变成鬼魂操作键，开关一次地图能恢复。
        ///
        /// 「开关地图就恢复」说明可能是 HUD 刷新时机问题，状态本身没错。
        /// 但如果日志显示本机 isDead **真的变了**，性质就完全不同 ——
        /// 很可能是 ownerId = 本机 ClientId 导致游戏把「我们的某个假人死亡」
        /// 当成了「本机玩家死亡」。这份日志就是用来区分这两种情况的。
        /// </summary>
        private static string DescribeHud()
        {
            try
            {
                var lp = PlayerControl.LocalPlayer;
                var d = lp != null ? lp.Data : null;
                var auc = AmongUsClient.Instance;
                var hm = HudManager.Instance;

                string role = d != null ? d.RoleType.ToString() : "?";
                string dead = d != null ? d.IsDead.ToString() : "?";
                string state = auc != null ? auc.GameState.ToString() : "?";

                string kill = "?", vent = "?", use = "?", report = "?";
                if (hm != null)
                {
                    kill = Active(hm.KillButton != null ? hm.KillButton.gameObject : null);
                    vent = Active(hm.ImpostorVentButton != null ? hm.ImpostorVentButton.gameObject : null);
                    use = Active(hm.UseButton != null ? hm.UseButton.gameObject : null);
                    report = Active(hm.ReportButton != null ? hm.ReportButton.gameObject : null);
                }

                return $"本机 pid={(lp != null ? lp.PlayerId.ToString() : "?")} role={role} isDead={dead} " +
                       $"GameState={state} | Kill键={kill} Vent键={vent} 使用键={use} 报告键={report} " +
                       $"| 比赛中={IsMatchRunning()}";
            }
            catch (Exception e)
            {
                return $"读取失败: {e.Message}";
            }
        }

        private static string Active(GameObject go)
        {
            try { return go != null && go.activeSelf ? "显示" : "隐藏"; }
            catch { return "?"; }
        }

        /// <summary>
        /// ★ 决定性诊断：区分「速度通道被劫持」与「位置被直接改写」。
        ///
        /// 判读方式：
        ///   重定向命中=0            → 游戏的输入根本不过托管 setter（被内联）
        ///   假人速度 ≈ 玩家速度     → 跟随是速度层面的，只是我们没拦住
        ///   假人速度 ≠ 玩家速度，
        ///     但位置仍同步跟上       → **跟随是直接写位置实现的**，改速度永远没用
        /// </summary>
        private static string DescribeVelocity()
        {
            try
            {
                var lp = PlayerControl.LocalPlayer;
                if (lp == null || _instance == null || _instance._brains.Count == 0)
                    return "无数据";

                var brain = _instance._brains[0];
                var bp = brain.Player;
                if (bp == null) return "假人已失效";

                Vector2 bv = bp.MyPhysics != null ? bp.MyPhysics.GetVelocity() : Vector2.zero;
                Vector2 lv = lp.MyPhysics != null ? lp.MyPhysics.GetVelocity() : Vector2.zero;

                Vector3 bp3 = bp.transform.position;
                Vector3 lp3 = lp.transform.position;
                float posDist = Vector2.Distance(new Vector2(bp3.x, bp3.y), new Vector2(lp3.x, lp3.y));

                return $"重定向命中={BotVelocityRedirectPatch.HitCount} 覆盖命中={BotInputOverridePatch.HitCount} " +
                       $"位置接管命中={BotPositionOverridePatch.HitCount} | " +
                       $"假人#0速度=({bv.x:F2},{bv.y:F2}) 玩家速度=({lv.x:F2},{lv.y:F2}) " +
                       $"速差={Vector2.Distance(bv, lv):F2} | 位置差={posDist:F2}";
            }
            catch (Exception e)
            {
                return $"读取失败: {e.Message}";
            }
        }
    }
}
