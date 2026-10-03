using System;
using UnityEngine;

namespace SmartLocal
{
    /// <summary>
    /// 阶段 7：AI 逻辑载体。
    ///
    /// 【确证】游戏内完全没有 Bot 设施 —— 全程序集搜 'Bot' 只命中
    /// HeliSabotage / Mushroom 等无关子串，没有任何 Bot / AI 类型。
    /// 因此跟感知、寻路、决策、任务执行相关的一切都必须 100% 自研。
    ///
    /// 注意：Among Us 的地图寻路依赖 UnityEngine.AI.NavMeshAgent（船员）与
    /// 自定义的通风管网络（内鬼），两者都不在本次调研范围内，
    /// 属于阶段 7 的独立课题。
    /// </summary>
    public class BotBrain : MonoBehaviour
    {
        public BotBrain(IntPtr ptr) : base(ptr) { }

        /// <summary>绑定的玩家对象</summary>
        private PlayerControl _player;

        /// <summary>所属职业（开局后由 RoleManager 写入）</summary>
        private AmongUs.GameOptions.RoleTypes _role;

        /// <summary>行为节拍，避免每帧决策</summary>
        private float _tickTimer;
        private const float TickInterval = 0.5f;

        public void Bind(PlayerControl player)
        {
            _player = player;
            Plugin.Logger.LogInfo($"BotBrain 绑定到 {player?.name}");
        }

        private void Update()
        {
            if (_player == null) return;

            _tickTimer += Time.deltaTime;
            if (_tickTimer < TickInterval) return;
            _tickTimer = 0f;

            try
            {
                Tick();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"BotBrain tick 异常: {e}");
            }
        }

        private void Tick()
        {
            // TODO(阶段 7)：AI 决策主循环。
            // 规划中的分层：
            //   1) 感知层：读 GameData.Instance.AllPlayers / ShipStatus，
            //              判断可见敌人、任务点、尸体、紧急按钮
            //   2) 决策层：船员 -> 跑任务 / 修破坏；内鬼 -> 击杀 / 破坏 / 伪装
            //   3) 执行层：PlayerPhysics.WalkPlayerTo(Vector2, float, float, bool)
            //              或直接驱动 NetTransform
            //
            // 【确证可用】PlayerPhysics:
            //   public IEnumerator WalkPlayerTo(Vector2 worldPos, float tolerance,
            //                                   float speedMul, bool ignoreColliderOffset)
            // 这是现成的寻路到点接口，执行层可以直接用它。
            _ = _role;
        }
    }
}
