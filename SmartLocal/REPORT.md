# Among Us「智能本地」第四模式 — 阶段 0.5 技术可行性报告

调研对象：`BepInEx/interop/Assembly-CSharp.dll`（Among Us 2026-10-03 构建，Unity 2022.3.44f1，IL2CPP）
调研工具：自建 `Il2CppInspector`（基于 Mono.Cecil 的纯元数据读取器，位于 `~/AmongUsMods/Il2CppInspector`）

---

## 0. 方法与一个必须先说清楚的限制

**所有签名均为从程序集元数据直接读出的确证结果。** 但有一个关键限制：

> il2cpp interop 程序集里**所有方法体都是 stub**（body 只做 `il2cpp_runtime_invoke` 转发到 native `GameAssembly.dll`），
> **IL 中不含任何真实游戏逻辑**。

因此**无法通过反编译这些程序集来追踪内部调用流程**。报告中凡涉及"流程"的部分，我都标注了
`【确证】`（来自签名/字段关系）与 `【推断】`（来自命名与类型关系，需运行期验证）。

如果需要真实方法体，方案是跑 Cpp2IL（BepInEx 自带 `Cpp2IL.Core.dll` + `LibCpp2IL.dll`）对
`GameAssembly.dll` + `global-metadata.dat` 做完整反编译，产出带真实 IL 的程序集。
**这是阶段 1 之前值得做的一件事**，成本约几十分钟，收益是能直接看懂原版流程。

---

## 1. 【重大修正】需求中的两个前提是错的

### 1.1「本地 / 在线 / 练习模式」不是 `GameModes`

```csharp
// 【确证】全局命名空间
public enum NetworkModes {
    LocalGame,      // 本地
    OnlineGame,     // 在线
    FreePlay,       // 练习模式
}

// 【确证】AmongUs.GameOptions
public enum AmongUs.GameOptions.GameModes {
    None, Normal, HideNSeek, NormalFools, SeekFools
}
```

你要加的「第四模式」在语义上对应 **`NetworkModes`** 这一层（LocalGame / OnlineGame / FreePlay），
而不是 `GameModes`（那是个位服务器/Puff 的玩法变体）。

**这意味着一个关键决策**：`NetworkModes` 是编译期枚举，IL2CPP 下无法在运行期给它加新值。
即使强转 `(NetworkModes)3`，原版所有 `switch (NetworkMode)` 都会走 default 分支。

> **建议**：**不要新增枚举值**。复用 `NetworkModes.LocalGame`，用一个 Mod 自己的静态标志位
> （如 `SmartLocalState.IsSmartLocalGame`）来区分"这是 AI 对局"。这样能 100% 复用原版本地游戏的全部
> 合法流程（房间创建、场景加载、开局校验），只在需要注入 AI 的地方挂钩子。风险最低。

### 1.2 `AmongUsClient` 没有 `CreateGame()` 方法

【确证】`AmongUsClient` 全部方法已 dump，**不存在** `CreateGame` / `CreateLocalGame` / `CreateOnlineGame`
（只有 `CoCreateOnlineGame()` 用于在线）。全程序集搜索 `CreateGame*` 只命中：

```
GameManagerCreator::public static GameManager CreateGameManager(AmongUs.GameOptions.GameModes mode)   // 静态，造 GameManager
PSManager::public Sys.Void CreateGame(AmongUs.GameOptions.GameModes gameMode)                        // PlayStation 专用，不可用
PSManager::public Il2CppSystem.Collections.IEnumerator CreateGameCo(AmongUs.GameOptions.GameModes)   // 同上
InnerNet.GameCode::public static Sys.Int32 CreateGameId(Sys.Int32 sn, Sys.Int32 gn)                  // 只生成房间号
```

真正的入口是**回调**，不是主动创建：

```csharp
// 【确证】InnerNet.InnerNetClient（AmongUsClient 的基类）
public virtual Sys.Void OnGameCreated(Sys.String gameIdString)
public virtual Sys.Void OnGameJoined(Sys.String gameIdString)
public virtual Sys.Void OnWaitForHost(Sys.String gameIdString)
public Sys.Void Connect(InnerNet.MatchMakerModes mode, Sys.String matchmakerToken)
public Il2CppSystem.Collections.IEnumerator CoConnect(InnerNet.MatchMakerModes mode, Sys.String matchmakerToken)
public Sys.Void SendStartGame()
```

