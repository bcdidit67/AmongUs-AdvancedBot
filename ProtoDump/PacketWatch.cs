using System;
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
