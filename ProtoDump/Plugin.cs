using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using InnerNet;
using UnityEngine;

namespace ProtoDump
{
    /// <summary>
    /// 协议常量导出插件。
    ///
    /// 目的：把「元数据里读不到、只有运行期才知道」的协议常量打进日志。
    ///
    /// 关键是 **InnerNet.Tags 的那 26 个静态 byte 值** —— 它们在元数据里只有
    /// 「静态属性」这个事实，没有数值；而它们是构造任何协议包的基础。
    /// 公开的协议文档是旧版本的（v19 多出 10 个包类型），只有运行中的游戏才是权威。
    ///
    /// 做法：**用反射遍历所有静态属性**，而不是硬编码标签名 ——
    /// 这样即使 v19 新增了包类型也一个不漏。
    /// </summary>
    [BepInPlugin(Guid, "ProtoDump", "1.0.0")]
    public class Plugin : BasePlugin
    {
        public const string Guid = "com.smartlocal.protodump";
        internal static ManualLogSource L;

        public override void Load()
        {
            L = Log;
            L.LogInfo("════════ ProtoDump 开始导出协议常量 ════════");

            DumpTags();
            DumpRpcCalls();
            DumpGameDataTypes();
            DumpVersion();
            DumpDisconnectReasons();
            PlayerInfoWatch.DumpInvalidNetId();

            // 客户端状态（GameId 等）需要延迟到进房后才有值
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<ClientWatcher>();
                AddComponent<ClientWatcher>();
                L.LogInfo("[ProtoDump] ClientWatcher 已挂载，将每 2 秒检查一次客户端状态");
            }
            catch (Exception e)
            {
                L.LogError($"[ProtoDump] 挂载 ClientWatcher 失败: {e}");
            }

            try
            {
                var h = new Harmony("com.smartlocal.protodump.packets");
                h.PatchAll(typeof(PacketWatch).Assembly);
                h.PatchAll(typeof(RawWatch).Assembly);
                h.PatchAll(typeof(VersionOverride).Assembly);
                h.PatchAll(typeof(PlayerInfoWatch).Assembly);
                L.LogInfo("[ProtoDump] PlayerInfoWatch 已挂载（AddPlayer 观测）");
                L.LogInfo("[ProtoDump] VersionOverride 已挂载（版本号可伪装）");
                L.LogInfo("[ProtoDump] PacketWatch 补丁已挂载（入站包观测）");
            }
            catch (Exception e) { L.LogError($"[ProtoDump] Harmony 补丁失败: {e}"); }

            L.LogInfo("════════ ProtoDump 导出完毕 ════════");
        }

        /// <summary>
        /// ★ 核心：反射遍历 InnerNet.Tags 的全部静态属性，读出运行期真值。
        /// </summary>
        private static void DumpTags()
        {
            L.LogInfo("──── InnerNet.Tags（顶层包类型 / 握手标签）────");
            try
            {
                var t = typeof(Tags);
                var props = t.GetProperties(BindingFlags.Public | BindingFlags.Static);
                int n = 0;
                foreach (var p in props)
                {
                    try
                    {
                        object v = p.GetValue(null);
                        if (v == null) { L.LogWarning($"  [TAG] {p.Name} = <null>"); continue; }
                        byte b = Convert.ToByte(v);
                        L.LogInfo($"  [TAG] {p.Name} = 0x{b:X2}  ({b})");
                        n++;
                    }
                    catch (Exception e) { L.LogWarning($"  [TAG] {p.Name} 读取失败: {e.Message}"); }
                }
                L.LogInfo($"  → 共 {n} 个标签");
            }
            catch (Exception e) { L.LogError($"  Tags 反射失败: {e}"); }
        }

        /// <summary>
        /// ★ 客户端版本号 —— 外置 bot 客户端做 Hello 握手时**必须**携带的值。
        ///
        /// 实测：用文档样例的 2020.9.7 (50516550) 能收到服务端的 Acknowledgement
        /// 和被正确解析的回应，但**始终收不到 JoinedGame** —— 加入被拒。
        /// 版本不匹配是最可能的原因，所以必须读出真值。
        ///
        /// 编码公式（文档）：version = year*25000 + month*1800 + day*50 + revision
        /// </summary>
        private static void DumpVersion()
        {
            L.LogInfo("──── Constants 版本号（Hello 握手必需）────");
            try
            {
                int bv = Constants.GetBroadcastVersion();
                L.LogInfo($"  [VER] ★ BroadcastVersion = {bv} (0x{unchecked((uint)bv):X8})");

                // 反解成人类可读，验证编码公式
                int v = bv;
                int year = v / 25000; v %= 25000;
                int month = v / 1800; v %= 1800;
                int day = v / 50;
                int rev = v % 50;
                L.LogInfo($"  [VER]   解码 = {year}.{month}.{day}.{rev}");
            }
            catch (Exception e) { L.LogError($"  [VER] GetBroadcastVersion 失败: {e.Message}"); }

            try
            {
                var cv = Constants.CompatVersions;
                if (cv != null)
                {
                    L.LogInfo($"  [VER] CompatVersions 共 {cv.Length} 个:");
                    for (int i = 0; i < cv.Length; i++)
                        L.LogInfo($"    [VER]   [{i}] = {cv[i]}");
                }
                else L.LogWarning("  [VER] CompatVersions 为 null");
            }
            catch (Exception e) { L.LogWarning($"  [VER] CompatVersions 失败: {e.Message}"); }

            try { L.LogInfo($"  [VER] extraBuildVersionInfo = '{Constants.extraBuildVersionInfo}'"); } catch { }
            try { L.LogInfo($"  [VER] pipelineBuildNumber = {Constants.pipelineBuildNumber}"); } catch { }
            try { L.LogInfo($"  [VER] MODDER_VERSION = {Constants.MODDER_VERSION}"); } catch { }
        }