**结论**：不要试图自己"创建游戏"。正确做法是**触发原版的创建流程**（`CreateGameOptions.Confirm()`），
然后挂 `OnGameCreated` / 大厅加载 的回调注入假人。

---

## 2. 逐项回答你的四个问题

### Q1. 原版按钮如何打开 `CreateGameOptions`？

**【确证】`MainMenuManager`**（`public class MainMenuManager : UE.MonoBehaviour`）

⚠️ **它不是单例**——属性表里**没有 `Instance`**。这点很关键，后面骨架里要用
`UnityEngine.Object.FindObjectOfType<MainMenuManager>()` 拿实例。

相关字段：

```csharp
PassiveButton playLocalButton       // 「本地」
PassiveButton freePlayButton        // 「练习模式」
PassiveButton playButton            // 在线主按钮
PassiveButton createGameButton      // 「创建游戏」
PassiveButton findGameButton        // FindGameButton 类型
CreateGameOptions createGameScreen  // ★ 界面实例引用（同一 MainMenu 场景里）
UE.GameObject gameModeButtons, onlineButtons, accountButtons, enterCodeButtons, mainMenuUI
Il2CppSystem.Collections.Generic.List<PassiveButton> mainButtons
```

相关方法：

```csharp
public Sys.Void OpenCreateGame()                       // ★ 打开创建游戏界面
public Il2CppSystem.Collections.IEnumerator ShowCreateGameCo()   // 带滑入动画的协程
public Sys.Void GoBackCreateGame()
public Il2CppSystem.Collections.IEnumerator GoBackCreateGameCo()
public Sys.Void OpenGameModeMenu()                     // 打开模式选择
public Sys.Void OpenOnlineMenu()
public Sys.Void OpenFindGame()
public Sys.Void ResetScreen()
public Sys.Void ConnectMainMenuScreenButtonEvents()    // ★ 按钮事件绑定处
public Sys.Boolean IsMainMenuActive()
public Sys.Void ActivateMainMenuUI() / DeactivateMainMenuUI()
```

【推断】原版链路：`playLocalButton.OnClick` 绑定到 `OpenCreateGame()` →
走 `ShowCreateGameCo()` 协程（有 `TIME_SLIDE` / `X_OFFSCREEN_LEFT/RIGHT` 等常量，说明是横向滑入动画）。

**复用方案**：我们的新按钮点击时直接调 `mainMenuManager.OpenCreateGame()`。
**不要**自己 `createGameScreen.Show()`——那会跳过界面状态机（`ResetScreen`、按钮禁用列表
`disableOnStartup` 等），可能导致返回时 UI 状态错乱。

按钮本体是 `PassiveButton`：

```csharp
// 【确证】public class PassiveButton : PassiveUiElement
public ButtonClickedEvent OnClick { get; set; }     // UnityEvent 风格
public TMPro.TextMeshPro buttonText { get; set; }
public UE.GameObject selectedSprites, activeSprites, inactiveSprites, disabledSprites, onClickSprites;
public UE.AudioClip ClickSound, HoverSound;
```

> **建议做法**：`Instantiate(playLocalButton.gameObject, playLocalButton.transform.parent)`
> 克隆一个现成按钮，改文字、改位置、清空 `OnClick` 并重新 `AddListener`。
> 这样能自动继承原版的 sprite、悬停效果、音效、控制器导航，比从零拼一个 `PassiveButton` 靠谱得多。

---

### Q2. `CreateGameOptions` 里怎么拿最大人数？怎么隐藏聊天标签页？

**【确证】`public class CreateGameOptions : UE.MonoBehaviour`**

#### 2.1 最大人数 — 三层来源

