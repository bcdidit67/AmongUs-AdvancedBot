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

        /// <summary>
        /// 假人的 ownerId 策略。
        ///
        /// ⚠️ 实测结论：这两个取值构成一组**硬冲突**，不要指望靠它解决独立驱动问题。
        ///
        ///   false = 本机 ClientId ← **当前采用**
        ///       网络层正常（服务端认为对象有主，不等待），能正常开局；
        ///       但游戏的输入层会按归属把本地玩家输入套到它们身上 → 蜂群行为。
        ///       解决办法不是改这里，而是用 BotInputOverridePatch 在物理步前覆盖速度。
        ///
        ///   true  = InnerNetClient.NoClientId
        ///       输入劫持解除、假人能独立移动（实测有效的）；
        ///       但对象变成「无人所有」，服务端把它们当成其他玩家等待数据，
        ///       触发 "Timeout while waiting for other player data" 断连。
        /// </summary>
        public const bool UseNoClientOwner = false;

        /// <summary>
        /// 是否驱动假人独立移动（阶段 7a 验证项）。
        ///
        /// 关掉它、只开 UseNoClientOwner，就能得到「假人变为静止木桩」的基准态 ——
        /// 那恰好证明本地输入不再劫持它们。
        /// </summary>
        public const bool EnableBotMovement = true;

        /// <summary>
        /// 位置层接管开关（方案A 的核心）。
        ///
        /// 所有权划分：
        ///   我们拥有 → 假人的**位置**（自己积分移动 + 射线避墙）
        ///   游戏拥有 → 身份、相机、灯光、HUD、职业、任务、击杀
        ///
        /// 走这条路的理由（前三轮的教训）：
        /// 逐个去争抢「本机玩家」身份附带的组件（输入/相机/灯光/HUD），
        /// 每抢一个就在别处留下不一致 —— 修好 A 必然坏掉 B。
        ///
        /// 位置接管的实测优点：
        ///   假人确实独立移动（散落全图，坐标互不相同）
        ///   压制住了跟随行为（位置差被钉住不再缩小）
        ///   完全不碰身份系统 → 不引发 HUD/相机/输入连锁故障
        ///
        /// 代价是绕过游戏物理，所以自带射线避墙（BotBrain.Blocked）。
        /// </summary>
        public const bool EnableBotPositionOverride = true;

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
