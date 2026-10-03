using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace SmartLocal
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BasePlugin
    {
        public const string PluginGuid = "com.smartlocal.amongus";
        public const string PluginName = "Smart Local";
        public const string PluginVersion = "0.4.0-spike";

        // ══════════════════════════════════════════════════════════
        //  阶段 4 冒烟开关
        // ══════════════════════════════════════════════════════════

        /// <summary>true = 跳过 UI 门控，进入原版「本地游戏」大厅就无条件注入假人。</summary>
        public const bool SpikeMode = true;

        /// <summary>写死假人数量。本地玩家占 1 个名额，所以 14 = 15 人满员。</summary>
        public const int HardcodedBotCount = 14;

        /// <summary>
        /// 假人生成路径选择。
        ///
        /// true  = isDummy 路线：不注册客户端（GameData.AddDummy + Spawn），
        ///         绕开内网「Timeout while waiting for other player data」超时。
        /// false = 伪 ClientData 路线（旧方案，已实测会被服务端超时踢掉，仅留作对照）。
        /// </summary>
        public const bool UseDummyPath = true;

        /// <summary>阶段 1 的主菜单按钮。冒烟测试期间关闭，减少变量。</summary>
        public const bool EnableMainMenuButton = false;

        // ══════════════════════════════════════════════════════════

        public const string ButtonLabel = "智能本地";

        internal static Plugin Instance;
        internal static ManualLogSource Logger;

        public override void Load()
        {
            Instance = this;
            Logger = Log;
            Logger.LogInfo($"{PluginName} v{PluginVersion} loading...");
            Logger.LogInfo($"模式: SpikeMode={SpikeMode} 硬编码假人={HardcodedBotCount} 主菜单按钮={EnableMainMenuButton}");

            // 自定义 MonoBehaviour 必须先在 il2cpp 侧注册类型，
            // 否则 AddComponent 会失败/崩溃。
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<BotSpawner>();
                Logger.LogInfo("BotSpawner 类型已注册进 il2cpp。");
            }
            catch (Exception e)
            {
                Logger.LogWarning($"BotSpawner 注册失败（可能已注册过）: {e.Message}");
            }

            SmartLocalState.Reset();

            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo("Harmony patches applied.");
        }
    }

    internal static class SmartLocalState
    {
        public static bool IsSmartLocalGame;
        public static bool ButtonInjected;
        public const int ChatTabIndex = 1;
        public static int DesiredBotCount;

        /// <summary>逐帧保活开关：注入假人后打开，由 ClientKeepAlivePatch 每帧重刷就绪标志。</summary>
        public static bool KeepAlive;

        public static void Reset()
        {
            IsSmartLocalGame = false;
            ButtonInjected = false;
            DesiredBotCount = 0;
            KeepAlive = false;
        }
    }
}