```csharp
// A. 设置对象（数据源）
IntGameSetting capacitySetting;      // : BaseGameSetting，含 ValidRange (IntRange)、Value (Int32)、Increment
NumberOption   capacityOption;       // : OptionBehaviour，UI 控件
                                     //   含 Value (Single)、ValidRange (FloatRange)、Increment
                                     //   方法：Increase() / Decrease() / GetInt() / GetFloat() / UpdateValue()

// B. 界面读数
public Sys.Single GetCapacity();     // ★ 读当前容量
public Sys.Void SetCrewmateGraphic(Sys.Single capacity);   // 按容量更新小人图形

// C. UI 按钮
PassiveButton minusButtonCapacity, plusButtonCapacity;

// D. ★ 权威数据源（全局游戏选项）
// AmongUs.GameOptions.IGameOptions
public virtual Sys.Int32 MaxPlayers   { get; set; }
public virtual Sys.Int32 NumImpostors { get; set; }
```

> **推荐**：**以 `GameOptionsManager.Instance.CurrentGameOptions.MaxPlayers` 为准**。
> `GetCapacity()` 和 `capacityOption.Value` 只是界面层的当前显示值，
> 而 `CurrentGameOptions.MaxPlayers` 才是真正会随 `Confirm()` 落到大厅、并决定
> `GameStartManager` 校验逻辑的那份数据。假人数量必须以它为准，否则会出现
> "界面显示 15 人、实际开局 10 人"的不一致。

`GameOptionsManager`：

```csharp
// 【确证】public class GameOptionsManager
public static GameOptionsManager Instance { get; set; }
AmongUs.GameOptions.IGameOptions CurrentGameOptions { get; set; }
AmongUs.GameOptions.IGameOptions GameHostOptions    { get; set; }
AmongUs.GameOptions.GameModes currentGameMode       { get; set; }
public Sys.Void SwitchGameMode(AmongUs.GameOptions.GameModes gameMode)
public Sys.Void LoadOrCreateNormalGameHostOptions()
```

#### 2.2 聊天标签页

```csharp
// 【确证】标签页结构
Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<PassiveButton> tabButtons;
Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UE.GameObject> contentObjects;

public Sys.Void OpenTab(Sys.Boolean isGeneral);   // ★ 参数是 bool，说明原版只有两个标签
Sys.Int32 currentTag;

// 聊天专属成员（可用来交叉定位）
Il2CppReferenceArray<PassiveButton> chatTypeButtons;
FilterOptionUI chatOptionUI;
TMPro.TextMeshPro chatDescText, chatWarningText;
public Sys.Void CheckChatType();
public Sys.Void SetChatType(Sys.Boolean isFreeChat);
```

【确证】`OpenTab` 只接受一个 `bool isGeneral` —— **原版就是 General / Chat 两个标签**，
所以 `tabButtons[0]` = General、`tabButtons[1]` = Chat。`contentObjects` 与之下标平行。

**隐藏方案与两个坑**：

```csharp
tabButtons[1].gameObject.SetActive(false);
contentObjects[1].SetActive(false);
```

- **坑 1**：必须同时把 `currentTag` 归位，并在返回/重进时**再次**隐藏。因为 `ValueChanged()` /
  `OpenTab()` 会按 `currentTag` 重新激活对应 `contentObjects`。稳妥做法是 Hook `OpenTab`，
  当 `isGeneral == false` 时直接 `return`（或强制 `isGeneral = true`），从源头掐断。
- **坑 2**：`tabButtons` / `contentObjects` 是**定长数组**，隐藏下标 1 后 0 号仍占原位，
  视觉上会留下一块空档。若要更干净，可克隆 General 标签按钮并重新布局，但这会牵动
  `SetUpLangButtonsNav()` 的控制器导航，**建议阶段 1 先只做隐藏**，布局微调放后面。

---

### Q3. 本地模式下 `CreateGame()` 如何触发 `LobbyBehaviour` 加载？

**再次强调：没有 `AmongUsClient.CreateGame()`。** 下面是【推断】的完整链路（每条都有签名佐证）：

