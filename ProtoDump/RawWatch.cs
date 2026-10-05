using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using Hazel;
using InnerNet;

namespace ProtoDump
{
    /// <summary>
    /// 原始数据路径观测器（**带节流保护**）。
    ///
    /// ⚠️ 血泪教训：上一版在 `Connection.SetState`（每秒数百次的热路径）里直接写盘，
    /// 产生 7.8 万行日志把游戏卡死。本版遵守三条铁律：
    ///
    ///   1. **热路径只做内存计数**，绝不写盘
    ///   2. 日志先在内存里攒着，**每 2 秒批量 flush 一次**
    ///   3. 高频的 GameData 只计数、不打内容；单条包内容最多记 40 条
    ///
    /// 目的：分清「我们的 Hello 到没到游戏层」——
    ///   · InvokeDataReceived 看到了 → 包到达游戏层，问题在 AcceptConnection 准入判定
    ///   · 完全看不到           → 包没被 listener 认领，问题在 Hazel 的 endpoint 匹配
    /// </summary>
    internal static class RawWatch
    {
        private static readonly object _lock = new object();
        private static readonly StringBuilder _pending = new StringBuilder();

        private static int _totalMsgs;
        private static int _gameDataCount;
        private static int _detailLogged;
        private static float _timer;

        private const int MaxDetail = 40;   // 最多详记 40 条

        internal static void Tick(float dt)
        {
            _timer += dt;
            if (_timer < 2f) return;
            _timer = 0f;
            Flush();
        }

        private static void Flush()
        {
            string s;
            int total, gd;
            lock (_lock)
            {
                if (_pending.Length == 0) return;
                s = _pending.ToString();
                _pending.Clear();
                total = _totalMsgs;
                gd = _gameDataCount;
            }
            Plugin.L.LogInfo($"[RAW] ── 累计 {total} 条消息（其中 GameData {gd} 条）──");
            Plugin.L.LogInfo(s.TrimEnd());
        }

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
        // ★★★ 客户端出站包的咽喉 ★★★
        //
        // WriteBytesToConnection 是 UnityUdpClientConnection 上**每一个出站字节**
        // 的必经之路 —— 包括 JoinGame 之后游戏自己发的 ClientInfo 报到消息。
        //
        // ⚠️ 这是最热的路径，安全措施最严：
        //   · 只捕获「启动后的前 80 个包」（握手阶段），之后永久关闭
        //   · 零 I/O，只往内存 StringBuilder 里追加
        //   · 每 2 秒批量 flush
        // ═══════════════════════════════════════════════════════════
        private static int _outCount;
        private static bool _outDone;
        private const int MaxOut = 80;

        [HarmonyPatch(typeof(Hazel.Udp.UnityUdpClientConnection), nameof(Hazel.Udp.UnityUdpClientConnection.WriteBytesToConnection))]
        internal static class Patch_WriteBytes
        {
            [HarmonyPrefix]
            private static void Prefix(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte> bytes, int length)
            {
                try
                {
                    if (_outDone || bytes == null) return;
                    if (++_outCount > MaxOut) { _outDone = true; return; }

                    var sb = new StringBuilder();
                    int n = length > 0 && length <= bytes.Length ? length : bytes.Length;
                    for (int i = 0; i < n && i < 512; i++) sb.Append(bytes[i].ToString("X2")).Append(' ');

                    lock (_lock)
                    {
                        byte t = bytes[0];
                        _pending.AppendLine($"  [OUT] #{_outCount} {n}B type=0x{t:X2}: {sb.ToString().TrimEnd()}");
                    }
                }
                catch { }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // ★★★ 真正的 Hello 捕获点 ★★★
        //
        // 之前 hook 的是基类 UdpConnection.SendHello —— 一次都没触发。
        // 原因：游戏用的是 Unity 专用子类 UnityUdpClientConnection，
        // 它自己重写了 ConnectAsync，Hello 的构造走的是这条路。
        //
        // ConnectAsync(bytes) 的参数**就是 Hello 载荷** —— 而且是稀有事件，
        // 打日志完全安全。
        // ═══════════════════════════════════════════════════════════
        [HarmonyPatch(typeof(Hazel.Udp.UnityUdpClientConnection), nameof(Hazel.Udp.UnityUdpClientConnection.ConnectAsync))]
        internal static class Patch_UnityConnectAsync
        {
            [HarmonyPrefix]
            private static void Prefix(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte> bytes)
            {
                try
                {
                    var sb = new StringBuilder();
                    if (bytes != null) for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("X2")).Append(' ');
                    Plugin.L.LogWarning($"[HELLO] ★★★ 真实 Hello 载荷  {bytes?.Length ?? 0}B: {sb.ToString().TrimEnd()}");
                }
                catch (Exception e) { Plugin.L.LogError($"[HELLO] ConnectAsync 读取失败: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Hazel.Udp.UdpServerConnection), nameof(Hazel.Udp.UdpServerConnection.ConnectAsync))]
        internal static class Patch_ServerConnectAsync
        {
            [HarmonyPrefix]
            private static void Prefix(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte> bytes)
            {
                try
                {
                    var sb = new StringBuilder();
                    if (bytes != null) for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("X2")).Append(' ');
                    Plugin.L.LogWarning($"[SRV] ★ 服务端处理新连接 Hello  {bytes?.Length ?? 0}B: {sb.ToString().TrimEnd()}");
                }
                catch (Exception e) { Plugin.L.LogError($"[SRV] ConnectAsync 读取失败: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Hazel.Udp.UdpServerConnection), nameof(Hazel.Udp.UdpServerConnection.SendDisconnect))]
        internal static class Patch_ServerDisconnect
        {
            // ★ 断开原因就藏在 MessageWriter 里 —— 必须把内容读出来
            [HarmonyPrefix]
            private static void Prefix(MessageWriter data)
            {
                try
                {
                    var sb = new StringBuilder();
                    if (data != null && data.Buffer != null)
                    {
                        int len = Math.Min(data.Length > 0 ? data.Length : data.Position, 64);
                        for (int i = 0; i < len; i++) sb.Append(data.Buffer[i].ToString("X2")).Append(' ');
                    }
                    Plugin.L.LogWarning($"[SRV] ★ 服务端断开客户端，原因载荷: {sb.ToString().TrimEnd()}");
                }
                catch (Exception e) { Plugin.L.LogError($"[SRV] SendDisconnect 读取失败: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Connection), nameof(Connection.InvokeDataReceived))]
        internal static class Patch_InvokeDataReceived
        {
            [HarmonyPrefix]
            private static void Prefix(MessageReader msg, SendOption sendOption)
            {
                // ★ 这里绝不做任何 I/O —— 只累加计数、拼字符串
                try
                {
                    if (msg == null) return;
                    byte tag = msg.Tag;

                    lock (_lock)
                    {
                        _totalMsgs++;
                        if (tag == 0x05 || tag == 0x06) { _gameDataCount++; return; }
                        if (_detailLogged >= MaxDetail) return;
                        _detailLogged++;
                        _pending.AppendLine(
                            $"  [RAW] ← tag=0x{tag:X2} ({TagName(tag)}) opt={sendOption} 剩={msg.BytesRemaining}B");
                    }
                }
                catch { }
            }
        }
    }
}
