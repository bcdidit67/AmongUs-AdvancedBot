# Smart Local — Among Us 第四模式（v0.4.0-hivemind）

在 Among Us 原版「本地 / 在线 / 练习模式」之外增加第四种模式：**纯本地 AI 对局**。
进入本地大厅时自动注入满员假人，无需联网、无需真实玩家。

> **这是技术里程碑版本，不是成品。** 假人目前呈现「蜂群」行为（详见下文），
> 核心价值在于证明了「满员假人 + 场景切换 + 职业分配」整条链路在 Among Us
> IL2CPP 版上完全可行。

---

## 前置条件

| 项 | 要求 |
|---|---|
| 游戏 | Among Us（Steam 版，Unity 2022.3.44f1 / IL2CPP） |
| 插件框架 | **BepInEx 6 bleeding edge，IL2CPP win-x64** |
| 实测版本 | BepInEx `6.0.0-be.788`（commit `5b766a3`） |

尚未安装 BepInEx 的话：从 <https://builds.bepinex.dev/projects/bepinex_be> 下载
`BepInEx-Unity.IL2CPP-win-x64-*.zip`，解压到游戏根目录，先启动一次游戏让
`BepInEx/interop/` 生成完毕。

---

## 安装

把压缩包内的 **`BepInEx` 文件夹解压到游戏根目录**（与 `Among Us.exe` 同级），
最终结构应为：

```
Among Us/
├── Among Us.exe
├── BepInEx/
│   └── plugins/
│       └── SmartLocal.dll        ← 本插件
└── ...
```

### Windows

直接启动游戏即可，无需任何额外设置。

### Linux / Proton

**必须设置 DLL 覆写**，否则游戏不会加载 BepInEx（详见下方「为什么」）。

**方式一 · Steam 启动项**（推荐）

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

**方式二 · Wine 前缀注册表**（无需启动参数）

在 `compatdata/945360/pfx/user.reg` 的 `[Software\\Wine\\DllOverrides]` 节内加入：

```
"winhttp"="native,builtin"
```

> **为什么需要**：Windows 的 DLL 搜索顺序是「应用程序目录优先于 System32」，
> 所以 BepInEx 放在游戏根目录的 `winhttp.dll` 代理能自动劫持注入。
> 但 **Wine 刻意反着来** —— 对 `winhttp` 这类它自己实现的系统 DLL 默认
> **builtin 优先、native 其次**，游戏根目录那份会被无视。
>
> ⚠️ 不要用 Proton 的 `user_settings.py`：它没有 per-game 键，
> 会对该 Proton 版本下的**所有游戏**生效。

---

## 使用

1. 启动游戏
2. 主菜单点 **「本地」**（原版按钮）
3. 随便选地图，点 **「创建游戏」**
4. 进大厅 —— 约 0.5 秒后自动注入 **14 个假人**（加你自己共 15 人满员）
5. 点 **「开始」** —— 过场动画、职业分配、进地图全部正常

---

## 已知行为（重要）

### 蜂群效应 —— 本版本刻意保留

所有假人的 `ownerId` 都是**本机客户端**，因此它们**共享你的输入**：
行动路线和你完全一致，又因为各自初始位置不同，撞到墙时会各自停住。
效果是「14 个自己跟着你走，还会卡在墙上」。

这是为绕开内网超时机制付出的设计代价，**不是随机 bug**，而且挺好玩，
所以在这个版本里刻意保留了。真正的独立 AI 在下一阶段（见路线图）。

### 其他限制

- **假人数量写死在代码里**（14 个），暂时不读取「最大人数」设置
- **只要进任何本地大厅就会注入**，不区分是否从「智能本地」按钮进入
  （主菜单按钮代码已实现，但默认关闭）
- 假人名字由游戏接管，显示为 **「你的玩家名 + 序号」**（如 `Fellpillow 1`）
- 大厅座位只有 10 个（`SpawnPositions` 长度），超出部分会重叠

---

## 卸载

删除 `BepInEx/plugins/SmartLocal.dll`。
若在 Linux 上用了注册表覆写且想彻底还原，把 `user.reg` 里那行 `"winhttp"` 删掉。

---

## 技术要点

核心难点在于**如何让假人既存在于游戏逻辑里、又不被网络层检查**。

最初方案是伪造 `ClientData` 并用 `AmongUsClient.CreatePlayer()` 生成假人 ——
大厅阶段完美（15/15 满员、名字正常、自动入座），但**一按开始就断连**：

```
Server > Client DC because Error: Timeout while waiting for other player data
```

实测把假人客户端的所有状态字段全部填满（`IsReady` / `InScene` / `Character` /
`ProductUserId` / `FriendCode`）之后，超时**依然触发** —— 说明服务端等的不是
`ClientData` 的任何字段，而是只有真实网络连接才能产生的内部确认状态。

**决定性线索是 `AddDummy` 的签名本身：**

```csharp
NetworkedPlayerInfo AddPlayer(PlayerControl pc, ClientData client)  // 真实玩家，需要客户端
NetworkedPlayerInfo AddDummy (PlayerControl pc)                     // ★ 不需要客户端
```

`AddDummy` 不接受 `ClientData` —— dummy 玩家天然没有客户端、不进 `allClients`，
服务端没有对象可等。最终方案：

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

完整调研报告（含全部 API 真实签名）：见仓库 `SmartLocal/REPORT.md`。

---

## 路线图

- [ ] **独立 AI**：把 `ownerId` 改为 `InnerNetClient.NoClientId`，
      用 `PlayerPhysics.WalkPlayerTo(Vector2, float, float, bool)` 驱动寻路
- [ ] 启用主菜单「智能本地」按钮、隐藏聊天标签页
- [ ] 假人数量读取「最大人数」设置
- [ ] 自定义假人命名

---

## 许可证

[MIT](https://github.com/bcdidit67/AmongUs-AdvancedBot/blob/main/LICENSE) © 2026 bcdidit67

Among Us 是 Innersloth LLC 的商标与版权作品。本项目为独立的第三方 Mod，
与 Innersloth 无任何关联，亦未获得其背书。本压缩包不包含任何游戏原始资源。

本项目仅供**纯本地单机**使用，不涉及联机对战、不修改任何在线服务数据。
