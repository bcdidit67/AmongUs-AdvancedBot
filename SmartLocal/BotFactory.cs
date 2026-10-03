using System;
using AmongUs.GameOptions;
using InnerNet;
using UnityEngine;

namespace SmartLocal
{
    /// <summary>
    /// 假人构造工厂。两条路径都实现，由 BotSpawner 自适应选择。
    ///
    /// 【确证】AmongUsClient.PlayerPrefab : PlayerControl      ← 玩家预置体
    /// 【确证】InnerNetClient.GetOrCreateClient(ClientData)    ← 把假客户端登记进 allClients
    /// 【确证】AmongUsClient.CreatePlayer(ClientData) -> IEnumerator
    /// 【确证】InnerNetClient.Spawn(InnerNetObject, int ownerId, SpawnFlags)
    /// 【确证】GameData.AddDummy(PlayerControl) -> NetworkedPlayerInfo
    /// 【确证】GameData.GetAvailableId() -> SByte
    /// 【确证】PlayerControl.isDummy : bool
    /// 【确证】Constants.DummyNamePrefix : static string
    /// </summary>
    internal static class BotFactory
    {
        private static readonly string[] Names =
            { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel",
              "India", "Juliet", "Kilo", "Lima", "Mike", "November", "Oscar" };

        /// <summary>假人 clientId 基址，取高位避免与真玩家（0..127）冲突。</summary>
        private const int BotClientIdBase = 1000;

        internal static string BotName(int index) => Names[index % Names.Length];

        /// <summary>路径 A 用：构造一个假 ClientData。
        ///
        /// ⚠️ 上一轮失败的关键教训：ProductUserId / FriendCode 之前传的是 string.Empty。
        /// 断连快照显示本机玩家这两项「有」、假人「&lt;空&gt;」，而服务端的报错正是
        /// "Timeout while waiting for other player data" —— 字面就是「在等其他玩家的数据」。
        /// 所以这里必须填成非空值，模拟客户端已经把自己的账号数据发过来了。
        /// </summary>
        internal static ClientData CreateClientData(int index)
        {
            var platform = new PlatformSpecificData();
            platform.Platform = Platforms.StandaloneSteamPC;
            platform.PlatformName = "Steam";

            return new ClientData(
                BotClientIdBase + index,
                $"{Constants.DummyNamePrefix}{BotName(index)}",
                platform,
                1u,
                MakePuid(index),
                MakeFriendCode(index));
        }

        /// <summary>造一个格式合法的假 EOS ProductUserId（32 位十六进制，非全零）。</summary>
        private static string MakePuid(int index)
            => (0x10000000UL + (ulong)index + 1UL).ToString("x8").PadLeft(32, '0');

        /// <summary>造一个假的 6 位好友码。</summary>
        private static string MakeFriendCode(int index)
            => $"SL{(index + 1):D4}";

        /// <summary>
        /// 路径 B：实例化 PlayerControl 预置体并直接登记为 dummy，绕开网络层。
        /// 返回 true 表示调用序列本身没抛异常（**不代表游戏已接受**，需外部验证）。
        /// </summary>
        internal static bool SpawnDirectDummy(int index)
        {
            var auc = AmongUsClient.Instance;
            if (auc == null) { Plugin.Logger.LogError("[路径B] AmongUsClient.Instance 为空。"); return false; }

            var prefab = auc.PlayerPrefab;
            if (prefab == null) { Plugin.Logger.LogError("[路径B] AmongUsClient.PlayerPrefab 为空，无法实例化。"); return false; }

            var gd = GameData.Instance;
            if (gd == null) { Plugin.Logger.LogError("[路径B] GameData.Instance 为空。"); return false; }

            // 1) 实例化
            var pc = UnityEngine.Object.Instantiate(prefab);
            if (pc == null) { Plugin.Logger.LogError("[路径B] Instantiate 返回 null。"); return false; }

            // 2) 标记为假人（官方原生标志位）
            pc.isDummy = true;

            // 3) 分配玩家 ID
            byte pid = (byte)gd.GetAvailableId();
            pc.PlayerId = pid;

            // 4) 起名
            string name = $"{Constants.DummyNamePrefix}{BotName(index)}";
            pc.SetName(name);
            pc.SetColor(index % 12);

            Plugin.Logger.LogInfo($"[路径B] 已实例化 PlayerControl pid={pid} name={name}，准备登记");

            // 5) 登记进 GameData（不登记的话开局校验/职业分配都看不到它）
            var info = gd.AddDummy(pc);
            Plugin.Logger.LogInfo($"[路径B] AddDummy 返回 {(info != null ? "非空" : "null")}");

            // 6) 交给网络系统生成（OwnerId 用 HostId，因为是本地房主）
            auc.Spawn(pc, auc.HostId, SpawnFlags.None);
            Plugin.Logger.LogInfo($"[路径B] Spawn 已调用 ownerId={auc.HostId}");

            return true;
        }