```csharp
// 1) 用户在 CreateGameOptions 点「创建游戏」
public Sys.Void CreateGameOptions.Confirm()
public Il2CppSystem.Collections.IEnumerator CreateGameOptions.ContinueCoStart()
public Sys.Void CreateGameOptions.ContinueStart()
public Sys.Void CreateGameOptions.CoStartGame()          // 注意：返回 Void，不是协程
public Sys.Void CreateGameOptions.NotOnlinePermissions()

// 2) 写入网络模式 / 发起连接
//    NetworkModes.LocalGame 在这里被使用
public Sys.Void InnerNet.InnerNetClient.Connect(InnerNet.MatchMakerModes mode, Sys.String matchmakerToken)
// 3) 房间号生成（本地模式也会生成一个 GameId）
public static Sys.Int32 InnerNet.GameCode.CreateGameId(Sys.Int32 sn, Sys.Int32 gn)
// 4) ★ 创建完成回调 —— 这是最可靠的注入锚点
public virtual Sys.Void AmongUsClient.OnGameCreated(Sys.String gameIdString)   // 重写了基类
// 5) GameManager 生成
public static GameManager GameManagerCreator.CreateGameManager(AmongUs.GameOptions.GameModes mode)
// 6) 开始游戏
public Sys.Void AmongUsClient.StartGame()
public Il2CppSystem.Collections.IEnumerator AmongUsClient.CoStartGameHost()
// 7) 场景切换事件（大厅场景加载）
public static Sys.Void AmongUsClient.OnActiveSceneChange(UE.SceneManagement.Scene from, UE.SceneManagement.Scene to)
```

大厅预置体在 `GameStartManager` 上：

```csharp
// 【确证】public class GameStartManager : DestroyableSingleton<GameStartManager>
LobbyBehaviour LobbyPrefab { get; set; }     // ★ 大厅预置体
Sys.Int32 MinPlayers { get; set; }           // ★ 开局最小人数校验
public Sys.Void BeginGame()
public Sys.Void ReallyBegin(Sys.Boolean neverShow)
public Sys.Void FinallyBegin()
public Sys.Void DoHostSetup()
StartingStates startState { get; set; }
```

而 `LobbyBehaviour`：

```csharp
// 【确证】public class LobbyBehaviour : InnerNet.InnerNetObject
public static LobbyBehaviour Instance { get; set; }
Il2CppStructArray<UE.Vector2> SpawnPositions { get; set; }   // ★ 座位坐标数组
public Sys.Void Start()
public Sys.Void Update()
public Sys.Void FixedUpdate()
```

> **注入锚点选择建议**：优先挂 **`LobbyBehaviour.Start()`**（大厅刚就绪，座位数组已可用）
> 或 **`AmongUsClient.OnGameCreated`**。不建议挂 `CreateGameOptions.Confirm`——
> 那时大厅对象还没实例化，`SpawnPositions` 拿不到。

---

### Q4. 怎么循环生成 `PlayerControl` 假人并强制入座？

这是**好消息最多的一段**——游戏原生就有 dummy 概念。

#### 4.1 ★ 游戏自带假人设施（需求里没预料到的）

```csharp
// 【确证】PlayerControl
public Sys.Boolean isDummy { get; set; }          // ★ 假人标志位
Sys.Byte PlayerId { get; set; }
NetworkedPlayerInfo CachedPlayerData { get; set; }
PlayerPhysics MyPhysics { get; }
CustomNetworkTransform NetTransform { get; }
Il2CppSystem.Collections.Generic.List<PlayerControl> AllPlayerControls { static; }   // ★ 全玩家列表
PlayerControl LocalPlayer { static; }

// 【确证】GameData
public NetworkedPlayerInfo AddDummy(PlayerControl pc)                      // ★ 注册假人
public NetworkedPlayerInfo AddPlayer(PlayerControl pc, ClientData client)
public Sys.SByte GetAvailableId()
Il2CppSystem.Collections.Generic.List<NetworkedPlayerInfo> AllPlayers { get; set; }
public static GameData Instance { get; set; }

// 【确证】Constants
public static Sys.String DummyNamePrefix { get; set; }    // ★ 假人名字前缀
```

`AddDummy(PlayerControl)` 的存在说明**官方自己会构造 dummy 玩家**（很可能用于商店/装扮预览，
`SaveIconCamera.saveIconDummy` 也是一处佐证）。**优先复用这套机制，而不是自己伪造 `ClientData`。**

#### 4.2 生成玩家与入座

