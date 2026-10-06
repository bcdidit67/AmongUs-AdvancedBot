using System;
using UnityEngine;
using BepInEx.Logging;
using HarmonyLib;
using Hazel;
using InnerNet;

namespace ProtoDump
{
    /// <summary>
    /// 服务端入站包观测器。
    ///
    /// 目的：外置 bot 客户端发 JoinGame 后，**传输层被确认、应用层却毫无反应**。
    /// 本插件 hook 住游戏自己的包处理链路，直接看服务端内部发生了什么：
    ///   HandleMessage      → 每个入站包的 tag（看到底收没收到、收到了什么）
    ///   OnPlayerJoined     → 有没有玩家被真正加入
    ///   HandleDisconnect   → 断开的真实原因
    ///   OnGameJoined       → 加入流程走到哪一步
    ///
    /// 注意：为了避免把日志淹没（游戏每秒几十个包），只记录**非 Ping / 非 GameData**
    /// 的包，以及所有与「加入」相关的事件。
    /// </summary>
    internal static class PacketWatch
    {
        private static int _count;
        private static float _lastLog;
        private static int _subLogged;

        internal static string TagName(byte t) => t switch
        {
            0x00 => "HostGame", 0x01 => "JoinGame", 0x02 => "StartGame",
            0x03 => "RemoveGame", 0x04 => "RemovePlayer", 0x05 => "GameData",
            0x06 => "GameDataTo", 0x07 => "JoinedGame", 0x08 => "EndGame",
            0x0a => "AlterGame", 0x0b => "KickPlayer", 0x0c => "WaitForHost",
            0x0d => "Redirect", 0x0e => "ReselectServer", 0x10 => "GetGameListV2",
            0x11 => "ReportPlayer", 0x12 => "QuickMatch", 0x13 => "QuickMatchHost",
            0x1a => "PackedGameDataTo", 0xff => "ServerDebugAlert", _ => "?"
        };


        // ═══════════════════════════════════════════════════════════
        // ★★ 抄游戏自己发的 Hello ★★
        //
        // 我们的外置客户端发的 Hello 只换来一个 Ack，服务端从不回 Hello 挑战，
        // 之后应用层就再也不派发我们的任何消息（HandleMessage 一次都没触发）。
        //
        // 与其继续猜 Hello 的字段（Hazel 版本字节、字段顺序…），
        // 不如直接把游戏自己发的那串字节抄下来 —— 那是服务端**已经接受过**的格式。
        // ═══════════════════════════════════════════════════════════
        // ═══════════════════════════════════════════════════════════
        // ★★★ 可行走网格 —— 让人机不穿墙 ★★★
        //
        // 人机是**外置进程**，没有游戏的物理引擎，算不出哪里是墙。
        // 但本插件跑在游戏进程里，可以做重叠检测 ——
        // 所以由插件**预计算一张网格**，人机读进去自己做碰撞。
        //
        // 墙的层：Constants.ShipOnlyMask = LayerMask.GetMask("Ship")
        // 判据：  Physics2D.OverlapCircle(世界坐标, 玩家半径, ShipOnlyMask)
        //
        // 坐标约定（与 BotClient 严格一致）：
        //   原点 (0,0) 为世界原点，格子边长 CELL
        //   格 (i,j) 的世界坐标 = ( -GW*CELL/2 + i*CELL + CELL/2 ,
        //                          -GH*CELL/2 + j*CELL + CELL/2 )
        //   输出：'1'=可走  '0'=墙
        // ═══════════════════════════════════════════════════════════
        internal const int GW = 96;
        internal const int GH = 96;
        internal const float CELL = 0.5f;
        private const float PlayerRadius = 0.30f;   // 略小于真实半径，避免过度保守
        private static string _gridDumpedFor;   // ★ 改成「按场景」记录，而不是只导一次

        internal static void TickGrid()
        {
            // ★ 场景一变就重新导出 —— 否则进地图后用的还是大厅的碰撞几何
            //   （之前只导一次，所以人机在地图里完全没有墙体约束）
            string scene = "";
            try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; } catch { }
            if (string.IsNullOrEmpty(scene)) return;
            if (_gridDumpedFor == scene) return;