        /// <summary>
        /// 假人名字修正。
        ///
        /// 现象：路径 A 生成的假人在大厅里名字显示为 `?????`。
        /// 原因：正常流程里玩家名字是靠网络握手同步到 NetworkedPlayerInfo 的，
        /// 我们的假人没有真实客户端，那一步不会发生，PlayerName 保持为空。
        /// 修法：房主侧直接把 PlayerName 写进 GameData 并标脏。
        /// </summary>
        internal static void FixupBotNames()
        {
            var gd = GameData.Instance;
            if (gd == null || gd.AllPlayers == null)
            {
                Plugin.Logger.LogError("[改名] GameData.AllPlayers 不可用。");
                return;
            }

            var list = gd.AllPlayers;
            int fixedCount = 0;

            for (int i = 0; i < list.Count; i++)
            {
                NetworkedPlayerInfo info = list[i];
                if (info == null) continue;

                int cid = info.ClientId;
                if (cid < BotClientIdBase) continue;   // 只处理我们的假人

                string want = ExpectedName(cid);
                string had = info.PlayerName;

                info.PlayerName = want;
                info.MarkDirty();

                var pc = info.Object;
                if (pc != null)
                {
                    pc.SetName(want);
                    pc.SetColor((cid - BotClientIdBase) % 12);
                }

                fixedCount++;
                Plugin.Logger.LogInfo($"[改名] clientId={cid} isDummy={(pc != null ? pc.isDummy.ToString() : "?")} '{had}' -> '{want}'");
            }

            Plugin.Logger.LogInfo($"[改名] 共修正 {fixedCount} 个假人名字。");

            // 自诊断：如果一个都没匹配上，说明 ClientId 不如预期，
            // 把全部玩家的身份快照打出来供分析。
            if (fixedCount == 0)
            {
                Plugin.Logger.LogWarning("[改名] 未匹配到任何假人，输出全部玩家快照：");
                for (int i = 0; i < list.Count; i++)
                {
                    var info = list[i];
                    if (info == null) continue;
                    var pc = info.Object;
                    Plugin.Logger.LogWarning(
                        $"  [{i}] clientId={info.ClientId} playerId={info.PlayerId} " +
                        $"playerName='{info.PlayerName}' isDummy={(pc != null ? pc.isDummy.ToString() : "?")}");
                }
            }
        }

        /// <summary>由 clientId 反推假人应有的名字。</summary>
        private static string ExpectedName(int clientId)
        {
            int idx = clientId - BotClientIdBase;
            if (idx < 0) idx = 0;
            return $"{Constants.DummyNamePrefix}{BotName(idx)}";
        }