        /// <summary>
        /// ★ DisconnectReasons 的运行期数值 —— 服务端回给我们 0x03，
        /// 必须知道它到底代表哪个原因（GameStarted？GameNotFound？）。
        /// 枚举的声明顺序不等于数值顺序，所以只能运行期读。
        /// </summary>
        private static void DumpDisconnectReasons()
        {
            L.LogInfo("──── DisconnectReasons（断开原因码）────");
            try
            {
                var t = Type.GetType("DisconnectReasons, Assembly-CSharp")
                     ?? Type.GetType("InnerNet.DisconnectReasons, Assembly-CSharp");
                if (t == null) { L.LogWarning("  找不到 DisconnectReasons"); return; }
                int n = 0;
                foreach (var v in Enum.GetValues(t))
                {
                    int num = Convert.ToInt32(v);
                    L.LogInfo("  [DCR] " + num.ToString().PadLeft(4) + " = " + v);
                    n++;
                }
                L.LogInfo($"  → 共 {n} 个原因码");
            }
            catch (Exception e) { L.LogError($"  DisconnectReasons 失败: {e.Message}"); }
        }

        /// <summary>RpcCalls 枚举的全部取值（元数据里其实有，这里做交叉验证）</summary>
        private static void DumpRpcCalls()
        {
            L.LogInfo("──── RpcCalls（RPC 操作码）────");
            try
            {
                var t = typeof(RpcCalls);
                int n = 0;
                foreach (var name in Enum.GetNames(t))
                {
                    try
                    {
                        var v = Convert.ToByte(Enum.Parse(t, name));
                        L.LogInfo($"  [RPC] {name} = {v}");
                        n++;
                    }
                    catch (Exception e) { L.LogWarning($"  [RPC] {name} 解析失败: {e.Message}"); }
                }
                L.LogInfo($"  → 共 {n} 个 RPC");
            }
            catch (Exception e) { L.LogError($"  RpcCalls 反射失败: {e}"); }
        }

        private static void DumpGameDataTypes()
        {
            L.LogInfo("──── GameDataTypes（GameData 内部分类）────");
            try
            {
                var t = Type.GetType("AmongUs.InnerNet.GameDataMessages.GameDataTypes, Assembly-CSharp");
                if (t == null) { L.LogWarning("  找不到 GameDataTypes 类型"); return; }
                foreach (var name in Enum.GetNames(t))
                {
                    try
                    {
                        var v = Convert.ToByte(Enum.Parse(t, name));
                        L.LogInfo($"  [GDT] {name} = 0x{v:X2}");
                    }
                    catch { }
                }
            }
            catch (Exception e) { L.LogError($"  GameDataTypes 反射失败: {e}"); }
        }
    }

    /// <summary>
    /// 盯着 AmongUsClient，把 GameId / ClientId / HostId 等只有进房后才有值的状态打出来。
    /// ★ 其中 GameId 是外置 bot 客户端构造 JoinGame 包唯一需要的参数。
    /// </summary>
    public class ClientWatcher : MonoBehaviour
    {
        public ClientWatcher(IntPtr ptr) : base(ptr) { }

        private float _t;
        private int _lastGameId = int.MinValue;
        private int _ticks;

        private void Update()
        {
            RawWatch.Tick(Time.deltaTime);   // 内部每 2 秒 flush 一次
            PlayerInfoWatch.Tick(Time.deltaTime);
            PlayerInfoWatch.TickPrefab(Time.deltaTime);
            PacketWatch.TickIdAndGrid();            // ★ 大厅加载后输出一次可行走网格

            _t += Time.deltaTime;
            if (_t < 2f) return;
            _t = 0f;
            // ★★★ 这里原来有一句「最多盯 2 分钟就自我禁用」：
            //         if (++_ticks > 60) { enabled = false; return; }
            //     它把**核心功能一起关掉了** ✗ ——
            //     因为同一个 Update 里还挂着 PacketWatch.TickIdAndGrid()（几何导出）。
            //     后果：游戏启动 2 分钟后组件自己关 → 进地图时没人重导几何
            //           → 人机在地图里用的还是**等待大厅的几何**
            //           → 直接从餐厅的桌子里穿过去（用户实测，且这是他从一开始就怀疑的）。
            //
            //     教训：诊断组件的「自动关闭」和功能代码放在同一个生命周期里时，
            //           关掉的就不只是诊断。功能必须与诊断分离。
            //     现在只保留计数（供日志用），**不再禁用组件**。

            try
            {
                var c = AmongUsClient.Instance;
                if (c == null) return;

                int gid = c.GameId;
                // GameId 变化时全量打印，否则静默
                if (gid != _lastGameId)
                {
                    _lastGameId = gid;
                    Plugin.L.LogInfo(
                        $"  [CLIENT] ★ GameId={gid} (0x{unchecked((uint)gid):X8})  " +
                        $"ClientId={c.ClientId}  HostId={c.HostId}  " +
                        $"NetworkMode={c.NetworkMode}  GameState={c.GameState}");
                }
            }
            catch (Exception e)
            {
                Plugin.L.LogError($"  [CLIENT] 读取失败: {e.Message}");
                enabled = false;
            }
        }
    }
}
