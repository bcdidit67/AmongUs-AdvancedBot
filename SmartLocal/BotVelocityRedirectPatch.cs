using System;
using HarmonyLib;
using UnityEngine;

namespace SmartLocal
{
    /// <summary>
    /// ★★ 解决「输入劫持」的决定性补丁。
    ///
    /// 现象（实测）：不按键时假人乱动（我们的速度生效），
    /// 一按 WASD 全体立刻转向并跟着玩家走。
    ///
    /// 推断的调用顺序：
    ///     PlayerPhysics.FixedUpdate():
    ///         ├─ [我们的 Prefix] 设置 AI 方向
    ///         ├─ [方法体] 读输入 → SetNormalizedVelocity(玩家方向)   ← 把我们的覆盖掉了
    ///         └─ 按速度移动
    /// 所以补 FixedUpdate 的 Prefix 不够 —— 游戏在方法体里又写了一次。
    ///
    /// 正确的位置是 **拦截 setter 本身**：
    ///     PlayerPhysics.SetNormalizedVelocity(Vector2 direction)
    ///
    /// 这是 PlayerPhysics 上唯一的速度写入口（已确认没有其他 SetVelocity）。
    /// 只要它是唯一的入口，那么无论谁来写、在哪写、什么顺序写，
    /// 写进去的都会变成 AI 的方向 —— 从根上无法被覆盖回去。
    /// </summary>
    [HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.SetNormalizedVelocity))]
    internal static class BotVelocityRedirectPatch
    {
        /// <summary>
        /// 补丁实际触发次数。
        ///
        /// ★ 这个数字是当前最关键的诊断指标：
        ///   如果它一直是 0，说明游戏的输入**根本不经过这个托管 setter** ——
        ///   `SetNormalizedVelocity` 极可能被 IL2CPP 内联进了 PlayerControl.FixedUpdate，
        ///   原生代码直接写内部速度字段，Harmony 无钩可挂。
        ///   那样的话拦截 setter 这条路就是死的，必须改用「直接覆盖位置」。
        /// </summary>
        internal static int HitCount;

        [HarmonyPrefix]
        private static void Prefix(PlayerPhysics __instance, ref Vector2 direction)
        {
            HitCount++;

            if (!Plugin.EnableBotMovement) return;

            try
            {
                if (!BotManager.TryGetDirection(__instance, out Vector2 aiDir)) return;
                direction = aiDir;
            }
            catch (Exception e)
            {
                if (!_logged)
                {
                    _logged = true;
                    Plugin.Logger.LogError($"[AI] 速度重定向失败: {e}");
                }
            }
        }

        private static bool _logged;
    }
}
