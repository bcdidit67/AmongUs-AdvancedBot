using System;
using HarmonyLib;

namespace SmartLocal
{
    /// <summary>
    /// ★★★ 位置层接管 —— 目前唯一还没被游戏绕开的控制点。
    ///
    /// 三轮实测的结论汇总：
    ///   1) ownerId = NoClientId  → 独立移动有效，但服务端超时断连（不可用）
    ///   2) 开局后改 ownerId       → 不解决问题，速度仍与玩家精确相等
    ///   3) 拦 SetNormalizedVelocity → 补丁命中 800 次/2秒，但假人速度读出来
    ///      仍是玩家的 → **游戏直接写字段，绕开了托管 setter**
    ///
    /// 也就是说：速度层和归属层都拦不住，因为游戏走的是原生直接写入。
    ///
    /// 唯一剩余的层是 **LateUpdate 里的位置**：
    ///   Unity 帧序 = FixedUpdate（物理）→ Update → LateUpdate → 渲染
    ///   在 LateUpdate 写入的 transform.position 当帧不会再被覆盖，
    ///   因此这是「对渲染而言的最终结论」。
    ///
    /// 代价：绕过了游戏自身的物理与碰撞（假人可能贴墙/穿墙）。
    /// 这是当前唯一能拿到独立控制的方案，碰撞问题后续可以自己补射线检测。
    /// </summary>
    [HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.LateUpdate))]
    internal static class BotPositionOverridePatch
    {
        /// <summary>补丁触发次数（诊断用）</summary>
        internal static int HitCount;

        [HarmonyPostfix]
        private static void Postfix(PlayerPhysics __instance)
        {
            HitCount++;

            if (!Plugin.EnableBotPositionOverride) return;

            try
            {
                if (!BotManager.TryGetBrain(__instance, out var brain)) return;
                brain.ApplyPosition();
            }
            catch (Exception e)
            {
                if (!_logged)
                {
                    _logged = true;
                    Plugin.Logger.LogError($"[AI] 位置接管失败: {e}");
                }
            }
        }

        private static bool _logged;
    }
}
