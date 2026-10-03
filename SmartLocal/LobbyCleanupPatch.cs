using System;
using HarmonyLib;

namespace SmartLocal
{
    /// <summary>
    /// 离开房间时清理 AI 状态。
    ///
    /// 实测反馈：退出房间后重新创建房间，本机玩家会变成「三问号绿人」，
    /// 必须重启游戏才能恢复。
    ///
    /// 原因：Brain 列表、物理体反查表、_reowned 等状态都还是上一局的残留，
    /// 而对象早已被销毁 —— 身份守卫拿着过期的 ClientData.Character 去写
    /// PlayerControl.LocalPlayer，就把本机身份写坏了。
    ///
    /// 所以在 LobbyBehaviour 销毁时（离开房间）把状态彻底清空。
    /// </summary>
    [HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.OnDestroy))]
    internal static class LobbyCleanupPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                SmartLocalState.KeepAlive = false;
                BotManager.ResetAll();
                Plugin.Logger.LogInfo("[清理] 大厅已销毁，离开房间，AI 状态清空。");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[清理] 失败: {e}");
            }
        }
    }
}
