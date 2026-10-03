using System;
using HarmonyLib;
using AmongUs.GameOptions;
using UnityEngine;

namespace SmartLocal
{
    /// <summary>
    /// 阶段 2：打开创建游戏界面时，强制隐藏「聊天」标签页。
    ///
    /// 【确证】CreateGameOptions 只有两个标签：
    ///   tabButtons[0] / contentObjects[0] = General
    ///   tabButtons[1] / contentObjects[1] = Chat
    /// 依据：public void OpenTab(bool isGeneral) —— 单个 bool 参数。
    /// </summary>
    internal static class CreateGameOptionsPatch
    {
        /// <summary>把「聊天」标签和它的内容面板一起关掉。</summary>
        internal static void HideChatTab(CreateGameOptions instance)
        {
            if (instance == null) return;

            try
            {
                // 数组是定长的，隐藏下标 1 即可，0 号不受影响。
                var tabs = instance.tabButtons;
                if (tabs != null && tabs.Length > SmartLocalState.ChatTabIndex)
                {
                    var chatTab = tabs[SmartLocalState.ChatTabIndex];
                    if (chatTab != null && chatTab.gameObject != null)
                        chatTab.gameObject.SetActive(false);
                }

                var contents = instance.contentObjects;
                if (contents != null && contents.Length > SmartLocalState.ChatTabIndex)
                {
                    var chatContent = contents[SmartLocalState.ChatTabIndex];
                    if (chatContent != null)
                        chatContent.SetActive(false);
                }

                // 聊天专属控件一并关掉，避免残留悬空引用报错
                if (instance.chatOptionUI != null && instance.chatOptionUI.gameObject != null)
                    instance.chatOptionUI.gameObject.SetActive(false);

                if (instance.chatDescText != null) instance.chatDescText.gameObject.SetActive(false);
                if (instance.chatWarningText != null) instance.chatWarningText.gameObject.SetActive(false);

                // 确保当前停在 General 页
                // currentTag 必须归位，否则 ValueChanged() 会按旧 tag 把聊天页重新激活
                instance.currentTag = 0;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"隐藏聊天标签失败: {e}");
            }
        }
    }

    /// <summary>界面显示完成后立刻隐藏聊天页（含首次进入）。</summary>
    [HarmonyPatch(typeof(CreateGameOptions), nameof(CreateGameOptions.Show))]
    internal static class CreateGameOptionsShowPatch
    {
        [HarmonyPostfix]
        private static void Postfix(CreateGameOptions __instance)
        {
            if (!SmartLocalState.IsSmartLocalGame) return;
            CreateGameOptionsPatch.HideChatTab(__instance);
        }
    }

    /// <summary>动画协程结束后再隐藏一次（CoShow 会重排布局，可能复活标签）。</summary>
    [HarmonyPatch(typeof(CreateGameOptions), nameof(CreateGameOptions.CoShow))]
    internal static class CreateGameOptionsCoShowPatch
    {
        [HarmonyPostfix]
        private static void Postfix(CreateGameOptions __instance)
        {
            if (!SmartLocalState.IsSmartLocalGame) return;
            CreateGameOptionsPatch.HideChatTab(__instance);
        }
    }

    /// <summary>
    /// 从源头掐断：任何试图切到聊天页的调用都改写成切 General 页。
    /// 只隐藏 GameObject 是不够的 —— OpenTab / ValueChanged 会按 currentTag 把它救回来。
    /// </summary>
    [HarmonyPatch(typeof(CreateGameOptions), nameof(CreateGameOptions.OpenTab))]
    internal static class CreateGameOptionsOpenTabPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ref bool isGeneral)
        {
            if (!SmartLocalState.IsSmartLocalGame) return;
            if (!isGeneral)
            {
                Plugin.Logger.LogInfo("拦截到聊天标签切换请求，强制改为 General。");
                isGeneral = true;
            }
        }
    }

    /// <summary>
    /// 在「创建游戏」确认的那一刻，把最终人数记下来，供大厅注入假人用。
    ///
    /// 【确证】人群来源优先级：
    ///   GameOptionsManager.Instance.CurrentGameOptions.MaxPlayers (IGameOptions.MaxPlayers)
    /// 是权威数据源；CreateGameOptions.GetCapacity() / capacityOption.Value 只是界面层读数。
    /// 以界面读数为准会出现「显示 15 实际 10」的不一致。
    /// </summary>
    [HarmonyPatch(typeof(CreateGameOptions), nameof(CreateGameOptions.Confirm))]
    internal static class CreateGameOptionsConfirmPatch
    {
        [HarmonyPostfix]
        private static void Postfix(CreateGameOptions __instance)
        {
            if (!SmartLocalState.IsSmartLocalGame) return;

            try
            {
                int max = -1;
                var gom = GameOptionsManager.Instance;
                if (gom != null && gom.CurrentGameOptions != null)
                    max = gom.CurrentGameOptions.MaxPlayers;

                // 拿不到就退回界面读数
                if (max <= 0) max = Mathf.RoundToInt(__instance.GetCapacity());

                // 本地玩家自己占 1 个名额
                SmartLocalState.DesiredBotCount = Mathf.Max(0, max - 1);
                Plugin.Logger.LogInfo($"最大人数={max}，计划注入假人={SmartLocalState.DesiredBotCount}");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"计算假人数量失败: {e}");
            }
        }
    }
}