        /// <summary>
        /// 替假人客户端「报到」。
        ///
        /// 问题现象：按「开始」后断连，服务端日志为
        ///   Server > Client DC because Error: Timeout while waiting for other player data
        /// 调用栈显示是在 InnerNetClient.Update() 里超时踢人。
        ///
        /// 成因：假人通过 GetOrCreateClient 登记成了「远端客户端」，
        /// 但它们是空壳，永远不会走正常握手（发送自己的玩家数据 / ready 消息）。
        /// 服务端 Update 里一直在等这些客户端报到，等不到就 DC。
        ///
        /// 修法：房主侧直接替它们把就绪标志置位，跳过等待。
        /// ClientData 的 IsReady / InScene / IsBeingCreated 都是 public set。
        /// </summary>
        internal static void FixupBotClients(bool verbose = true)
        {
            var auc = AmongUsClient.Instance;
            if (auc == null) { Plugin.Logger.LogError("[报到] AmongUsClient.Instance 为空。"); return; }

            var gd = GameData.Instance;
            if (gd == null || gd.AllPlayers == null) { Plugin.Logger.LogError("[报到] GameData 不可用。"); return; }

            var list = gd.AllPlayers;
            int done = 0;

            // 本机客户端自己也要置为就绪。
            // 断连快照里本机 IsReady=False —— 超时判定很可能就是 !client.IsReady，
            // 而本机这一项我们之前从来没管过。
            var me = auc.GetClient(auc.ClientId);
            if (me != null && !me.IsReady)
            {
                me.IsReady = true;
                if (verbose)
                    Plugin.Logger.LogInfo($"[报到] 本机客户端 id={auc.ClientId} 已置为 IsReady=true");
            }

            for (int i = 0; i < list.Count; i++)
            {
                NetworkedPlayerInfo info = list[i];
                if (info == null) continue;
                if (info.ClientId < BotClientIdBase) continue;

                var cd = auc.GetClient(info.ClientId);
                if (cd == null)
                {
                    Plugin.Logger.LogWarning($"[报到] clientId={info.ClientId} 在 allClients 里找不到。");
                    continue;
                }

                bool r0 = cd.IsReady, s0 = cd.InScene, c0 = cd.IsBeingCreated;
                string p0 = cd.ProductUserId, f0 = cd.FriendCode;

                cd.IsReady = true;
                cd.InScene = true;
                cd.IsBeingCreated = false;

                // 补齐账号数据（只在为空时填，避免覆盖真实数据）
                int idx = cd.Id - BotClientIdBase;
                if (string.IsNullOrEmpty(cd.ProductUserId)) cd.ProductUserId = MakePuid(idx);
                if (string.IsNullOrEmpty(cd.FriendCode)) cd.FriendCode = MakeFriendCode(idx);
                if (cd.PlayerLevel == 0) cd.PlayerLevel = 1;

                done++;
                if (verbose)
                {
                    Plugin.Logger.LogInfo(
                        $"[报到] clientId={cd.Id} IsReady {r0}->true InScene {s0}->true IsBeingCreated {c0}->false  " +
                        $"Puid '{p0}'->'{cd.ProductUserId}'  FC '{f0}'->'{cd.FriendCode}'  Character={(cd.Character != null)}");
                }
            }

            if (verbose)
                Plugin.Logger.LogInfo($"[报到] 共处理 {done} 个假人客户端。");
        }

        /// <summary>断连前/后把 allClients 全量状态打出来，用于分析服务端到底在等什么。</summary>
        internal static void DumpClientStates(string tag)
        {
            try
            {
                var auc = AmongUsClient.Instance;
                if (auc == null || auc.allClients == null)
                {
                    Plugin.Logger.LogWarning($"[客户端快照/{tag}] allClients 不可用。");
                    return;
                }

                var clients = auc.allClients;
                Plugin.Logger.LogWarning($"[客户端快照/{tag}] allClients.Count={clients.Count}  本机 ClientId={auc.ClientId}  HostId={auc.HostId}");
                for (int i = 0; i < clients.Count; i++)
                {
                    var c = clients[i];
                    if (c == null) { Plugin.Logger.LogWarning($"  [{i}] null"); continue; }
                    Plugin.Logger.LogWarning(
                        $"  [{i}] id={c.Id} name='{c.PlayerName}' IsReady={c.IsReady} InScene={c.InScene} " +
                        $"IsBeingCreated={c.IsBeingCreated} Character={(c.Character != null)} " +
                        $"Puid='{(string.IsNullOrEmpty(c.ProductUserId) ? "<空>" : "有")}' FC='{(string.IsNullOrEmpty(c.FriendCode) ? "<空>" : "有")}'");
                }

                var gd = GameData.Instance;
                if (gd != null && gd.AllPlayers != null)
                    Plugin.Logger.LogWarning($"[客户端快照/{tag}] GameData.AllPlayers.Count={gd.AllPlayers.Count}");
                Plugin.Logger.LogWarning($"[客户端快照/{tag}] PlayerControl.AllPlayerControls.Count={CountAllPlayers()}");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[客户端快照/{tag}] 采集失败: {e}");
            }
        }