            try
            {
                // ⚠️ 只等 LobbyBehaviour —— **不能等 ShipStatus**：
                //    ShipStatus 是真正的游戏地图，开局后才加载；
                //    大厅（LobbyBehaviour）是独立场景，里面根本没有 ShipStatus。
                //    之前多写了这一句，导致网格在大厅阶段永远生成不出来。
                if (LobbyBehaviour.Instance == null && ShipStatus.Instance == null) return;

                // ★ 先诊断：看看大厅里的碰撞体到底分布在哪些层
                //   （用 ShipOnlyMask 采出来 9027/9216 都是可走 —— 说明墙不在 "Ship" 层）
                try
                {
                    var all = Physics2D.OverlapCircleAll(Vector2.zero, 40f);
                    var byLayer = new System.Collections.Generic.Dictionary<int, int>();
                    int total = 0;
                    if (all != null)
                        foreach (var c in all)
                        {
                            if (c == null) continue;
                            total++;
                            int L = c.gameObject.layer;
                            byLayer[L] = byLayer.TryGetValue(L, out var v) ? v + 1 : 1;
                        }
                    var sbL = new System.Text.StringBuilder();
                    sbL.Append($"[GRID] 半径40内共 {total} 个碰撞体，按层分布: ");
                    foreach (var kv in byLayer) sbL.Append($"layer{kv.Key}={kv.Value} ");
                    Plugin.L.LogWarning(sbL.ToString());
                    Plugin.L.LogWarning($"[GRID] ShipOnlyMask={Constants.ShipOnlyMask}  AllLayers={Physics2D.AllLayers}");
                }
                catch (Exception e3) { Plugin.L.LogWarning($"[GRID] 层诊断失败: {e3.Message}"); }

                // ★ 用所有层采样（老 Mod 的 Blocked() 也是不设掩码的）
                //   注意：OverlapCircle 不返回 trigger，所以开关/按钮不会误判成墙。
                int shipMask = Physics2D.AllLayers;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[GRID] BEGIN {GW} {GH} {CELL}");
                for (int j = 0; j < GH; j++)
                {
                    var row = new char[GW];
                    for (int i = 0; i < GW; i++)
                    {
                        float x = -(GW * CELL) / 2f + i * CELL + CELL / 2f;
                        float y = -(GH * CELL) / 2f + j * CELL + CELL / 2f;
                        bool blocked;
                        try { blocked = Physics2D.OverlapCircle(new Vector2(x, y), PlayerRadius, shipMask) != null; }
                        catch { blocked = true; }
                        row[i] = blocked ? '0' : '1';
                    }
                    sb.AppendLine(new string(row));
                }
                sb.AppendLine("[GRID] END");
                Plugin.L.LogWarning(sb.ToString());
                _gridDumpedFor = scene;
                Plugin.L.LogWarning($"[GRID] 场景 = {scene}");
                Plugin.L.LogWarning($"[GRID] ✅ 可行走网格已输出（{GW}x{GH}，格子 {CELL}，半径 {PlayerRadius}）");

                // ═══════════════════════════════════════════════════════
                // ★★★ 碰撞体真实几何导出 ★★★
                //
                // 采样网格会漏 —— 采样点若正好落在薄墙两侧的空隙里就探不到，
                // 结果机器人一路走出大厅（实测跑到 x=22.8）。
                //
                // 改成直接把场景里每个 Collider2D 的**世界包围盒**导出，
                // 一个都不会漏。包围盒偏保守（斜墙会被放大成矩形），
                // 但大厅的碰撞体基本都是矩形，够用。
                // ═══════════════════════════════════════════════════════
                try
                {
                    var cols = UnityEngine.Object.FindObjectsOfType<Collider2D>();
                    var sb2 = new System.Text.StringBuilder();
                    int cnt = 0;
                    sb2.AppendLine($"[COL] BEGIN {cols?.Length ?? 0} SCENE {scene}");
                    if (cols != null)
                        foreach (var c in cols)
                        {
                            if (c == null) continue;
                            var b = c.bounds;
                            // 跳过小得没意义的（按钮、图标等）
                            if (b.size.x < 0.05f || b.size.y < 0.05f) continue;
                            cnt++;
                            sb2.AppendLine($"{c.gameObject.layer} {b.min.x:F3} {b.min.y:F3} {b.max.x:F3} {b.max.y:F3} {c.GetType().Name} {c.gameObject.name.Replace(' ', '_')}");
                        }
                    sb2.AppendLine("[COL] END");
                    Plugin.L.LogWarning(sb2.ToString());
                    Plugin.L.LogWarning($"[COL] ✅ 碰撞体几何已导出：{cnt} 个（跳过尺寸过小的）");
                }
                catch (Exception e4) { Plugin.L.LogError($"[COL] 导出失败: {e4.Message}"); }
            }
            catch (Exception e) { Plugin.L.LogError($"[GRID] 生成失败: {e.Message}"); }
        }


