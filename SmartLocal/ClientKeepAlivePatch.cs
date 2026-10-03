using System;
using HarmonyLib;
using InnerNet;

namespace SmartLocal
{
    /// <summary>
    /// 逐帧保活 + 周期性状态采样。
    ///
    /// 假设：按「开始」时游戏会把所有客户端的就绪/场景状态**重置**，
    /// 然后等服务端确认它们都回来了。假人永远不会回来 → 超时 DC。
    ///
    /// 上一轮我用的是每 30 帧刷一次的续期循环，颗粒度太粗，会被逐帧的检查抢先。
    /// 这里改成挂在 InnerNetClient.Update 的 Postfix 上，**每帧**重刷，
    /// 保证标志在服务端检查的那一刻一定为真。
    ///
    /// 同时每 60 帧打一行紧凑状态，万一还是失败，
    /// 我们能从日志看到状态在 DC 之前是怎么演变的 —— 这比只看 DC 瞬间的快照更有信息量。
    /// </summary>
    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.Update))]
    internal static class ClientKeepAlivePatch
    {
        private static int _lastLogFrame = -999;

        [HarmonyPostfix]
        private static void Postfix(InnerNetClient __instance)
        {
            if (!SmartLocalState.KeepAlive) return;

            try
            {
                // 每帧静默重刷就绪标志与账号数据
                BotFactory.FixupBotClients(verbose: false);

                // 周期性采样：只打一行，避免刷屏
                int f = UnityEngine.Time.frameCount;
                if (f - _lastLogFrame >= 60)
                {
                    _lastLogFrame = f;
                    LogSample(__instance);
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[保活] 异常: {e}");
                SmartLocalState.KeepAlive = false;   // 出错就自停，避免每帧刷错误
            }
        }

        private static void LogSample(InnerNetClient c)
        {
            var auc = AmongUsClient.Instance;
            if (auc == null || auc.allClients == null) return;

            int ready = 0, inScene = 0, noPuid = 0, total = 0;
            var clients = auc.allClients;
            for (int i = 0; i < clients.Count; i++)
            {
                var cd = clients[i];
                if (cd == null) continue;
                total++;
                if (cd.IsReady) ready++;
                if (cd.InScene) inScene++;
                if (string.IsNullOrEmpty(cd.ProductUserId)) noPuid++;
            }

            var me = auc.GetClient(auc.ClientId);
            Plugin.Logger.LogInfo(
                $"[保活] frame={UnityEngine.Time.frameCount} 客户端={total} IsReady={ready}/{total} " +
                $"InScene={inScene}/{total} 缺Puid={noPuid} | 本机 id={auc.ClientId} " +
                $"IsReady={(me != null ? me.IsReady.ToString() : "?")} InScene={(me != null ? me.InScene.ToString() : "?")} " +
                $"NetworkMode={auc.NetworkMode}");
        }
    }
}
