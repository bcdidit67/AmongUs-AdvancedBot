using System;
using BepInEx.Logging;
using HarmonyLib;
using InnerNet;

namespace ProtoDump
{
    /// <summary>
    /// ★ PlayerInfo 创建观测器 —— 本项目最后一个未解问题。
    ///
    /// 现状：外置客户端能加入、能生成自己的角色（Color 测试已证明协议正确），
    /// 但房主的 GameData 里始终没有它的玩家条目，导致：
    ///   · 玩家计数 1/15
    ///   · 名字/颜色显示为绿色 ??? 失败占位符
    ///   · 几秒后被房主清理掉线
    ///
    /// AddPlayer 是创建 PlayerInfo 的唯一入口，所以 hook 它就能直接看到：
    ///   · 房主到底有没有尝试为机器人创建条目
    ///   · 如果有，参数是什么（pc 是否为 null、client 是谁）
    ///
    /// AddPlayer(PlayerControl pc, ClientData client) —— 两个参数都有含义：
    /// pc 为空说明房主没找到对应角色，client 为空说明房主不认识这个客户端。
    /// </summary>
    internal static class PlayerInfoWatch
    {
        [HarmonyPatch(typeof(GameData), nameof(GameData.AddPlayer))]
        internal static class Patch_AddPlayer
        {
            [HarmonyPrefix]
            private static void Prefix(PlayerControl pc, ClientData client)
            {
                try
                {
                    Plugin.L.LogWarning(
                        $"[GDP] ★★★ AddPlayer 被调用！ " +
                        $"pc={(pc == null ? "null" : $"pid={pc.PlayerId} owner={pc.OwnerId}")}  " +
                        $"client={(client == null ? "null" : $"id={client.Id} name='{client.PlayerName}'")}");

                    var gd = GameData.Instance;
                    if (gd != null)
                        Plugin.L.LogWarning($"[GDP]     调用前 AllPlayers.Count={gd.AllPlayers?.Count}");

                    // ★ 把房主创建的 PlayerControl 细节打出来 —— netId 是 SetName RPC 的目标
                    if (pc != null)
                    {
                        try
                        {
                            Plugin.L.LogWarning(
                                $"[GDP]     pc.NetId={pc.NetId}  pc.PlayerId={pc.PlayerId}  pc.OwnerId={pc.OwnerId}  " +
                                $"pc.name='{pc.name}'  pc.Data={(pc.Data == null ? "null" : $"pid={pc.Data.PlayerId} name='{pc.Data.PlayerName}'")}");
                        }
                        catch (Exception e) { Plugin.L.LogWarning($"[GDP]     pc 字段读取失败: {e.Message}"); }
                    }
                }
                catch (Exception e) { Plugin.L.LogError($"[GDP] AddPlayer hook 失败: {e.Message}"); }
            }
        }


        [HarmonyPatch(typeof(GameData), nameof(GameData.AddPlayer))]
        internal static class Patch_AddPlayerPost
        {
            [HarmonyPostfix]
            private static void Postfix(PlayerControl pc, ClientData client, NetworkedPlayerInfo __result)
            {
                try
                {
                    Plugin.L.LogWarning(
                        $"[GDP]     AddPlayer 返回: " +
                        $"{(__result == null ? "null" : $"pid={__result.PlayerId} name='{__result.PlayerName}'")}");
                    var gd = GameData.Instance;
                    if (gd?.AllPlayers != null)
                        Plugin.L.LogWarning($"[GDP]     调用后 AllPlayers.Count={gd.AllPlayers.Count}");
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(GameData), nameof(GameData.AddPlayerInfo))]
        internal static class Patch_AddPlayerInfo
        {
            [HarmonyPrefix]
            private static void Prefix(NetworkedPlayerInfo info)
            {
                try
                {
                    Plugin.L.LogWarning(
                        $"[GDP] ★ AddPlayerInfo: " +
                        $"{(info == null ? "null" : $"playerId={info.PlayerId} name='{info.PlayerName}'")}");
                }
                catch { }
            }
        }


