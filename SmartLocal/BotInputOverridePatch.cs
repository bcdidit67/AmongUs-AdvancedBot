using System;
using HarmonyLib;
using UnityEngine;

namespace SmartLocal
{
    /// <summary>
    /// 主动驱动：每个物理步为假人写入 AI 方向。
    ///
    /// 与 BotVelocityRedirectPatch 的分工：
    ///   - Redirect 负责「游戏来写时改成我们的」（防守）
    ///   - 本补丁负责「游戏不来写时我们也要写」（进攻）
    ///
    /// 实测发现游戏**只在有输入时**才调用 SetNormalizedVelocity，
    /// 所以不按键时假人会失去驱动 —— 必须由我们每步补上。
    ///
    /// 另外增加了「比赛进行中」门控：
    ///   只在 ShipStatus 已加载且过场动画已结束时驱动，
    ///   否则假人会在准备阶段/加载/分身份时乱动（实测反馈的问题）。
    /// </summary>
    [HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.FixedUpdate))]
    internal static class BotInputOverridePatch
    {
        /// <summary>补丁触发次数（诊断用，和重定向补丁的命中数对比）</summary>
        internal static int HitCount;

        [HarmonyPrefix]
        private static void Prefix(PlayerPhysics __instance)
        {
            HitCount++;

            if (!Plugin.EnableBotMovement) return;

            try
            {
                if (!BotManager.CanBotsMove()) return;
                if (!BotManager.TryGetDirection(__instance, out Vector2 dir)) return;
                __instance.SetNormalizedVelocity(dir);
            }
            catch (Exception e)
            {
                if (!_logged)
                {
                    _logged = true;
                    Plugin.Logger.LogError($"[AI] 速度覆盖失败: {e}");
                }
            }
        }

        private static bool _logged;
    }
}