        [HarmonyPatch(typeof(Hazel.Udp.UdpConnection), nameof(Hazel.Udp.UdpConnection.SendHello))]
        internal static class Patch_SendHello
        {
            [HarmonyPrefix]
            private static void Prefix(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte> bytes)
            {
                try
                {
                    if (bytes == null) { Plugin.L.LogWarning("[HELLO] SendHello bytes=null"); return; }
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("X2")).Append(' ');
                    Plugin.L.LogWarning($"[HELLO] ★ 游戏发出 Hello  {bytes.Length}B: {sb.ToString().TrimEnd()}");
                }
                catch (Exception e) { Plugin.L.LogError($"[HELLO] 读取失败: {e.Message}"); }
            }
        }

                internal static string SubName(byte t) => t switch
        {
            0x01 => "Data", 0x02 => "RPC", 0x04 => "★Spawn", 0x05 => "Despawn",
            0x06 => "SceneChange", 0x07 => "Ready", 0x08 => "ChangeSettings",
            0xcd => "ClientInfo", _ => "?"
        };


        // ═══════════════════════════════════════════════════════════
        // ★★★ 房主到底在等什么 ★★★
        //
        // 实测房主手里的 ClientData：
        //   IsReady = False    ← 等这个
        //   InScene = False    ← 和这个
        //   Character = null   ← 所以角色一直没生成
        //
        // 「Timeout while waiting for other player data」就是在等这两个标志位。
        // hook 它们的 setter 就能看到：谁在设、什么时候设、设成什么 ——
        // 从而确定我们该补哪个包。
        //
        // ⚠️ il2cpp 的属性 setter 参数常常没有名字（Harmony 按名绑定会失败），
        //    所以只用 __instance 的 Postfix。
        // ═══════════════════════════════════════════════════════════
        [HarmonyPatch(typeof(ClientData), nameof(ClientData.InScene), MethodType.Setter)]
        internal static class Patch_InScene
        {
            [HarmonyPostfix]
            private static void Postfix(ClientData __instance) =>
                Plugin.L.LogWarning($"[CD] ★ InScene 被设置 → {__instance?.InScene} (client id={__instance?.Id})");
        }

        [HarmonyPatch(typeof(ClientData), nameof(ClientData.IsReady), MethodType.Setter)]
        internal static class Patch_IsReady
        {
            [HarmonyPostfix]
            private static void Postfix(ClientData __instance) =>
                Plugin.L.LogWarning($"[CD] ★ IsReady 被设置 → {__instance?.IsReady} (client id={__instance?.Id})");
        }

        [HarmonyPatch(typeof(ClientData), nameof(ClientData.Character), MethodType.Setter)]
        internal static class Patch_Character
        {
            [HarmonyPostfix]
            private static void Postfix(ClientData __instance) =>
                Plugin.L.LogWarning($"[CD] ★ Character 被设置 → {( __instance?.Character == null ? "null" : $"pid={__instance.Character.PlayerId}")} (client id={__instance?.Id})");
        }

        [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.HandleMessage))]
        internal static class Patch_HandleMessage
        {
            [HarmonyPrefix]
            private static void Prefix(Hazel.MessageReader reader, Hazel.SendOption sendOption)
            {
                try
                {
                    if (reader == null) return;
                    byte tag = reader.Tag;

                    // GameData 太频繁，只统计；但**把子消息 tag 也记下来**
                    // —— Spawn(0x04) 就藏在 GameData 里，之前完全看不见。
                    if (tag == 0x05 || tag == 0x06)
                    {
                        _count++;
                        if (_subLogged < 40 && reader.BytesRemaining >= 8)
                        {
                            _subLogged++;
                            string subs = "";
                            try
                            {
                                var r = reader;
                                int pos = r.Offset;
                                var buf = r.Buffer;
                                int end = Math.Min(buf.Length, pos + Math.Min(r.BytesRemaining, 48));
                                for (int i = pos + 4; i + 2 <= end; )
                                {
                                    int slen = buf[i] | (buf[i+1] << 8);
                                    if (i + 2 >= end) break;
                                    byte stag = buf[i+2];
                                    subs += $" tag=0x{stag:X2}({SubName(stag)})[{slen}]";
                                    i += 2 + 1 + slen;
                                    if (slen == 0) break;
                                }
                            }
                            catch { }
                            if (subs.Length > 0)
                                Plugin.L.LogWarning($"[PKT] ← GameData 子消息:{subs}");
                        }
                        return;
                    }

                    Plugin.L.LogInfo(
                        $"[PKT] ← tag=0x{tag:X2} ({TagName(tag)})  opt={sendOption}  " +
                        $"剩余={reader.BytesRemaining}B");
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.OnGameJoined))]
        internal static class Patch_OnGameJoined
        {
            [HarmonyPostfix]
            private static void Postfix(string gameIdString) =>
                Plugin.L.LogWarning($"[PKT] ★ OnGameJoined gameIdString='{gameIdString}'");
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerJoined))]
        internal static class Patch_OnPlayerJoined
        {
            [HarmonyPostfix]
            private static void Postfix(ClientData data)
            {
                try
                {
                    var c = AmongUsClient.Instance;
                    Plugin.L.LogWarning(
                        $"[PKT] ★ OnPlayerJoined: id={data?.Id} name='{data?.PlayerName}' " +
                        $"(本机 ClientId={c?.ClientId} HostId={c?.HostId})");

                    // ★★★ 把整个 ClientData 摊开
                    //   「Timeout while waiting for other player data」的字面意思就是
                    //   「等这个玩家的数据就绪」—— 那缺的一定是某个字段。
                    //   （路线 A 当年也填过这些字段但无效，因为那是伪造的 ClientData；
                    //     这里的 ClientData 是房主依据我们真实的 Hello/JoinGame 建出来的，
                    //     所以能看到**真实缺口**。）
                    if (data != null)
                    {
                        var sb2 = new System.Text.StringBuilder();
                        sb2.AppendLine("[CD] ★★★ ClientData 全字段:");
                        sb2.AppendLine($"      Id             = {data.Id}");
                        sb2.AppendLine($"      PlayerName     = '{data.PlayerName}'");
                        sb2.AppendLine($"      IsReady        = {data.IsReady}");
                        sb2.AppendLine($"      InScene        = {data.InScene}");
                        sb2.AppendLine($"      IsBeingCreated = {data.IsBeingCreated}");
                        sb2.AppendLine($"      HasBeenReported= {data.HasBeenReported}");
                        sb2.AppendLine($"      PlayerLevel    = {data.PlayerLevel}");
                        sb2.AppendLine($"      ColorId        = {data.ColorId}");
                        try { sb2.AppendLine($"      Character      = {(data.Character == null ? "null ← ★缺" : $"pid={data.Character.PlayerId} owner={data.Character.OwnerId}")}"); }
                        catch { sb2.AppendLine("      Character      = <读取失败>"); }
                        try { sb2.AppendLine($"      PlatformData   = {(data.PlatformData == null ? "null ← ★缺" : "有")}"); }
                        catch { sb2.AppendLine("      PlatformData   = <读取失败>"); }
                        try { sb2.AppendLine($"      ProductUserId  = '{(string.IsNullOrEmpty(data.ProductUserId) ? "空 ← ★缺" : data.ProductUserId)}'"); }
                        catch { sb2.AppendLine("      ProductUserId  = <读取失败>"); }
                        try { sb2.AppendLine($"      FriendCode     = '{(string.IsNullOrEmpty(data.FriendCode) ? "空 ← ★缺" : data.FriendCode)}'"); }
                        catch { sb2.AppendLine("      FriendCode     = <读取失败>"); }
                        Plugin.L.LogWarning(sb2.ToString());
                    }
                }
                catch (Exception e) { Plugin.L.LogError($"[PKT] OnPlayerJoined 读取失败: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.HandleDisconnect))]
        internal static class Patch_HandleDisconnect
        {
            [HarmonyPostfix]
            private static void Postfix(DisconnectReasons reason, string stringReason) =>
                Plugin.L.LogWarning($"[PKT] ★ HandleDisconnect reason={reason} text='{stringReason}'");
        }
    }
}