        private static int CountAllPlayers()
        {
            try
            {
                var l = PlayerControl.AllPlayerControls;
                return l != null ? l.Count : -1;
            }
            catch { return -1; }
        }

        // ═══════════════════════════════════════════════════════════════
        //  假人路径（isDummy）：不注册客户端，绕开内网超时机制
        // ═══════════════════════════════════════════════════════════════
        //
        // 为什么放弃「伪造 ClientData」：
        //   实测 15 个客户端 IsReady/InScene/Character 全为真、Puid/FC 全非空、
        //   GameData 和 PlayerControl 都是 15，超时依旧触发。
        //   说明服务端等的不是 ClientData 的任何一个字段，而是真实连接才会产生的
        //   内部确认状态 —— 无法伪造。
        //
        // 新思路：用游戏自带的「非网络玩家」设施。
        //   GameData.AddDummy(PlayerControl) 不接受 ClientData 参数
        //   → 说明 dummy 玩家天然没有客户端，不进 allClients，服务端无物可等。
        //
        //   佐证：Constants.DummyNamePrefix / SaveIconCamera.saveIconDummy
        //   都表明官方自己会造 dummy 玩家。

        /// <summary>本批创建的假人，供 BotManager 接管。</summary>
        internal static readonly System.Collections.Generic.List<PlayerControl> CreatedBots
            = new System.Collections.Generic.List<PlayerControl>();

        /// <summary>
        /// 全部走 isDummy 路径生成假人。
        /// 返回调用序列成功的数量（不代表游戏已完全接受，需看后续日志）。
        /// </summary>
        internal static int SpawnAllDummies(int count)
        {
            CreatedBots.Clear();

            var auc = AmongUsClient.Instance;
            if (auc == null) { Plugin.Logger.LogError("[假人] AmongUsClient.Instance 为空。"); return 0; }

            var gd = GameData.Instance;
            if (gd == null) { Plugin.Logger.LogError("[假人] GameData.Instance 为空。"); return 0; }

            var prefab = auc.PlayerPrefab;
            if (prefab == null) { Plugin.Logger.LogError("[假人] PlayerPrefab 为空。"); return 0; }

            var lobby = LobbyBehaviour.Instance;
            var seats = lobby != null ? lobby.SpawnPositions : null;
            int seatCount = seats != null ? seats.Length : 0;

            int made = 0;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    SpawnOneDummy(auc, gd, prefab, i, seats, seatCount);
                    made++;
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogError($"[假人] 第 {i} 个生成失败: {e}");
                }
            }

