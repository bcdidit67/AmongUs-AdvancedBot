# 可行性侦察：用「真实假客户端」实现 Among Us AI 玩家

> **术语约定**：本文讲的是「人机」路线 —— 一个**外置的 bot 客户端**，
> 而不是游戏自带的「假人」dummy 机制。两者的区别见 [README](README.md#-术语约定重要读之前请先看这个)。

调研日期：2026-10-04
调研对象：Among Us `v19s (build 7489)` / Unity 2022.3.44f1 / IL2CPP
调研工具：`Il2CppInspector`（本项目自建，Mono.Cecil 纯元数据读取）

---

## 0. 结论摘要

| 判据 | 结果 |
|---|---|
| 协议消息类型能否读出 | ✅ **完整读出，32 个类型** |
| RPC 操作码表能否读出 | ✅ **完整读出，28+ 个** |
| 序列化格式能否还原 | ✅ **可从 `Serialize(Hazel.MessageWriter)` 签名与字段类型还原** |
| 是否需要逆向机器码 | ❌ **不需要** —— 这是最关键的好消息 |
| Host 能否绑定 UDP 端点 | ⚠️ **有 API，但需运行期确认** |
| 是否有现成实现可参考 | ✅ Impostor（但版本可能过旧） |
| 总体工作量 | **周级**（非「再改几行」） |

**判断：技术路径成立，且比在本进程内伪造身份更有前途。但这是一个新项目，不是当前项目的延伸。**

---

## 1. 为什么这个方向能从根上解决问题

现有方案（本项目 17 轮所做）的本质是：

> 在游戏进程内**伪造**玩家，然后逐个说服游戏的各个子系统「这也是我」。

而「外置假客户端」方案的本质是：

> 让人机**成为真正的网络客户端**，游戏原生地把它们当真玩家处理。

**四条身份解析路径（相机 / ClientId→Character / inputHandler / HudManager 缓存）在真客户端方案下全部原生正确** —— 因为游戏根本不需要区分真假客户端。

额外收益：**AI 逻辑变成独立的普通 C# 程序**，彻底脱离 IL2CPP 的束缚。

---

## 2. ★ 核心发现：协议可以从游戏自己身上完整读出

### 2.1 全部数据消息类型（32 个）

```
AmongUs.InnerNet.GameDataMessages
├── 基础设施
│   ├── IGameDataMessage            (接口)
│   ├── BaseGameDataMessage         → Serialize(Hazel.MessageWriter) / SerializeValues(...)
│   ├── BaseRpcMessage              → SerializeRpcValues(...)
│   └── GameDataTypes               → Invalid / DataFlag / RpcFlag / SpawnFlag
├── 对象生成
│   ├── SpawnGameDataMessage
│   └── DespawnGameDataMessage
└── RPC 消息（23 个）
    ├── RpcSetTasksMessage            ← 任务分配
    ├── RpcCompleteTaskMessage        ← 任务完成
    ├── RpcSetRoleMessage             ← 职业
    ├── RpcReportDeadBodyMessage      ← 报告尸体
    ├── RpcEnterVentMessage / RpcExitVentMessage / RpcBootFromVentMessage
    ├── RpcClimbLadderMessage         ← 移动
    ├── RpcPlayAnimationMessage
    ├── RpcSendChatMessage / RpcSendChatNoteMessage / RpcSendQuickChatMessage
    ├── RpcVotingCompleteMessage
    ├── RpcSetNameMessage / RpcSetColorMessage / RpcSetLevelMessage
    ├── RpcSetHatStrMessage / RpcSetSkinStrMessage / RpcSetPetStrMessage
    ├── RpcSetVisorStrMessage / RpcSetNamePlateStrMessage
    ├── RpcSetScannerMessage / RpcSetStartCounterMessage
    ├── RpcCancelPetMessage / RpcPetMessage
    ├── RpcAddVoteBanMessage
    └── RpcUsePlatformMessage
```

**注意：这份清单覆盖了 bot 需要的全部 gameplay 行为** —— 派任务、做任务、报尸体、钻管道、爬梯、发言、投票、换装。

### 2.2 RPC 操作码表（28+ 个，完整）

```
RpcCalls:
  PlayAnimation, CompleteTask, SyncSettings, SetInfected, Exiled,
  CheckName, SetName, CheckColor, SetColor,
  SetHat_Deprecated, SetSkin_Deprecated, SetPet_Deprecated,
  ReportDeadBody, MurderPlayer, SendChat, StartMeeting, SetScanner,
  SendChatNote, SetStartCounter, EnterVent, ExitVent, SnapTo,
  CloseMeeting, VotingComplete, CastVote, ClearVote, AddVote,
  CloseDoorsOfType, ...
```

**`SetInfected` / `MurderPlayer` / `CastVote` / `SnapTo` 都在** —— 意味着**内鬼击杀、投票、位置同步全部可实现**。

### 2.3 序列化格式可直接还原（举例验证）

```csharp
public class RpcCompleteTaskMessage : BaseRpcMessage
{
    Sys.UInt32 idx { get; set; }              // 任务索引
    RpcCalls  RpcType { get; }                // 操作码
    public .ctor(Sys.UInt32 rpcObjectNetId, Sys.UInt32 idx);
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg);
}
```

**字段类型 + 构造函数参数 + 序列化方法签名都在** —— 逐个类型照此读出，就能重建完整协议。

### 2.4 为什么这比当年 Impostor 作者的条件好

| | Impostor 时代 | 现在 |
|---|---|---|
| 依据 | 抓包 + 逆向机器码 | **interop 程序集的元数据** |
| 覆盖度 | 靠样本推断 | **全量类型 + 字段 + 操作码** |
| 版本适配 | 每次更新重新逆 | **重新跑一遍 dump 即可** |

**这意味着一件重要的事：协议适配可以脚本化。** 游戏更新后重跑 `Il2CppInspector`，diff 消息类型与字段，就能知道协议改了什么。

---

## 3. 关键未知：Host 是否真的绑定 UDP 端口

### 3.1 现有证据（正面）

```csharp
InnerNetClient.SetEndpoint(Sys.String addr, Sys.UInt16 port, Sys.Boolean dtls)
InnerNetClient.Connect(InnerNet.MatchMakerModes mode, Sys.String matchmakerToken)
InnerNet.MatchMakerModes: None / Client / HostAndClient
```

`SetEndpoint` + `HostAndClient` 的组合说明**存在「在指定地址端口上开房」的能力**（局域网模式用的就是它）。

### 3.2 为什么不确认

**「本地游戏」（LocalGame）模式很可能使用进程内回环连接，根本不绑定 socket** ——
那样外置客户端就无法连接。

**这是整个方案的生死判据，必须运行期验证。**

### 3.3 验证方法（需要一次实测，约 2 分钟）

1. 游戏内选择**局域网**（或通过 `regionInfo.json` 指向自建服务器）开一局
2. 在游戏运行时于终端执行：

```bash
ss -ulpn | grep -i among
# 或
ss -ulpn | head -20
```

3. **若出现 UDP 监听端口（Among Us 传统使用 22023）→ 方案成立**
   **若毫无监听 → 方案不成立，需转向「自建 Impostor 服务器」路线**

---

## 4. 风险清单

| 风险 | 严重度 | 说明 |
|---|---|---|
| **本地模式不开端口** | 🔴 高 | 直接决定方案成立与否，见 §3 |
| Impostor 不支持 v19 | 🟡 中 | 它停更在 1.10.x；但可作**协议参考**而非直接依赖 |
| Hazel 传输层需另行研究 | 🟡 中 | `Hazel.dll` 在独立 interop 程序集，需一并 dump |
| 握手流程未知 | 🟡 中 | 顶层包类型（`InnerNet.Tags`）需单独整理 |
| 工作量周级 | 🟡 中 | 非「改几行看效果」，缺少快速反馈循环 |
| 游戏模式改变 | 🟢 低 | 从「主菜单点本地」变为「连本地服务器」，但**仍是纯本地** |

---

## 5. 建议的推进路径（若验证通过）

| 阶段 | 目标 | 产出 |
|---|---|---|
| **0** | 端口验证（§3.3） | 成立 / 不成立 的判定 |
| **1** | 系统导出协议 | 脚本化 dump 出全部消息类型 + 字段 + RpcCalls 映射 → 生成协议文档 |
| **2** | 最小客户端 | 只做握手 + 进大厅，能在大厅里看到自己的名字 |
| **3** | 位置同步 | 让人机在大厅走动（`SnapTo`） |
| **4** | 开局 | 派任务、进地图 |
| **5** | AI 行为 | 纯 C# 逻辑，不再受引擎束缚 |

**阶段 1 完全不需要启动游戏**，可以立即开始 —— 而且它产出的协议文档本身就有独立价值。

---

## 6. 与现有成果的关系

现有 BepInEx 插件（v0.4.0-hivemind / v0.6.0-wander）**不需要废弃**：

- 它验证了「14 个假人 + 开局 + 职业分配」在游戏内可行
- 它积累的 `REPORT.md`、`DummyBehaviour`、四条身份路径等结论，**正好解释了「为什么进程内伪造走不通」**——这就是转向外置客户端的论证基础

**两条线是互补的：进程内方案摸到了天花板，而天花板的高度恰好证明了外置方案的必要性。**