        // ═══════════════════════════════════════════════════════════
        // ★★ 关键：房主等待「客户端报告场景」的协程 ★★
        //
        // AmongUsClient.CoOnPlayerChangedScene(ClientData client, string currentScene)
        //
        // 推断：房主的「为加入者生成角色并建 PlayerInfo」流程，
        // 会等这个协程确认客户端已进入游戏场景才开始。
        // 我们的 SceneChange 包被收到了（tag=0x06），但有没有进到这个协程
        // 决定了整条链路走不走得通 —— 所以必须看这一行。
        // ═══════════════════════════════════════════════════════════
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.CoOnPlayerChangedScene))]
        internal static class Patch_OnPlayerChangedScene
        {
            [HarmonyPrefix]
            private static void Prefix(ClientData client, string currentScene)
            {
                try
                {
                    Plugin.L.LogWarning(
                        $"[GDP] ★★ CoOnPlayerChangedScene: " +
                        $"client={(client == null ? "null" : $"id={client.Id} name='{client.PlayerName}'")}  " +
                        $"scene='{currentScene}'");
                }
                catch (Exception e) { Plugin.L.LogError($"[GDP] scene hook 失败: {e.Message}"); }
            }
        }

        /// <summary>房主的 PlayerPrefab 是否就绪 —— 没就绪的话生成流程会一直等</summary>
        private static float _t2;
        internal static void TickPrefab(float dt)
        {
            _t2 += dt;
            if (_t2 < 5f) return;
            _t2 = 0f;
            try
            {
                var c = AmongUsClient.Instance;
                if (c == null) return;
                var pf = c.PlayerPrefab;
                Plugin.L.LogInfo($"[GDP] PlayerPrefab={(pf == null ? "null（未就绪！）" : "已就绪")}  GameState={c.GameState}");
            }
            catch { }
        }


        // ═══════════════════════════════════════════════════════════
        // ★★★ 名字的真正入口 ★★★
        //
        // NetworkedPlayerInfo.UpdateName(string playerName, ClientData client)
        //
        // 这是给玩家条目设名字的唯一地方，而且**同时拿到名字和 ClientData** ——
        // 房主手里的 ClientData 明明有 name='BotTest'，可建出来的 PlayerInfo 却是空名。
        // hook 它就能立刻看清：是没被调用，还是被调用时传了空字符串。
        // ═══════════════════════════════════════════════════════════
        [HarmonyPatch(typeof(NetworkedPlayerInfo), nameof(NetworkedPlayerInfo.UpdateName))]
        internal static class Patch_UpdateName
        {
            [HarmonyPrefix]
            private static void Prefix(string playerName, ClientData client)
            {
                try
                {
                    Plugin.L.LogWarning(
                        $"[GDP] ★★ UpdateName: playerName='{playerName}'  " +
                        $"client={(client == null ? "null" : $"id={client.Id} PlayerName='{client.PlayerName}'")}");
                }
                catch (Exception e) { Plugin.L.LogError($"[GDP] UpdateName hook 失败: {e.Message}"); }
            }
        }

        // ⚠️ 这里原来挂了一个 NetworkedPlayerInfo.PlayerName 的 setter 观测，
        //    用途是排查「名字为什么设不上」—— 那个问题早已解决。
        //    但它是个**极高频**的钩子（游戏每帧都会设这个名字），
        //    实测把日志刷到了 8000+ 行同一句，后果有二：
        //      ① 自检读到的 GameState 是 8000 行之前的旧值 → 误判「不在大厅」
        //      ② 每秒几千行日志写入，本身就在拖慢游戏
        //    已永久移除。需要时再临时加，用完就摘。


        // ═══════════════════════════════════════════════════════════
        // ★ NetId 分配观测 —— 解释「RPC 打到 netId=8 却无效」
        //
        // InnerNetClient.FindObjectByNetId<T>(netId) 是 RPC 分发的查表入口，
        // 说明房主收到 RPC 后是**按 netId 找对象**的：找不到就丢弃。
        //
        // 而房主为我们建的 PlayerControl 是 pc.NetId=0（疑似 InvalidNetId），
        // 我们的 RPC 却打在自建对象的 netId=8 上 —— 两者对不上。
        //
        // 这里要确认的是：房主到底有没有给我们的角色分配过 netId。
        // ⚠️ setter 可能被高频调用，所以只记 PlayerControl，且总量封顶。
        // ═══════════════════════════════════════════════════════════
        private static int _netIdLogged;

        [HarmonyPatch(typeof(InnerNetObject), nameof(InnerNetObject.NetId), MethodType.Setter)]
        internal static class Patch_NetIdSetter
        {
            // ⚠️ 不能声明原方法的参数 —— set_NetId 的参数在 interop 里**没有名字**，
            //    Harmony 按名字绑定会报 Parameter "value" not found。
            //    改用 Postfix + __instance：此时读到的就是**赋值后**的新值。
            [HarmonyPostfix]
            private static void Postfix(InnerNetObject __instance)
            {
                try
                {
                    if (_netIdLogged >= 30) return;
                    if (__instance == null) return;

                    // 只关心玩家对象
                    PlayerControl pc = null;
                    try { pc = __instance.TryCast<PlayerControl>(); } catch { }
                    if (pc == null) return;

                    _netIdLogged++;
                    Plugin.L.LogWarning(
                        $"[GDP] ★ NetId 赋值: PlayerControl netId={__instance.NetId} " +
                        $"playerId={pc.PlayerId} owner={pc.OwnerId} (NetIdCnt={AmongUsClient.Instance?.NetIdCnt})");
                }
                catch { }
            }
        }

        /// <summary>启动时打印 InvalidNetId 的值 —— 判断 pc.NetId=0 是不是「未分配」</summary>
        internal static void DumpInvalidNetId()
        {
            try
            {
                Plugin.L.LogInfo($"[GDP] InvalidNetId = {InnerNet.NetId.InvalidNetId}");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[GDP] InvalidNetId 读取失败: {e.Message}"); }
        }


        // ═══════════════════════════════════════════════════════════
        // ★★ 房主在等什么 —— 定位「Timeout while waiting for other player data」
        //
        // InnerNetClient.WaitWithTimeout(Func<bool> success, string errorMessage, int durationSeconds)
        //
        // 这是一个「等条件成立、超时就以 errorMessage 报错」的协程。
        // 我们观测到的踢人理由正是它传的字符串之一 ——
        // 所以只要看到**哪个 errorMessage 被启动过**，就知道房主卡在哪个条件上。
        //
        // success 是个 Func，读不到内容，但 errorMessage 足以定位。
        // ═══════════════════════════════════════════════════════════
        [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.WaitWithTimeout))]
        internal static class Patch_WaitWithTimeout
        {
            [HarmonyPrefix]
            private static void Prefix(Il2CppSystem.Func<bool> success, string errorMessage, int durationSeconds)
            {
                try
                {
                    Plugin.L.LogWarning(
                        $"[WAIT] ★ 房主开始等待: '{errorMessage}'  限时 {durationSeconds} 秒");
                }
                catch (Exception e) { Plugin.L.LogError($"[WAIT] hook 失败: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.WaitForConnectionOrFail))]
        internal static class Patch_WaitForConnectionOrFail
        {
            [HarmonyPrefix]
            private static void Prefix() =>
                Plugin.L.LogWarning("[WAIT] ★★ WaitForConnectionOrFail 被调用");
        }

        /// <summary>定期汇报玩家表规模 —— 看机器人有没有被算进去</summary>
        private static float _t;
        internal static void Tick(float dt)
        {
            _t += dt;
            if (_t < 5f) return;
            _t = 0f;
            try
            {
                var gd = GameData.Instance;
                if (gd == null || gd.AllPlayers == null) return;
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < gd.AllPlayers.Count; i++)
                {
                    var p = gd.AllPlayers[i];
                    if (p == null) continue;
                    sb.Append($" [pid={p.PlayerId} '{p.PlayerName}'");
                    try { sb.Append($" owner={p.OwnerId}"); } catch { }
                    // ★ 职业 —— 用来回答「哪个机器人是内鬼」
                    try
                    {
                        int rt = (int)p.RoleType;
                        // ★ 用游戏自己的枚举名（权威），并把**数字**一起打出来 ——
                        //   数字是关键：有了它才能知道 8/9/10… 分别是什么职业
                        //   （网上文档是旧版本，新职业的编号查不到）。
                        string realName = "?";
                        try { realName = p.RoleType.ToString(); } catch { }
                        string rn = rt switch
                        {
                            0 => "船员", 1 => "★内鬼★", 2 => "科学家", 3 => "工程师",
                            4 => "守护天使", 5 => "变形者", 6 => "船员(鬼)", 7 => "内鬼(鬼)",
                            8 => "大嗓门?", 9 => "侦察员?", 10 => "侦探?",
                            11 => "法宫?", 12 => "幻象师?", 13 => "毒蛇?", 14 => "网红?",
                            _ => $"未知{rt}"
                        };
                        sb.Append($" 职业={rt}({realName}/{rn})");
                    }
                    catch { }
                    try { if (p.IsDead) sb.Append(" ☠已死"); } catch { }
                    sb.Append(']');
                }
                Plugin.L.LogInfo($"[GDP] 玩家表({gd.AllPlayers.Count}):{sb}");
                // ★ 单独打一行「谁是内鬼」，便于一眼找到
                var imp = new System.Text.StringBuilder();
                for (int i = 0; i < gd.AllPlayers.Count; i++)
                {
                    var q = gd.AllPlayers[i];
                    if (q == null) continue;
                    try { if ((int)q.RoleType == 1 || (int)q.RoleType == 7) imp.Append($" pid={q.PlayerId} '{q.PlayerName}'"); }
                    catch { }
                }
                if (imp.Length > 0) Plugin.L.LogWarning($"[GDP] ★★★ 内鬼:{imp}");
            }
            catch { }
        }
    }
}