```csharp
// 【确证】AmongUsClient
public Il2CppSystem.Collections.IEnumerator CreatePlayer(InnerNet.ClientData clientData)   // ★ 生成玩家
public override Sys.Void OnPlayerJoined(InnerNet.ClientData data)
public Il2CppSystem.Collections.IEnumerator CoOnPlayerChangedScene(InnerNet.ClientData client, Sys.String currentScene)

// 【确证】InnerNet.ClientData（构造函数是 public）
public Sys.Void .ctor(Sys.Int32 id, Sys.String playerName, PlatformSpecificData platformData,
                      Sys.UInt32 playerLevel, Sys.String productUserId, Sys.String friendCode)
Sys.Int32 Id; Sys.Boolean InScene, IsReady, IsBeingCreated;
PlayerControl Character; Sys.String PlayerName; Sys.Int32 ColorId; Sys.UInt32 PlayerLevel;

// 【确证】PlatformSpecificData（构造 ClientData 的必需品）
public Platforms Platform { get; set; }
public Sys.String PlatformName { get; set; }
public Sys.UInt64 XboxPlatformId, PsnPlatformId;

// 【确证】NetworkedPlayerInfo
public Sys.Void Init(PlayerControl pc, Sys.Int32 clientId)
Sys.Byte PlayerId; Sys.Int32 ClientId;
AmongUs.GameOptions.RoleTypes RoleType { get; set; }
Il2CppSystem.Collections.Generic.Dictionary<PlayerOutfitType, PlayerOutfit> Outfits;
PlayerControl Object { get; }
Sys.String PlayerName { get; set; }
```

#### 4.3 ★ 入座是自动的

```csharp
// 【确证】PlayerPhysics
public Il2CppSystem.Collections.IEnumerator CoSpawnPlayer(LobbyBehaviour lobby)   // ★ 入座入口
public static Sys.Boolean get_isDummy() / 见 PlayerControl
// 【确证】内部状态机
PlayerPhysics/_CoSpawnPlayer_d__42 : Sys.Int32 _spawnSeatId_5__2     // 座位号是协程内部局部变量
```

**关键推论**：`_spawnSeatId` 是 `CoSpawnPlayer` 协程的**内部局部变量**，不由外部传入——
说明**座位号是协程内部根据 `PlayerId` 自动算出来的**（大概率是 `PlayerId % SpawnPositions.Length`）。

> **这对我们极其有利**：只要假人走正常的 `CreatePlayer` → `CoSpawnPlayer(lobby)` 流程，
> **入座是自动的**，不需要我们手动计算坐标。需求里"让它们坐进大厅座椅上"这一条
> **不需要额外实现**。

#### 4.4 职业分配

```csharp
// 【确证】public class RoleManager : DestroyableSingleton<RoleManager>
public Sys.Void SetRole(PlayerControl targetPlayer, AmongUs.GameOptions.RoleTypes roleType)   // ★ 强制指定
public Sys.Void SelectRoles()                                                                // ★ 原生随机分配
public RoleBehaviour GetRole(AmongUs.GameOptions.RoleTypes roleType)
Il2CppSystem.Collections.Generic.List<RoleBehaviour> AllRoles { get; set; }
public static Sys.Boolean IsImpostorRole(AmongUs.GameOptions.RoleTypes roleType)
public static Sys.Void TryAssignSpecialGhostRoles(PlayerControl player, Sys.Boolean impostorRoles)

// 【确证】PlayerControl 侧的职业接口
public Il2CppSystem.Collections.IEnumerator CoSetRole(AmongUs.GameOptions.RoleTypes role, Sys.Boolean canOverride)
public Sys.Void RpcSetRole(AmongUs.GameOptions.RoleTypes roleType, Sys.Boolean canOverrideRole)
```

需求里说"假人通过 RoleManager 正常分配职业"——**这个可以做到，而且有两条路**：

- **路线 A（推荐）**：假人注册进 `GameData.Instance.AllPlayers` 后，直接调
  `RoleManager.Instance.SelectRoles()`。原版算法会把它当成真玩家一起分配，
  连内鬼人数、角色概率、`GetAdjustedNumImpostors` 都自动处理，**最省事且与原版一致**。
- **路线 B**：跳过随机，用 `SetRole(pc, roleType)` 逐个硬指定。适合调试或做固定剧本。

