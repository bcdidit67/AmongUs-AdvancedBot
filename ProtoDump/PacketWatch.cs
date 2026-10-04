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

                    // GameData 太频繁，只统计不逐条打
                    if (tag == 0x05 || tag == 0x06) { _count++; return; }

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
