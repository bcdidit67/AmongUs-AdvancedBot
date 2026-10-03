## v0.4.0-hivemind — 假人注入全链路打通（技术里程碑）

这是「智能本地」第四模式的**第一个可玩里程碑**。

### 本版达成

首次跑通完整链路，全部经实测验证：

**主菜单 → 本地创建游戏 → 满员假人注入 → 开局 → 场景切换 → 职业分配 → 进地图**

- ✅ 大厅自动注入 **14 个假人**（加玩家共 15 人满员）
- ✅ 假人**自动入座**（`PlayerPhysics.CoSpawnPlayer` 内部自动分配座位）
- ✅ **职业分配自动生效** —— 过场动画正常显示「我们当中有 N 个伪装者」
- ✅ 无断连、无报错、场景切换稳定

### 核心技术突破

**假人不再伪造 `ClientData`，改用游戏官方的 `isDummy` 设施。**

此前方案在大厅阶段完美工作，但一按「开始」就断连：

```
Server > Client DC because Error: Timeout while waiting for other player data
```

排查确证：把假人客户端的所有状态字段填满（`IsReady` / `InScene` / `Character` /
`ProductUserId` / `FriendCode`）后，超时**依然触发** —— 服务端等的不是
`ClientData` 的任何字段，而是只有真实网络连接才能产生的内部确认状态。

**决定性线索是 `AddDummy` 的签名本身：**

```csharp
NetworkedPlayerInfo AddPlayer(PlayerControl pc, ClientData client)  // 真实玩家，需要客户端
NetworkedPlayerInfo AddDummy (PlayerControl pc)                     // ★ 不需要客户端
```

dummy 玩家天然没有客户端、不进 `allClients`，服务端没有对象可等。

```csharp
var pc = UnityEngine.Object.Instantiate(AmongUsClient.Instance.PlayerPrefab);
pc.isDummy = true;
pc.PlayerId = (byte)GameData.Instance.GetAvailableId();
pc.SetName(name);
pc.SetColor(index % 12);

GameData.Instance.AddDummy(pc);                                   // 注册，无客户端
AmongUsClient.Instance.Spawn(pc, AmongUsClient.Instance.ClientId, // ownerId = 本机
                             SpawnFlags.None);
```

### 已知行为

**蜂群效应 —— 刻意保留。** 所有假人 `ownerId` 都是本机客户端，因此共享玩家输入：
行动路线与玩家完全一致，位置偏移又导致撞墙时各自停住。呈现出「14 个自己跟着你走、
还会卡在墙上」的效果。这是为绕开网络超时付出的设计代价，**不是随机 bug**，
而且挺有趣，故在本版保留。

其他限制：

- 假人数量**写死在代码里**（14 个），暂不读取「最大人数」设置
- **进任何本地大厅都会注入**，不区分入口
- 假人名字由游戏接管，显示为「玩家名 + 序号」
- 大厅座位仅 10 个，超出部分会重叠

### 安装

压缩包解压到游戏根目录（与 `Among Us.exe` 同级），最终结构：

```
Among Us/BepInEx/plugins/SmartLocal.dll
```

**Linux / Proton 必须设置 DLL 覆写**，否则不会加载：

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

原因：Wine 对 `winhttp` 这类系统 DLL 默认 **builtin 优先**（与 Windows 相反），
游戏根目录的 BepInEx 代理会被无视。详见包内 README。

### 前置条件

- Among Us（Steam 版，Unity 2022.3.44f1 / IL2CPP）
- BepInEx 6 bleeding edge **IL2CPP win-x64**，实测 `6.0.0-be.788`

### 完整报告

全部 API 真实签名与调研过程：仓库内 [`SmartLocal/REPORT.md`](https://github.com/bcdidit67/AmongUs-AdvancedBot/blob/main/SmartLocal/REPORT.md)

### 校验

```
SmartLocal-v0.4.0-hivemind.zip
SHA256: ef475fb932dd9d2b8b7c5fda2a6a6a4f587e44fd88e4c52c07be92bafe16c164
```

### 下一步

- [ ] **独立 AI**：`ownerId` 改为 `InnerNetClient.NoClientId`，用
      `PlayerPhysics.WalkPlayerTo(Vector2, float, float, bool)` 驱动寻路
- [ ] 启用主菜单「智能本地」按钮、隐藏聊天标签页
- [ ] 假人数量读取「最大人数」设置