            Plugin.Logger.LogInfo($"[假人] 本批成功调用 {made}/{count} 个。");
            return made;
        }

        private static void SpawnOneDummy(AmongUsClient auc, GameData gd,
                                          PlayerControl prefab, int index,
                                          Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<UnityEngine.Vector2> seats,
                                          int seatCount)
        {
            string name = $"{Constants.DummyNamePrefix}{BotName(index)}";

            // 1) 实例化玩家预置体
            var pc = UnityEngine.Object.Instantiate(prefab);
            if (pc == null) { Plugin.Logger.LogError($"[假人] {name} Instantiate 返回 null。"); return; }

            // 2) 标记为非网络假人 —— 这是官方的「非真实玩家」标志位
            pc.isDummy = true;

            // 3) 分配玩家 ID（唯一，从 GameData 要）
            byte pid = (byte)gd.GetAvailableId();
            pc.PlayerId = pid;

            // ⚠️ 这里**不再**关闭假人的 inputHandler。
            //
            // 历史上这一行是用来修「一按 WASD 假人一起走」的，但那其实是
            // DummyBehaviour / 位置同步 造成的，关错了对象。
            //
            // 方案A 的架构下（我们拥有位置、每帧写入），假人的速度驱动会被
            // 我们的位置写入完全覆盖，所以**不需要碰输入接收器**。
            // 而且实践发现：碰它会波及游戏自身的输入判定 ——
            // 实测症状是「玩家自己被固定、按 WASD 动的却是假人」。
            //
            // 原则：只拥有位置，别的都不碰。

            // 状态复位（同样参考 MCI 的做法，避免残留动画/移动状态）
            try
            {
                pc.MyPhysics?.ResetMoveState(true);
                pc.MyPhysics?.ResetAnimState();
            }
            catch { /* 非致命 */ }

            // ★★★ 真正的输入读取者：KeyboardJoystick ★★★
            //
            // 上一轮我关的是 MyPhysics.inputHandler（类型是 SpecialInputHandler），
            // 日志显示 28 次全部关闭成功，但假人依然跟随玩家 —— 说明关错了组件。
            //
            // 决定性证据（来自用户实测）：
            //   打开设置菜单（输入被屏蔽）→ 假人乱走（我们的 AI 方向生效）
            //   关掉菜单（输入恢复）      → 假人又全体跟随
            // 证明假人确实在读取键盘输入，只是读取者不是 SpecialInputHandler。
            //
            // MCI 这个 Mod patch 的是 KeyboardJoystick.Update —— 那才是键盘读取组件，
            // 它是挂在角色上的独立 MonoBehaviour。
            try
            {
                var kj = pc.GetComponent<KeyboardJoystick>();
                if (kj != null)
                {
                    kj.enabled = false;
                    Plugin.Logger.LogInfo($"[假人] {name} KeyboardJoystick 已关闭");
                }
                else
                {
                    Plugin.Logger.LogWarning($"[假人] {name} 身上没有 KeyboardJoystick 组件（需另找输入源）");
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[假人] {name} 关闭 KeyboardJoystick 失败: {e.Message}");
            }

            // ★★★★ 真正的元凶：DummyBehaviour ★★★★
            //
            // 组件清单（实测）显示假人身上挂着 8 个 MonoBehaviour，其中一个是
            // **DummyBehaviour** —— 游戏自带的「假人行为」组件。
            //
            // Among Us 的 dummy 本来就是装饰性的（商店预览那类），
            // 这个组件的作用就是让它们**跟着本机玩家移动**，而且是**直接写位置**。
            //
            // 这一条解释了我们踩过的所有坑：
            //   为什么改 ownerId 无效        → 跟随与归属无关
            //   为什么拦 SetNormalizedVelocity 无效 → 它根本不经速度通道
            //   为什么位置接管能压制它        → 那是唯一比它更晚的写入点
            //   为什么关 inputHandler 无效    → 输入源压根不在假人身上
            //   为什么开设置菜单会「乱走」    → 玩家不能动时它没得跟，我们的方向才显现
            //
            // 而且是我们自己打开它的：pc.isDummy = true 很可能就是激活开关。
            //
            // 修法：直接销毁这个组件。保留 isDummy 标志位不动（GameData 可能依赖它），
            // 只把「行为」摘掉。
            try
            {
                var db = pc.GetComponent<DummyBehaviour>();
                if (db != null)
                {
                    bool wasEnabled = db.enabled;
                    db.enabled = false;                 // 立即生效，本帧就不再跑
                    UnityEngine.Object.Destroy(db);     // 再彻底移除
                    Plugin.Logger.LogInfo($"[假人] {name} DummyBehaviour 已移除（原 enabled={wasEnabled}）");
                }
                else
                {
                    Plugin.Logger.LogWarning($"[假人] {name} 身上没有 DummyBehaviour");
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[假人] {name} 移除 DummyBehaviour 失败: {e.Message}");
            }

            pc.moveable = true;

            // 诊断：把第一个假人身上所有组件类型打出来。
            // 如果 KeyboardJoystick 不在身上，这份清单就是找到真正输入源的唯一线索 ——
            // 上一轮的教训是「关错了组件却以为成功」，不能再靠猜。
            if (index == 0)
            {
                try
                {
                    var comps = pc.GetComponents<UnityEngine.MonoBehaviour>();
                    var sb = new System.Text.StringBuilder();
                    int n = comps != null ? comps.Length : 0;
                    for (int i = 0; i < n; i++)
                    {
                        var c = comps[i];
                        if (c == null) continue;
                        string tn;
                        try { tn = c.GetIl2CppType().Name; }
                        catch { tn = c.GetType().Name; }
                        sb.Append(tn).Append(" | ");
                    }
                    Plugin.Logger.LogInfo($"[组件清单] 假人#0 共 {n} 个 MonoBehaviour: {sb}");
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning($"[组件清单] 采集失败: {e.Message}");
                }
            }

            // 4) 外观
            pc.SetName(name);
            pc.SetColor(index % 12);

            Plugin.Logger.LogInfo($"[假人] {name} 实例化完成 pid={pid}，准备注册 GameData");

            // 5) 注册进 GameData（不登记的话开局校验和职业分配都看不到它）
            var info = gd.AddDummy(pc);
            Plugin.Logger.LogInfo($"[假人] {name} AddDummy 返回 {(info != null ? "非空" : "null")}");

            if (info != null)
            {
                info.PlayerName = name;
                info.MarkDirty();
            }

            // 6) 网络对象登记。
            //    ownerId 策略见 Plugin.UseNoClientOwner：
            //      NoClientId  → 游戏不把本地输入套到它们身上（独立 AI 前提）
            //      本机 ClientId → 蜂群模式（共享玩家输入）
            int owner = Plugin.UseNoClientOwner ? InnerNetClient.NoClientId : auc.ClientId;
            auc.Spawn(pc, owner, SpawnFlags.None);
            Plugin.Logger.LogInfo($"[假人] {name} Spawn 已调用 ownerId={owner}" +
                                  (Plugin.UseNoClientOwner ? "（NoClientId）" : "（本机，蜂群模式）"));

            // 记录下来交给 BotManager 接管
            CreatedBots.Add(pc);

            // 7) 手动入座兜底：isDummy 路径不经过 CreatePlayer，
            //    PlayerPhysics.CoSpawnPlayer 可能不会被自动触发，先按座位号摆好。
            if (seatCount > 0 && seats != null)
            {
                try
                {
                    var seat = seats[index % seatCount];
                    var t = pc.transform;
                    var pos = t.position;
                    t.position = new UnityEngine.Vector3(seat.x, seat.y, pos.z);
                    Plugin.Logger.LogInfo($"[假人] {name} 已摆到座位 {index % seatCount} ({seat.x:F2}, {seat.y:F2})");
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning($"[假人] {name} 入座失败: {e.Message}");
                }
            }
        }

        /// <summary>
        /// dummy 玩家的名字修正。
        /// dummy 的 ClientId 是 NoClientId（负数），不能复用 FixupBotNames 的过滤条件，
        /// 这里改用 isDummy 标志来识别。
        /// </summary>
        internal static void FixupDummyNames()
        {
            try
            {
                var all = PlayerControl.AllPlayerControls;
                if (all == null) { Plugin.Logger.LogWarning("[假人改名] AllPlayerControls 不可用。"); return; }

                int n = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    var pc = all[i];
                    if (pc == null || !pc.isDummy) continue;

                    var info = pc.Data;
                    if (info == null) continue;

                    string want = info.PlayerName;
                    if (string.IsNullOrEmpty(want)) continue;

                    pc.SetName(want);
                    pc.SetColor((n) % 12);
                    info.MarkDirty();
                    n++;

                    var t = pc.transform;
                    Plugin.Logger.LogInfo($"[假人改名] pid={pc.PlayerId} name='{want}' pos=({t.position.x:F2},{t.position.y:F2})");
                }
                Plugin.Logger.LogInfo($"[假人改名] 共处理 {n} 个 dummy 玩家。");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[假人改名] 失败: {e}");
            }
        }

        /// <summary>
        /// 阶段 6：职业分配。
        /// 推荐直接调原生随机分配 —— 只要假人进了 GameData.AllPlayers，
        /// 内鬼人数上限 / 角色概率 / GetAdjustedNumImpostors 全部自动正确处理。
        /// </summary>
        internal static void AssignRoles()
        {
            var rm = RoleManager.Instance;
            if (rm == null) { Plugin.Logger.LogError("RoleManager.Instance 为空。"); return; }

            Plugin.Logger.LogInfo("调用 RoleManager.SelectRoles() 进行原生职业分配。");
            rm.SelectRoles();
        }
    }
}
