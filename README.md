# AmongUs-AdvancedBot

给 **Among Us**（Unity 2022.3.44f1 / IL2CPP / Steam）开发的 BepInEx 6 插件，
目标是在原版「本地 / 在线 / 练习模式」之外增加**第四种模式**：
**纯本地 AI 对局**——进入本地大厅时自动注入满员假人，无需联网、无需真实玩家。

---

## 当前状态

| 阶段 | 内容 | 状态 |
|---|---|---|
| 0.5 | API 签名调研（逆向 `Assembly-CSharp.dll`） | ✅ 完成，见 [`SmartLocal/REPORT.md`](SmartLocal/REPORT.md) |
| 1 | 主菜单新增「智能本地」按钮 | 🔧 代码就绪，默认关闭 |
| 2 | 隐藏「聊天」标签页 | 🔧 代码就绪 |
| 3 | 触发原版创建游戏流程 | 🔧 代码就绪 |
| 4 | 假人注入 | ✅ **实测通过** |
| 5 | 按最大人数满员注入 | ✅ **实测通过** |
| 6 | 职业分配 | ✅ **实测通过**（走原版 `RoleManager.SelectRoles()` 自动分配） |
| 7 | BotBrain AI | ⬜ 未开始 |

**里程碑 `v0.4.0-hivemind`**：首次跑通「主菜单 → 本地创建游戏 → 满员假人 → 开局 →
场景切换 → 职业分配 → 进地图」全链路，无断连、无报错。

---

## 核心技术方案

### 假人用 `isDummy`，不要伪造 `ClientData`

这是本项目最重要的一个坑。最初的做法是伪造 `ClientData` 并用
`AmongUsClient.CreatePlayer()` 生成假人 —— **大厅阶段完美工作**（15/15 满员、
名字正常、自动入座），但一按「开始」就断连：

```
Server > Client DC because Error: Timeout while waiting for other player data
```

排查确认：伪造的客户端**无论怎么填字段都过不了**。实测把它们全部置为
`IsReady=true` / `InScene=true` / `Character!=null` / `Puid`、`FriendCode` 非空之后，
超时**依然触发** —— 说明服务端等的不是 `ClientData` 的任何字段，而是只有真实网络连接
才能产生的内部确认状态。

**决定性线索是 `AddDummy` 的签名本身：**

```csharp
NetworkedPlayerInfo AddPlayer(PlayerControl pc, ClientData client)  // 真实玩家，需要客户端
NetworkedPlayerInfo AddDummy (PlayerControl pc)                     // ★ 不需要客户端
```

`AddDummy` 不接受 `ClientData` —— 说明 dummy 玩家天然没有客户端、不进 `allClients`，
服务端**没有对象可等**。这不是绕开超时，而是从根上不存在这个问题。

最终方案：

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

### 已知特性：蜂群行为

因为所有假人的 `ownerId` 都是本机客户端，它们**共享本地输入**，
行动路线与玩家完全一致，位置偏移又导致撞墙时各自停住 —— 呈现出「阴间又搞笑」的蜂群效果。

**本版本刻意保留此行为**作为彩蛋。要做真正独立的 AI，需要把 `ownerId` 改为
`InnerNetClient.NoClientId` 并自行驱动移动
（`PlayerPhysics.WalkPlayerTo(Vector2, float, float, bool)` 是现成的寻路到点接口）。

---

## 仓库结构

```
SmartLocal/            BepInEx 插件本体（核心）
├── Plugin.cs                   入口 + 全局状态 + 阶段开关
├── BotFactory.cs               假人构造 / 报到 / 改名 / 职业分配
├── BotSpawner.cs               自适应注入驱动器（含自诊断）
├── BotBrain.cs                 阶段 7：AI 载体骨架
├── LobbyBehaviourStartPatch.cs 注入触发点
├── MainMenuButtonPatch.cs      阶段 1：主菜单按钮
├── CreateGameOptionsPatch.cs   阶段 2：隐藏聊天标签页
├── DisconnectDiagnosticPatch.cs 断连瞬间现场快照（调试利器）
├── ClientKeepAlivePatch.cs     逐帧保活 + 状态采样
└── REPORT.md                   ★ 完整 API 反向工程报告

Il2CppInspector/       签名检查器：基于 Mono.Cecil 的纯元数据读取工具
                       可在不解析依赖、不需要运行时的前提下 dump
                       il2cpp interop 程序集的完整类型/方法签名
EnvCheck/              BepInEx 环境冒烟测试插件
releases/              里程碑存档说明
```

### 关于 `Il2CppInspector`

写 Among Us 的 IL2CPP mod 时非常有用。il2cpp interop 程序集里**所有方法体都是 stub**
（只做 `il2cpp_runtime_invoke` 转发到 native `GameAssembly.dll`），**IL 中没有真实逻辑**，
所以无法靠反编译追内部流程 —— 但**签名是完整可信的**，这个工具就是用来快速查签名的：

```bash
il2cpp-inspect <dll|目录> find   <正则>   # 按类型全名搜
il2cpp-inspect <dll|目录> type   <正则>   # 完整 dump 类型
il2cpp-inspect <dll|目录> member <正则>   # 搜成员
il2cpp-inspect <dll|目录> names           # 只列类型名
```

---

## 构建

前置：**.NET 6 SDK**（BepInEx 6 IL2CPP 自带的是 .NET 6.0.7 运行时，插件必须靶向 `net6.0`）。

```bash
cd SmartLocal
dotnet build -c Release
cp bin/Release/net6.0/SmartLocal.dll \
   "$HOME/.local/share/Steam/steamapps/common/Among Us/BepInEx/plugins/"
```

`GameDir` 可用属性覆盖，默认 `$(HOME)/.local/share/Steam/steamapps/common/Among Us`：

```bash
dotnet build -c Release -p:GameDir="/path/to/Among Us"
```

---

## Linux / Proton 部署要点

**在 Linux 上，仅仅把 `winhttp.dll` 解压到游戏根目录是没用的。**

Windows 的 DLL 搜索顺序是「应用程序目录优先于 System32」，所以 Doorstop 的
`winhttp.dll` 代理能压过系统版本、自动完成注入，**不需要任何启动参数**。

但 **Wine 故意反着来**：对 `winhttp` 这类它自己实现了的系统 DLL，默认加载顺序是
**builtin 优先、native 其次**，游戏根目录那份会被无视。所以必须设置 DLL 覆写：

**方式一：Steam 启动项**（推荐，换 Proton / 重建 prefix 都不丢）

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

**方式二：Wine 前缀注册表覆写**（无需启动参数，per-game）

写入 `compatdata/945360/pfx/user.reg` 的 `[Software\\Wine\\DllOverrides]` 节：

```
"winhttp"="native,builtin"
```

> ⚠️ **不推荐**用 Proton 的 `user_settings.py` —— 它没有 per-game 键，
> 会对该 Proton 版本下的**所有游戏**生效，可能搞坏别的游戏。

---

## 路线图

- [ ] **阶段 7**：BotBrain —— 独立移动（改 `ownerId`）、感知、决策、任务/击杀行为
- [ ] 主菜单「智能本地」按钮启用（阶段 1 代码已在，默认 `EnableMainMenuButton = false`）
- [ ] 假人自定义命名（目前由游戏接管为「本机玩家名 + 序号」）
- [ ] 蜂群模式保留为可切换的彩蛋

---

## 免责声明

本项目仅供**纯本地单机**使用，不涉及联机对战、不修改任何在线服务数据。
请勿用于任何形式的联机作弊。
