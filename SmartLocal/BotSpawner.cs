using System;
using System.Collections;
using System.Collections.Generic;
using AmongUs.GameOptions;
using BepInEx.Unity.IL2CPP.Utils;
using InnerNet;
using UnityEngine;

namespace SmartLocal
{
    /// <summary>
    /// 阶段 4 冒烟测试驱动器：自适应地尝试两条假人生成路径，并**自我验证**。
    ///
    /// 设计理由：假人注入是全局最大技术未知点，靠猜没用，必须让程序自己告诉我们哪条路通。
    /// 因此这里不盲目循环 14 次，而是：
    ///   1) 先用路径 A 生成 1 个假人
    ///   2) 等 180 帧，对比 PlayerControl.AllPlayerControls.Count
    ///   3) 真的涨了 → 用路径 A 补齐剩下 13 个
    ///   4) 没涨 → 打详细诊断，改用路径 B 再试 1 个
    ///   5) 还是不行 → 把现场快照（客户端数/玩家数/座位数）全打进日志
    ///
    /// ⚠️ 协程里只用 `yield return null`（按帧等待），绝不用 WaitForSeconds：
    /// 托管 IEnumerator 经 Il2CppManagedEnumerator 包装后，Current 返回的是托管对象，
    /// Unity 原生协程调度器不认，会出未定义行为。按帧数等最稳。
    /// </summary>
    public class BotSpawner : MonoBehaviour
    {
        public BotSpawner(IntPtr ptr) : base(ptr) { }

        private bool _started;

        /// <summary>每帧等待的采样次数（60fps 下 180 帧 ≈ 3 秒）</summary>
        private const int WaitFrames = 180;

        public void Begin(int count)
        {
            if (_started)
            {
                Plugin.Logger.LogWarning("BotSpawner 已在运行，忽略重复调用。");
                return;
            }
            _started = true;
            Plugin.Logger.LogInfo($"[BotSpawner] 启动，目标假人数={count}");

            // 这是 BepInEx 提供的扩展方法：MonoBehaviourExtensions.StartCoroutine(self, IEnumerator)
            // 走它才能把托管协程交给 Unity 调度器。
            this.StartCoroutine(Run(count));
        }

        private IEnumerator Run(int count)
        {
            // ── 0. 等大厅彻底就绪（座位数组、GameData 都要在位）──
            for (int i = 0; i < 30; i++) yield return null;

            var lobby = LobbyBehaviour.Instance;
            if (lobby == null)
            {
                Plugin.Logger.LogError("[BotSpawner] LobbyBehaviour.Instance 为空，放弃注入。");
                yield break;
            }

            int seats = lobby.SpawnPositions != null ? lobby.SpawnPositions.Length : 0;
            DumpSnapshot("注入前", seats);

            // ── 架构分支：isDummy 路线 ──
            if (Plugin.UseDummyPath)
            {
                yield return RunDummyPath(count, seats);
                yield break;
            }

            // ── 路径 A：先造 1 个试试 ──
            bool pathAStarted = false;
            try
            {
                var cd = BotFactory.CreateClientData(0);
                AmongUsClient.Instance.GetOrCreateClient(cd);
                Plugin.Logger.LogInfo($"[路径A] ClientData 已登记 id={cd.Id} name={cd.PlayerName}，启动 CreatePlayer 协程");

                // CreatePlayer 返回 Il2CppSystem.Collections.IEnumerator，
                // 匹配 MonoBehaviour 的原生 StartCoroutine 重载。
                AmongUsClient.Instance.StartCoroutine(AmongUsClient.Instance.CreatePlayer(cd));
                pathAStarted = true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[路径A] 启动失败: {e}");
            }

            if (pathAStarted)
            {
                for (int i = 0; i < WaitFrames; i++) yield return null;

                if (CountPlayers() > _baseline)
                {
                    Plugin.Logger.LogInfo($"[路径A] ✓ 有效！玩家数 {_baseline} -> {CountPlayers()}，开始补齐剩余假人。");
                    yield return FillWithPathA(count - 1, seats);
                    yield break;
                }

                Plugin.Logger.LogWarning($"[路径A] ✗ 无效：等待 {WaitFrames} 帧后玩家数仍为 {CountPlayers()}（基线 {_baseline}）。改用路径 B。");
                DumpSnapshot("路径A失败后", seats);
            }

            // ── 2. 路径 B：直接实例化 + 网络生成 ──
            bool pathBOk = false;
            try
            {
                pathBOk = BotFactory.SpawnDirectDummy(0);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[路径B] 抛异常: {e}");
            }

            if (pathBOk)
            {
                for (int i = 0; i < WaitFrames; i++) yield return null;

                if (CountPlayers() > _baseline)
                {
                    Plugin.Logger.LogInfo($"[路径B] ✓ 有效！玩家数 {_baseline} -> {CountPlayers()}，开始补齐。");
                    for (int i = 1; i < count; i++)
                    {
                        try { BotFactory.SpawnDirectDummy(i); }
                        catch (Exception e) { Plugin.Logger.LogError($"[路径B] 第 {i} 个失败: {e}"); }
                        yield return null;
                    }
                    yield break;
                }
                Plugin.Logger.LogWarning($"[路径B] ✗ 无效：玩家数仍为 {CountPlayers()}。");
            }

            // ── 3. 两条路都不通，把现场全部打进日志供分析 ──
            DumpSnapshot("全部路径失败", seats);
            Plugin.Logger.LogError("[BotSpawner] 两条路径均未生效，注入失败。请把以上日志发给开发者。");
        }

