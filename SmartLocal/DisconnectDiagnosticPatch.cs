using System;
using HarmonyLib;
using InnerNet;

namespace SmartLocal
{
    /// <summary>
    /// 断连诊断钩子。
    ///
    /// 目的：按「开始」后服务端会因为
    ///   "Timeout while waiting for other player data"
    /// 把客户端踢掉。这个钩子在断连发生的**瞬间**抓取现场：
    ///   - 断连原因字符串
    ///   - allClients 里每个客户端的完整状态（IsReady / InScene / Character / Puid / FriendCode）
    ///
    /// 有了这份快照，就能判断服务端到底在等哪个字段，
    /// 而不是靠猜 —— 上一轮我们就是靠日志才定位到 ClientData 这条线。
    ///
    /// 注意：这个钩子只读不改，纯诊断，不改变任何游戏行为。
    /// </summary>
    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.DisconnectInternal))]
    internal static class DisconnectDiagnosticPatch
    {
        [HarmonyPrefix]
        private static void Prefix(DisconnectReasons reason, string stringReason)
        {
            try
            {
                Plugin.Logger.LogError($"════════ 断连诊断 ════════");
                Plugin.Logger.LogError($"reason={reason}   stringReason='{stringReason}'");
                BotFactory.DumpClientStates("断连瞬间");
                Plugin.Logger.LogError($"════════════════════════");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"断连诊断本身出错: {e}");
            }
        }
    }
}
