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
        // ★★★ 输出当前 GameId ★★★
        //
        // 人机一直把 gameId 写死成 32，但**本地游戏的 gameId 不是固定的** ——
        // 每次开房可能不同。写错时服务端回 IncorrectGame，反复重试还会把房主弄掉线
        // （实测：每次都是 5/14 或 13/14 就位后房主连接中断）。
        //
        // 这里把真实值报出来，人机启动时读它即可。
        // ═══════════════════════════════════════════════════════════
        private static int _lastGameId = int.MinValue;


        // ═══════════════════════════════════════════════════════════
        // ★★★ 导出碰撞体的**真实形状**（多边形），而不是包围盒 ★★★
        //
        // 之前导出 collider.bounds（轴对齐矩形），把不规则形状补成了直角 ——
        // 用户画的示意图说得很清楚：
        //     绿色 = 正常人可走的区域（不规则）
        //     红色 = 交界处，有墙，内部可走（灵魂能穿）
        //     紫色 = 最外层硬墙（灵魂也穿不过）
        // 而 AreaCollider(layer 2) 很可能就是绿色区的精确轮廓，
        // 用矩形去近似它 → 多出来的直角部分正好是红区 → 表现为「穿墙后在墙内活动」。
        //
        // 输出格式：
        //   [POLY] BEGIN <总数>
        //   <层> <点数> x1 y1 x2 y2 ...        ← 世界坐标
        //   [POLY] END
        // ═══════════════════════════════════════════════════════════
        private static void DumpPolygons()
        {
            try
            {
                var cols = UnityEngine.Object.FindObjectsOfType<Collider2D>();
                if (cols == null) return;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[POLY] BEGIN {cols.Length}");
                int n = 0;
                foreach (var c in cols)
                {
                    if (c == null) continue;
                    try
                    {
                        var pts = new System.Collections.Generic.List<Vector2>();
                        var poly = c.TryCast<PolygonCollider2D>();
                        var box = c.TryCast<BoxCollider2D>();
                        var cir = c.TryCast<CircleCollider2D>();
                        if (poly != null)
                        {
                            for (int pi = 0; pi < poly.pathCount; pi++)
                                foreach (var v in poly.GetPath(pi)) pts.Add(c.transform.TransformPoint(v));
                        }
                        else if (box != null)
                        {
                            var sz = box.size * 0.5f; var o = box.offset;
                            pts.Add(c.transform.TransformPoint(new Vector2(o.x - sz.x, o.y - sz.y)));
                            pts.Add(c.transform.TransformPoint(new Vector2(o.x + sz.x, o.y - sz.y)));
                            pts.Add(c.transform.TransformPoint(new Vector2(o.x + sz.x, o.y + sz.y)));
                            pts.Add(c.transform.TransformPoint(new Vector2(o.x - sz.x, o.y + sz.y)));
                        }
                        else if (cir != null)
                        {
                            for (int k = 0; k < 12; k++)
                            {
                                float a = k * Mathf.PI * 2f / 12f;
                                pts.Add(c.transform.TransformPoint(
                                    cir.offset + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * cir.radius));
                            }
                        }
                        else
                        {
                            var b = c.bounds;
                            pts.Add(new Vector2(b.min.x, b.min.y)); pts.Add(new Vector2(b.max.x, b.min.y));
                            pts.Add(new Vector2(b.max.x, b.max.y)); pts.Add(new Vector2(b.min.x, b.max.y));
                        }
                        if (pts.Count < 3) continue;
                        n++;
                        var line = new System.Text.StringBuilder();
                        line.Append(c.gameObject.layer).Append(' ').Append(pts.Count);
                        foreach (var v in pts) line.Append(' ').Append(v.x.ToString("F2")).Append(' ').Append(v.y.ToString("F2"));
                        sb.AppendLine(line.ToString());
                    }
                    catch { }
                }
                sb.AppendLine("[POLY] END");
                Plugin.L.LogWarning(sb.ToString());
                Plugin.L.LogWarning($"[POLY] ✅ 形状已导出：{n} 个（多边形/矩形/圆形都已转成顶点）");
            }
            catch (Exception e) { Plugin.L.LogError($"[POLY] 导出失败: {e.Message}"); }
        }


        // ═══════════════════════════════════════════════════════════
        // ★★★ 反射导出游戏自己的枚举 ★★★
        //
        // 用户在「职业设置」里看到 11 个职业（科学家/守护天使/工程师/大嗓门/
        // 侦察员/侦探/法宫/网红 + 变形者/幻象师/毒蛇），
        // 而网上协议文档是旧版本的 —— RPC 号与职业枚举都查不到。
        //
        // 与其猜，不如让游戏自己报：插件跑在游戏进程里，
        // 直接反射 il2cpp 的枚举即可，这是**权威且必然匹配本版本**的答案。
        // ═══════════════════════════════════════════════════════════
        private static bool _enumsDumped;

        internal static void DumpEnumsOnce()
        {
            if (_enumsDumped) return;
            try
            {
                var asm = typeof(PlayerControl).Assembly;
                // ★ RpcCalls / SpawnType 等是**嵌套类型**（如 InnerNetObject+RpcCalls），
                //   顶层 GetType 找不到 —— 实测第一次只导出了 TaskTypes 就是这个原因。
                //   这里把常见宿主类型都试一遍。
                var hosts = new[] { "", "InnerNetObject+", "InnerNetClient+", "PlayerControl+",
                                    "ShipStatus+", "GameData+", "AmongUsClient+" };
                // ★ 名字全部来自 IL2CPP 元数据里的实测结果（不是猜的）：
                //   职业枚举真名是 RoleTypes（**复数**）——之前写 RoleType 所以找不到。
                foreach (var name in new[] { "RoleTypes", "RoleType", "RpcCalls", "SystemTypes",
                                             "SpawnType", "TaskTypes", "DeathReason",
                                             "PlayerOutfitType", "DisconnectReasons",
                                             "GameStates", "QuickChatPhraseType" })
                {
                    try
                    {
                        Type t = null;
                        foreach (var h in hosts)
                        {
                            t = asm.GetType(h + name);
                            if (t != null) break;
                        }
                        if (t == null)
                        {
                            // 最后兜底：扫全部类型（il2cpp 下 GetTypes 可能抛，所以放最后且单独 try）
                            try
                            {
                                foreach (var tt in asm.GetTypes())
                                    if (tt.Name == name) { t = tt; break; }
                            }
                            catch { }
                        }
                        if (t == null || !t.IsEnum) continue;
                        var sb = new System.Text.StringBuilder();
                        sb.Append($"[ENUM] {name}: ");
                        foreach (var v in Enum.GetValues(t))
                        {
                            string cn = "";
                            if (name.StartsWith("RoleType"))
                                cn = ((int)v) switch
                                {
                                    0 => "(船员)", 1 => "(内鬼)", 2 => "(科学家)", 3 => "(工程师)",
                                    4 => "(守护天使)", 5 => "(变形者)", 6 => "(船员鬼)", 7 => "(内鬼鬼)",
                                    8 => "(大嗓门?)", 9 => "(侦察员?)", 10 => "(侦探?)", 11 => "(法宫?)",
                                    12 => "(幻象师?)", 13 => "(毒蛇?)", 14 => "(网红?)", _ => ""
                                };
                            sb.Append($"{(int)v}={v}{cn} ");
                        }
                        Plugin.L.LogWarning(sb.ToString());
                    }
                    catch { }
                }
                // ★ 兜底：把程序集里**所有枚举类型的名字**列出来。
                //   与其猜 RoleType / RpcCalls 叫什么（这个版本可能叫 RoleTypes 等），
                //   不如让游戏把清单报出来，我们照着找。
                try
                {
                    var names = new System.Collections.Generic.List<string>();
                    foreach (var tt in asm.GetTypes())
                    {
                        try { if (tt.IsEnum) names.Add(tt.Name); } catch { }
                    }
                    names.Sort();
                    Plugin.L.LogWarning($"[ENUM-LIST] 共 {names.Count} 个枚举: {string.Join(", ", names)}");
                }
                catch (Exception e2) { Plugin.L.LogWarning($"[ENUM-LIST] 扫描失败: {e2.Message}"); }

                _enumsDumped = true;
                Plugin.L.LogWarning("[ENUM] ✅ 枚举导出完成");
            }
            catch (Exception e) { Plugin.L.LogError($"[ENUM] 失败: {e.Message}"); }
        }

        internal static void TickIdAndGrid()
        {
            TickGameId();
            TickGrid();
            DumpEnumsOnce();
        }

        internal static void TickGameId()
        {
            try
            {
                var c = AmongUsClient.Instance;
                if (c == null) return;
                int gid = c.GameId;
                if (gid == _lastGameId) return;
                _lastGameId = gid;
                Plugin.L.LogWarning($"[GAME] ★ GameId = {gid}");
            }
            catch { }
        }

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
            // ★★★ 不能用场景名判断！★★★
            //
            // 实测：大厅和游戏地图的 SceneManager 场景名**都是 "OnlineGame"** ——
            // 游戏只是往同一个场景里装不同内容。
            // 所以「场景名变了才重导」这条判据永远不成立，
            // 进了地图之后人机用的还是大厅的碰撞几何（表现为在地图里穿墙）。
            //
            // 改用**阶段**判断：大厅有 LobbyBehaviour，地图有 ShipStatus。
            string phase;
            try
            {
                if (ShipStatus.Instance != null) phase = "SHIP";
                else if (LobbyBehaviour.Instance != null) phase = "LOBBY";
                else return;
            }
            catch { return; }
            if (_gridDumpedFor == phase) return;

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
                DumpPolygons();          // ★ 同时导出真实形状
                Plugin.L.LogWarning(sb.ToString());
                _gridDumpedFor = phase;
                Plugin.L.LogWarning($"[GRID] 阶段 = {phase}");
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
                    sb2.AppendLine($"[COL] BEGIN {cols?.Length ?? 0} SCENE {phase}");
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