        /// <summary>
        /// isDummy 路线：不注册任何客户端，直接用官方 dummy 设施造玩家。
        ///
        /// 与路径 A 的关键区别：这里**不调用 GetOrCreateClient**，
        /// 所以 allClients 里永远只有本机自己一个，服务端没有「其他玩家」可等，
        /// 从根上不会触发 "Timeout while waiting for other player data"。
        /// </summary>
        private IEnumerator RunDummyPath(int count, int seats)
        {
            int before = CountPlayers();
            int beforeGd = CountGameData();

            Plugin.Logger.LogInfo($"[假人路径] 开始生成 {count} 个 dummy 玩家（座位数={seats}）");

            int made = BotFactory.SpawnAllDummies(count);

            // 给引擎时间把这些对象真正落到场景里
            for (int f = 0; f < 120; f++) yield return null;

            int after = CountPlayers();
            int afterGd = CountGameData();

            Plugin.Logger.LogInfo(
                $"[假人路径] 结果：调用={made}  PlayerControl {before}->{after}  GameData {beforeGd}->{afterGd}");

            if (after <= before)
            {
                Plugin.Logger.LogError("[假人路径] PlayerControl 数量没有增加，生成失败。");
                BotFactory.DumpClientStates("假人路径失败后");
                yield break;
            }

            // 名字/外观修正（dummy 玩家的 ClientId 是 NoClientId，不走 FixupBotNames 的过滤条件）
            BotFactory.FixupDummyNames();

            // 再观察一段时间，确认它们稳定存在（而不是闪一下就被回收）
            for (int f = 0; f < 300; f++) yield return null;
            Plugin.Logger.LogInfo($"[假人路径] 5 秒后复查：PlayerControl={CountPlayers()}  GameData={CountGameData()}");
        }

        private int CountGameData()
        {
            try
            {
                var gd = GameData.Instance;
                return gd != null && gd.AllPlayers != null ? gd.AllPlayers.Count : -1;
            }
            catch { return -1; }
        }

        private IEnumerator FillWithPathA(int remaining, int seats)
        {
            // 座位数只作为提示，不再作为裁剪依据。
            // 原版支持 15 人局，而 LobbyBehaviour.SpawnPositions 在 Skeld 只有 10 个，
            // 说明超出部分由游戏自己处理（复用/重叠），我们不该替它做决定。
            if (seats > 0 && remaining + 1 > seats)
            {
                Plugin.Logger.LogWarning(
                    $"[路径A] 目标 {remaining + 1} 个假人 > 座位数 {seats}，超出的可能重叠。继续注入，不裁剪。");
            }

            int made = 1;
            for (int i = 1; i <= remaining; i++)
            {
                try
                {
                    var cd = BotFactory.CreateClientData(i);
                    AmongUsClient.Instance.GetOrCreateClient(cd);
                    AmongUsClient.Instance.StartCoroutine(AmongUsClient.Instance.CreatePlayer(cd));
                    made++;
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogError($"[路径A] 第 {i} 个失败: {e}");
                }
                // 每个之间隔几帧，避免同一帧大量生成导致原生侧状态竞争
                for (int f = 0; f < 5; f++) yield return null;
            }
            Plugin.Logger.LogInfo($"[路径A] 补齐完成，本批生成 {made} 个。");

            // 等所有人都在 GameData 里落地，再做名字修正与客户端报到
            for (int f = 0; f < 60; f++) yield return null;
            BotFactory.FixupBotNames();
            BotFactory.FixupBotClients(verbose: true);

            // 打开逐帧保活：挂在内网客户端 Update 的 Postfix 上，
            // 保证就绪标志在服务端每一次检查时都为真（30 帧一次的粗颗粒续期会被抢先）。
            SmartLocalState.KeepAlive = true;
            Plugin.Logger.LogInfo("[保活] 逐帧保活已开启。");
        }

        private int _baseline = -1;

        private int CountPlayers()
        {
            try
            {
                var list = PlayerControl.AllPlayerControls;
                return list != null ? list.Count : -1;
            }
            catch { return -1; }
        }

        private void DumpSnapshot(string tag, int seats)
        {
            _baseline = CountPlayers();
            try
            {
                var gd = GameData.Instance;
                int gdCount = gd != null && gd.AllPlayers != null ? gd.AllPlayers.Count : -1;
                var auc = AmongUsClient.Instance;
                int clients = auc != null && auc.allClients != null ? auc.allClients.Count : -1;

                Plugin.Logger.LogInfo(
                    $"[快照/{tag}] PlayerControl={_baseline}  GameData.AllPlayers={gdCount}  " +
                    $"allClients={clients}  座位={seats}  NetworkMode={(auc != null ? auc.NetworkMode.ToString() : "?")}  " +
                    $"HostId={(auc != null ? auc.HostId : -999)}");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[快照/{tag}] 采集失败: {e}");
            }
        }
    }
}
