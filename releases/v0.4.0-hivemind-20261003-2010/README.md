# Smart Local v0.4.0 — "蜂群模式" 里程碑快照

## 这是什么
Among Us「智能本地」第四模式开发过程中的**第一个可玩里程碑**。

**历史意义**：首次实现「主菜单 → 本地创建游戏 → 满员假人注入 → 开局 →
场景切换 → 职业分配 → 进地图」全链路跑通。此前卡死的
`Timeout while waiting for other player data` 网络层超时被彻底绕开。

## 核心技术方案
放弃伪造 ClientData，改用游戏官方的「非网络玩家」设施：

    pc.isDummy = true;
    pc.PlayerId = (byte)GameData.Instance.GetAvailableId();
    GameData.Instance.AddDummy(pc);              // 不需要 ClientData
    AmongUsClient.Instance.Spawn(pc, auc.ClientId, SpawnFlags.None);

`AddDummy(PlayerControl)` 的签名里没有 ClientData 参数 —— 这是选择该路线的
决定性依据：dummy 玩家天然不进 allClients，服务端没有对象可等。

## 已知特性（Bug 即 Feature）
所有假人 `ownerId` 都是本机客户端，因此**共享本地输入**，
行动路线与玩家完全一致；且因为位置有偏移，撞墙时会各自停住，
呈现出「阴间又搞笑」的蜂群效果。**本版本刻意保留此行为。**

## 验证状态
- 大厅：15/15 满员 ✅
- 开局：过场动画正常，随机分配 1 名伪装者 ✅
- 地图：15 名玩家全部在场，任务/报告/紧急按钮正常 ✅
- 无断连、无插件报错 ✅