`I后续还有` `I后续还有` `I后续还有` 需求里说的"挂载 AI 逻辑（BotBrain）"——**游戏内完全没有任何 Bot 设施**，
全程序集搜 `Bot` 只命中 `HeliSabotage` / `Mushroom` 等无关子串。**AI 逻辑必须 100% 自研。**

---

## 3. 可行性结论

| 需求项 | 可行性 | 依据 |
|---|---|---|
| 主菜单加「智能本地」按钮 | ✅ 高 | `PassiveButton` 可克隆，`OpenCreateGame()` 可直调 |
| 点击后打开原版创建游戏界面 | ✅ 高 | `MainMenuManager.OpenCreateGame()` |
| 强制隐藏「聊天」标签页 | ✅ 高 | `OpenTab(bool isGeneral)` 单 bool 参数，结构确定 |
| 进入本地等待大厅 | ✅ 高 | 复用原版 `NetworkModes.LocalGame` 全流程 |
| 满员注入假人 | ⚠️ 中 | 有 `isDummy` / `AddDummy` / `CreatePlayer`，但**运行期行为未知，需实测** |
| 假人自动入座 | ✅ 高（可能免费） | `_spawnSeatId` 是协程内部量，座位自动分配 |
| `RoleManager` 正常分配职业 | ✅ 高 | `SelectRoles()` 会遍历 `AllPlayers` |
| 挂载 BotBrain AI | ⚠️ 工作量大 | 游戏零 Bot 设施，全部自研 |

**总体判断：可行。** 最不确定的环节是"假人注入"的运行期行为，
其余各环节都有明确、干净的官方 API 可用。

---

## 4. 主要风险与未知项

1. **假人能否通过 `CreatePlayer` 正常生成？**
   `CreatePlayer(ClientData)` 是网络对象生成路径，本地模式下可能对 `ClientId` 有假设。
   需要实测：是走 `ClientData` + `CreatePlayer`，还是走 `isDummy` + `AddDummy` + 手动 `Instantiate` 玩家预置体。
   **这是阶段 1 第一个要验证的东西。**

2. **`MinPlayers` 校验**：`GameStartManager.MinPlayers` 会拦截人数不足的开局。
   若假人只进 `LobbyBehaviour` 而没进 `GameData.AllPlayers`，开局会被拦。
   **假人必须注册进 `GameData`。**

3. **中文字体**：`「智能本地」` 是中文。Among Us 的 TMP 字体图集是预烘焙的，
   虽然官方有简中本地化，但**按钮上能否直接显示这四个字需要实测**。
   退路：用 `StringNames` 枚举（原版有 `StringNames.CreateGameButton` 之类的本地化 key），
   或先用英文占位。

4. **`MainMenuManager` 非单例**：必须 `FindObjectOfType`，且要注意 MainMenu 场景切换后引用失效，
   需要重新获取。

5. **Harmony 补丁点**：IL2CPP 下 Harmony 通过 Il2CppInterop 的 detour 支持生效，
   但**不能补丁 inline 的 native 方法**。若某个目标方法补不上，需要换锚点（有多个备选）。

---

## 5. 建议的实施顺序（阶段 1 起）

| 阶段 | 目标 | 验收标准 |
|---|---|---|
| **1** | 按钮能显示、能点开创建游戏界面 | 主菜单出现「智能本地」，点击后原版界面正常滑入 |
| **2** | 聊天标签页被隐藏且不反弹 | 界面里无聊天标签，来回切/返回重进都不出现 |
| **3** | 能进本地大厅（无假人） | 走原版流程进入等待大厅 |
| **4** | ★ **单只假人注入成功** | 大厅出现 1 个假人并自动入座 —— **这步是整个项目最大的技术门槛** |
| **5** | 按 `MaxPlayers` 满员注入 | 设为 15 人则出现 14 个假人 |
| **6** | 开局职业分配正常 | 假人拿到内鬼/船员职业，UI 正常 |
| **7** | 挂 `BotBrain` 骨架 | 假人能执行最简单的确定性行为（如原地待命/随机移动） |

**阶段 4 是整个项目的成败点**，建议提前做小实验验证，不要等前面都做完。
