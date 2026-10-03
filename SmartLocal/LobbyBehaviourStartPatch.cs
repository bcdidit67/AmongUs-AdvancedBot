using System;
using HarmonyLib;

namespace SmartLocal
{
    /// <summary>
    /// 阶段 4 触发点：进入本地等待大厅后启动假人注入。
    ///
    /// 为什么挂 LobbyBehaviour.Start 而不是 CreateGameOptions.Confirm：
    /// Confirm 那一刻大厅对象还没实例化，SpawnPositions（座位数组）拿不到。
    /// LobbyBehaviour.Start 时座位数组、GameData 都已就绪。
    /// </summary>
    [HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
    internal static class LobbyBehaviourStartPatch
    {
        [HarmonyPostfix]
        private static void Postfix(LobbyBehaviour __instance)
        {
            try
            {
                int count;
                if (Plugin.SpikeMode)
                {
                    count = Plugin.HardcodedBotCount;
                }
                else
                {
                    if (!SmartLocalState.IsSmartLocalGame) return;
                    count = SmartLocalState.DesiredBotCount;
                }

                if (count <= 0)
                {
                    Plugin.Logger.LogWarning("假人数量为 0，跳过注入。");
                    return;
                }

                int seats = __instance.SpawnPositions != null ? __instance.SpawnPositions.Length : 0;
                Plugin.Logger.LogInfo($"大厅已就绪：座位数={seats}，目标假人={count}");

                if (Plugin.Instance == null)
                {
                    Plugin.Logger.LogError("Plugin.Instance 为空，无法挂载 BotSpawner。");
                    return;
                }

                var spawner = Plugin.Instance.AddComponent<BotSpawner>();
                if (spawner == null)
                {
                    Plugin.Logger.LogError("AddComponent<BotSpawner> 返回 null。");
                    return;
                }

                spawner.Begin(count);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"启动假人注入失败: {e}");
            }
        }
    }
}
