using System;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Events;

namespace SmartLocal
{
    /// <summary>
    /// 阶段 1：在主菜单「本地」按钮旁注入「智能本地」按钮。
    ///
    /// 【确证】MainMenuManager 不是单例（属性表无 Instance），
    /// 只能用 FindObjectOfType 获取实例。
    ///
    /// 注入时机选 ConnectMainMenuScreenButtonEvents 的 Postfix：
    /// 原版刚bind完所有按钮事件，此时 UI 层级已就绪，
    /// 且我们的按钮不会被原版的 ResetScreen() 清掉。
    /// </summary>
    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.ConnectMainMenuScreenButtonEvents))]
    internal static class MainMenuButtonPatch
    {
        [HarmonyPostfix]
        private static void Postfix(MainMenuManager __instance)
        {
            // 阶段 4 冒烟测试期间关闭按钮注入，减少变量
            if (!Plugin.EnableMainMenuButton) return;
            if (SmartLocalState.ButtonInjected) return;
            if (__instance == null) return;

            try
            {
                var realButton = __instance.playLocalButton;
                if (realButton == null)
                {
                    Plugin.Logger.LogError("playLocalButton 为空，无法克隆按钮。");
                    return;
                }

                // 克隆原版「本地」按钮：自动继承 sprite / 悬停效果 / 音效 / 控制器导航。
                var parent = realButton.transform.parent;
                var clone = UnityEngine.Object.Instantiate(realButton.gameObject, parent);
                clone.name = "SmartLocalButton";

                var btn = clone.GetComponent<PassiveButton>();
                if (btn == null)
                {
                    Plugin.Logger.LogError("克隆体上没有 PassiveButton。");
                    return;
                }

                // 改文字
                var label = btn.buttonText;
                if (label != null) label.text = Plugin.ButtonLabel;

                // 位置：原按钮左边（原版常量 X_OFFSCREEN_LEFT = -2.85f 之类的量级）
                var t = clone.transform;
                var p = t.localPosition;
                t.localPosition = new Vector3(p.x, p.y - 0.62f, p.z);

                // 重绑点击事件。
                // ⚠️ IL2CPP 下不能直接传 C# lambda 给 UnityAction，
                //    必须经 DelegateSupport.ConvertDelegate 转换。
                System.Action onClick = () => OpenSmartLocal(__instance);
                btn.OnClick.AddListener(
                    DelegateSupport.ConvertDelegate<UnityAction>(onClick));

                SmartLocalState.ButtonInjected = true;
                Plugin.Logger.LogInfo("「智能本地」按钮注入完成。");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"注入按钮失败: {e}");
            }
        }

        /// <summary>
        /// 复用原版的界面打开逻辑。
        /// 不直接调 createGameScreen.Show()，因为那会跳过界面状态机
        /// （ResetScreen / disableOnStartup / 控制器导航重算），返回时 UI 容易错乱。
        /// </summary>
        private static void OpenSmartLocal(MainMenuManager menu)
        {
            SmartLocalState.IsSmartLocalGame = true;
            Plugin.Logger.LogInfo("进入智能本地流程。");

            if (menu.createGameScreen != null)
            {
                // 标记好之后交给原版协程做动画与状态切换
                menu.OpenCreateGame();
            }
            else
            {
                Plugin.Logger.LogError("createGameScreen 尚未初始化。");
            }
        }
    }
}
