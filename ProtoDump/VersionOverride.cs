using System;
using System.IO;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace ProtoDump
{
    /// <summary>
    /// ★ 让手机版能够加入电脑的房间 —— 用于抓取「真实加入者」的完整包序列。
    ///
    /// 关键认识：服务端校验客户端版本时，比的**不是** GetBroadcastVersion()，
    /// 而是 Constants.CompatVersions 这个数组。
    ///
    /// 所以最省事的做法不是去猜手机的确切版本号，而是
    /// **让 CompatVersions 返回一个覆盖一大段范围的数组** ——
    /// 手机的版本无论落在哪，都能匹配上。
    ///
    /// 范围由 version-range.txt 控制（两行：起始 结束）。删掉文件即恢复原样。
    /// </summary>
    internal static class VersionOverride
    {
        private static string CfgPath =>
            System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "version-range.txt");

        private static long _lastStamp = -1;
        private static int _lo = 50663600, _hi = 50663999;

        private static void Refresh()
        {
            try
            {
                if (!File.Exists(CfgPath)) return;
                long st = File.GetLastWriteTimeUtc(CfgPath).Ticks;
                if (st == _lastStamp) return;
                _lastStamp = st;

                var lines = File.ReadAllLines(CfgPath);
                if (lines.Length >= 2 &&
                    int.TryParse(lines[0].Trim(), out int lo) &&
                    int.TryParse(lines[1].Trim(), out int hi) && hi >= lo)
                {
                    _lo = lo; _hi = hi;
                    Plugin.L.LogWarning($"[VER] ★ CompatVersions 覆盖生效: {_lo} ~ {_hi} （{_hi - _lo + 1} 个版本）");
                }
            }
            catch { }
        }

        // ⚠️ 原 Patch_Compat（改 CompatVersions）已移除：
        //   CompatVersions 在 Harmony 里不是可 patch 的方法名，
        //   它会让 PatchAll 整体抛异常，连带 PlayerInfoWatch 也挂不上。
        //   而且实测表明服务端压根不做版本校验（撑开范围后日志零触发），
        //   所以这个补丁本来就无用。
    }
}
