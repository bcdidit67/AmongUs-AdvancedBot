# Among Us v19s (build 7489) 网络协议参考

> **数据来源**：由 `Il2CppInspector`（Mono.Cecil 纯元数据读取）从游戏自身的
> il2cpp interop 程序集直接导出，**非猜测、非抓包推断**。
>
> 游戏：Among Us `v19s (build 7489)` / Unity 2022.3.44f1 / IL2CPP / 元数据 v31
> 导出时间：2026-10-04 13:57:49

---

## 一、顶层包类型
```csharp
=== public enum AmongUs.InnerNet.GameDataMessages.GameDataTypes ===
  基类: Sys.Enum
  -- 字段 --
    Sys.Byte value__
    static AmongUs.InnerNet.GameDataMessages.GameDataTypes Invalid
    static AmongUs.InnerNet.GameDataMessages.GameDataTypes DataFlag
    static AmongUs.InnerNet.GameDataMessages.GameDataTypes RpcFlag
    static AmongUs.InnerNet.GameDataMessages.GameDataTypes SpawnFlag
    static AmongUs.InnerNet.GameDataMessages.GameDataTypes DespawnFlag
    static AmongUs.InnerNet.GameDataMessages.GameDataTypes SceneChangeFlag
    static AmongUs.InnerNet.GameDataMessages.GameDataTypes ReadyFlag
    static AmongUs.InnerNet.GameDataMessages.GameDataTypes ChangeSettingsFlag_Deprecated
    static AmongUs.InnerNet.GameDataMessages.GameDataTypes XboxDeclareXuid

```

## 二、RPC 操作码全表
```csharp
=== public enum RpcCalls ===
  基类: Sys.Enum
  -- 字段 --
    Sys.Byte value__
    static RpcCalls PlayAnimation
    static RpcCalls CompleteTask
    static RpcCalls SyncSettings
    static RpcCalls SetInfected
    static RpcCalls Exiled
    static RpcCalls CheckName
    static RpcCalls SetName
    static RpcCalls CheckColor
    static RpcCalls SetColor
    static RpcCalls SetHat_Deprecated
    static RpcCalls SetSkin_Deprecated
    static RpcCalls ReportDeadBody
    static RpcCalls MurderPlayer
    static RpcCalls SendChat
    static RpcCalls StartMeeting
    static RpcCalls SetScanner
    static RpcCalls SendChatNote
    static RpcCalls SetPet_Deprecated
    static RpcCalls SetStartCounter
    static RpcCalls EnterVent
    static RpcCalls ExitVent
    static RpcCalls SnapTo
    static RpcCalls CloseMeeting
    static RpcCalls VotingComplete
    static RpcCalls CastVote
    static RpcCalls ClearVote
    static RpcCalls AddVote
    static RpcCalls CloseDoorsOfType
    static RpcCalls SetTasks
    static RpcCalls ClimbLadder
    static RpcCalls UsePlatform
    static RpcCalls SendQuickChat
    static RpcCalls BootFromVent
    static RpcCalls UpdateSystem
    static RpcCalls SetVisor_Deprecated
    static RpcCalls SetNamePlate_Deprecated
    static RpcCalls SetLevel
    static RpcCalls SetHatStr
    static RpcCalls SetSkinStr
    static RpcCalls SetPetStr
    static RpcCalls SetVisorStr
    static RpcCalls SetNamePlateStr
    static RpcCalls SetRole
    static RpcCalls ProtectPlayer
    static RpcCalls Shapeshift
    static RpcCalls CheckMurder
    static RpcCalls CheckProtect
    static RpcCalls Pet
    static RpcCalls CancelPet
    static RpcCalls CheckZipline
    static RpcCalls UseZipline
    static RpcCalls TriggerSpores
    static RpcCalls CheckSpore
    static RpcCalls CheckShapeshift
    static RpcCalls RejectShapeshift
    static RpcCalls LobbyTimeExpiring
    static RpcCalls ExtendLobbyTimer
    static RpcCalls CheckVanish
    static RpcCalls StartVanish
    static RpcCalls CheckAppear
    static RpcCalls StartAppear
    static RpcCalls QueueOverruleVotes
    static RpcCalls SpiritGuideMessage

```

## 三、消息基类与序列化契约
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.BaseGameDataMessage ===
  基类: Il2CppSystem.Object
  -- 属性 --
    AmongUs.InnerNet.GameDataMessages.GameDataTypes GameDataType { get }
  -- 方法 --
    public virtual Sys.Void Serialize(Hazel.MessageWriter msg)
    public virtual Sys.Void SerializeValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor()
    public Sys.Void .ctor(Sys.IntPtr pointer)


=== public class AmongUs.InnerNet.GameDataMessages.BaseRpcMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseGameDataMessage
  -- 属性 --
    AmongUs.InnerNet.GameDataMessages.GameDataTypes _GameDataType_k__BackingField { get; set }
    Sys.UInt32 rpcObjectNetId { get; set }
    AmongUs.InnerNet.GameDataMessages.GameDataTypes GameDataType { get }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId)
    public override Sys.Void SerializeValues(Hazel.MessageWriter msg)
    public virtual Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

## 四、全部消息类型（字段 + 构造 + 序列化方法）

### `DespawnGameDataMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.DespawnGameDataMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseGameDataMessage
  -- 属性 --
    AmongUs.InnerNet.GameDataMessages.GameDataTypes _GameDataType_k__BackingField { get; set }
    Sys.UInt32 objToDespawnNetId { get; set }
    AmongUs.InnerNet.GameDataMessages.GameDataTypes GameDataType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 objToDespawnNetId)
    public override Sys.Void SerializeValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcAddVoteBanMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcAddVoteBanMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.Int32 sourceClientId { get; set }
    Sys.Int32 clientIdToVoteBan { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.Int32 sourceClientId, Sys.Int32 clientIdToVoteBan)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcBootFromVentMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcBootFromVentMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.Int32 ventId { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.Int32 ventId)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcCancelPetMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcCancelPetMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcClimbLadderMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcClimbLadderMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.Byte ladderId { get; set }
    Sys.Byte lastClimbLadderSid { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.Byte ladderId, Sys.Byte lastClimbLadderSid)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcCloseMeetingMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcCloseMeetingMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcCompleteTaskMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcCompleteTaskMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.UInt32 idx { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.UInt32 idx)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcEnterVentMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcEnterVentMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.Int32 ventId { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.Int32 ventId)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcExitVentMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcExitVentMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.Int32 ventId { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.Int32 ventId)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcPetMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcPetMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    UE.Vector2 pos { get; set }
    UE.Vector2 petPos { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, UE.Vector2 pos, UE.Vector2 petPos)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcPlayAnimationMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcPlayAnimationMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.Byte animType { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.Byte animType)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcReportDeadBodyMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcReportDeadBodyMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.Byte targetPlayerId { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.Byte targetPlayerId)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcSendChatMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcSendChatMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.String chatText { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.String chatText)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcSendChatNoteMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcSendChatNoteMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.Byte srcPlayerId { get; set }
    ChatNoteTypes noteType { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.Byte srcPlayerId, ChatNoteTypes noteType)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcSendQuickChatMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcSendQuickChatMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    AmongUs.QuickChat.QuickChatPhraseBuilderResult quickChatData { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, AmongUs.QuickChat.QuickChatPhraseBuilderResult quickChatData)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcSetColorMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcSetColorMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.UInt32 netIdToColor { get; set }
    Sys.Byte bodyColor { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.UInt32 netIdToColor, Sys.Byte bodyColor)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcSetHatStrMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcSetHatStrMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.String hatId { get; set }
    Sys.Byte rpcSequenceId { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.String hatId, Sys.Byte rpcSequenceId)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcSetLevelMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcSetLevelMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.UInt32 level { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.UInt32 level)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcSetNameMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcSetNameMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.UInt32 netIdOfNamed { get; set }
    Sys.String name { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.UInt32 netIdOfNamed, Sys.String name)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcSetNamePlateStrMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcSetNamePlateStrMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.String namePlateId { get; set }
    Sys.Byte rpcSequenceId { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.String namePlateId, Sys.Byte rpcSequenceId)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcSetPetStrMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcSetPetStrMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.String petId { get; set }
    Sys.Byte rpcSequenceId { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.String petId, Sys.Byte rpcSequenceId)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcSetRoleMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcSetRoleMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    AmongUs.GameOptions.RoleTypes roleType { get; set }
    Sys.Boolean canOverrideRole { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, AmongUs.GameOptions.RoleTypes roleType, Sys.Boolean canOverrideRole)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcSetScannerMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcSetScannerMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.Boolean value { get; set }
    Sys.Byte count { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.Boolean value, Sys.Byte count)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcSetSkinStrMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcSetSkinStrMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.String skinId { get; set }
    Sys.Byte rpcSequenceId { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.String skinId, Sys.Byte rpcSequenceId)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcSetStartCounterMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcSetStartCounterMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.Int32 counterId { get; set }
    Sys.SByte secondsLeft { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.Int32 counterId, Sys.SByte secondsLeft)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcSetTasksMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcSetTasksMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Sys.Byte> taskTypeIds { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Sys.Byte> taskTypeIds)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcSetVisorStrMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcSetVisorStrMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Sys.String visorId { get; set }
    Sys.Byte rpcSequenceId { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Sys.String visorId, Sys.Byte rpcSequenceId)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcUsePlatformMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcUsePlatformMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `RpcVotingCompleteMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.RpcVotingCompleteMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseRpcMessage
  -- 属性 --
    Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<VoterState> voterStates { get; set }
    Sys.Byte exiledPlayerId { get; set }
    Sys.Boolean tie { get; set }
    Sys.Boolean wasOverruled { get; set }
    Sys.UInt16 overrideId { get; set }
    RpcCalls RpcType { get }
  -- 方法 --
    public Sys.Void .ctor(Sys.UInt32 rpcObjectNetId, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<VoterState> voterStates, Sys.Byte exiledPlayerId, Sys.Boolean tie, Sys.Boolean wasOverruled, Sys.UInt16 overrideId)
    public override Sys.Void SerializeRpcValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### `SpawnGameDataMessage`
```csharp
=== public class AmongUs.InnerNet.GameDataMessages.SpawnGameDataMessage ===
  基类: AmongUs.InnerNet.GameDataMessages.BaseGameDataMessage
  -- 属性 --
    AmongUs.InnerNet.GameDataMessages.GameDataTypes _GameDataType_k__BackingField { get; set }
    Il2CppSystem.Type _NetObjectType_k__BackingField { get; set }
    Sys.UInt32 spawnTypeId { get; set }
    Sys.Int32 ownerId { get; set }
    InnerNet.SpawnFlags flags { get; set }
    Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<InnerNet.InnerNetObject> childNetObjects { get; set }
    AmongUs.InnerNet.GameDataMessages.GameDataTypes GameDataType { get }
    Il2CppSystem.Type NetObjectType { get; set }
  -- 方法 --
    public Sys.Void .ctor(InnerNet.InnerNetObject parentNetObject, Sys.Int32 ownerId, InnerNet.SpawnFlags flags, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<InnerNet.InnerNetObject> childNetObjects)
    public Sys.Void ClearOrDecrementChildObjectDirt()
    public override Sys.Void SerializeValues(Hazel.MessageWriter msg)
    public Sys.Void .ctor(Sys.IntPtr pointer)

```


---

## 六、顶层包类型（InnerNet.Tags）—— 握手与连接控制

共 26 个。这是玩家客户端与服务端之间的**最外层消息类型**。

```csharp
=== public static class InnerNet.Tags ===
  基类: Il2CppSystem.Object
  -- 属性 --
    Sys.Byte HostGame { static get; static set }
    Sys.Byte JoinGame { static get; static set }
    Sys.Byte StartGame { static get; static set }
    Sys.Byte RemoveGame { static get; static set }
    Sys.Byte RemovePlayer { static get; static set }
    Sys.Byte GameData { static get; static set }
    Sys.Byte GameDataTo { static get; static set }
    Sys.Byte JoinedGame { static get; static set }
    Sys.Byte EndGame { static get; static set }
    Sys.Byte AlterGame { static get; static set }
    Sys.Byte KickPlayer { static get; static set }
    Sys.Byte WaitForHost { static get; static set }
    Sys.Byte Redirect { static get; static set }
    Sys.Byte ReselectServer { static get; static set }
    Sys.Byte GetGameListV2 { static get; static set }
    Sys.Byte ReportPlayer { static get; static set }
    Sys.Byte QuickMatch { static get; static set }
    Sys.Byte QuickMatchHost { static get; static set }
    Sys.Byte SetGameSession { static get; static set }
    Sys.Byte SetActivePodType { static get; static set }
    Sys.Byte QueryPlatformIds { static get; static set }
    Sys.Byte QueryLobbyInfo { static get; static set }
    Sys.Byte EndGameHostMigration { static get; static set }
    Sys.Byte HostModdedGame { static get; static set }
    Sys.Byte PackedGameDataTo { static get; static set }
    Sys.Byte ServerDebugAlert { static get; static set }
  -- 方法 --
    public Sys.Void .ctor(Sys.IntPtr pointer)

```

### 握手时序（【推断】，依据包类型命名与语义，**需实测验证**）

```
客户端                          服务端
  │                                │
  │──── JoinGame ─────────────────▶│  携带 gameId / 地图 / 玩家名 / 平台数据
  │◀─── JoinedGame ────────────────│  返回 clientId / hostId / 玩家列表
  │◀─── WaitForHost ───────────────│  等待房主就绪
  │◀─── StartGame ─────────────────│  开局
  │                                │
  │◀─── GameData ──────────────────│  游戏数据（Spawn/RPC）
  │◀─── GameDataTo / PackedGameDataTo │  定向/批量
  │◀─── EndGame / KickPlayer / RemovePlayer
```

### 与 gameplay 的衔接

开局后所有游戏行为都走 `GameData` 包，内部由 `GameDataTypes` 再分三类：

| GameDataTypes | 含义 | 对应消息 |
|---|---|---|
| `DataFlag` | 状态数据 | `SpawnGameDataMessage` / `DespawnGameDataMessage` |
| `RpcFlag` | 远程调用 | 30 个 `Rpc*Message`（见第四节） |
| `SpawnFlag` | 对象生成 | 见 Spawn 相关 |

---

## 七、Hazel 传输层

位于独立程序集 `Hazel.dll`。协议结构可从以下类型读出：

```csharp
=== public enum Hazel.SendOption ===
  基类: Sys.Enum
  特性: FlagsAttribute
  -- 字段 --
    Sys.Byte value__
    static Hazel.SendOption None
    static Hazel.SendOption Reliable


=== public class Hazel.MessageWriter ===
  基类: Il2CppSystem.Object
  -- 属性 --
    Sys.Int32 BufferSize { static get; static set }
    Hazel.ObjectPool<Hazel.MessageWriter> WriterPool { static get; static set }
    Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Sys.Byte> Buffer { get; set }
    Sys.Int32 Length { get; set }
    Sys.Int32 Position { get; set }
    Hazel.SendOption _SendOption_k__BackingField { get; set }
    Il2CppSystem.Collections.Generic.Stack<Sys.Int32> messageStarts { get; set }
    Hazel.SendOption SendOption { get; set }
  -- 方法 --
    public Sys.Void .ctor(Sys.Int32 bufferSize)
    public Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Sys.Byte> ToByteArray(Sys.Boolean includeHeader)
    public static Hazel.MessageWriter Get(Hazel.SendOption sendOption)
    public Sys.Boolean HasBytes(Sys.Int32 expected)
    public Sys.Void StartMessage(Sys.Byte typeFlag)
    public Sys.Void EndMessage()
    public Sys.Void CancelMessage()
    public Sys.Void Clear(Hazel.SendOption sendOption)
    public virtual Sys.Void Recycle()
    public Sys.Void CopyFrom(Hazel.MessageReader target)

=== public class Hazel.MessageReader ===
  基类: Il2CppSystem.Object
  -- 属性 --
    Hazel.ObjectPool<Hazel.MessageReader> ReaderPool { static get; static set }
    Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Sys.Byte> Buffer { get; set }
    Sys.Byte Tag { get; set }
    Sys.Int32 Length { get; set }
    Sys.Int32 Offset { get; set }
    Hazel.MessageReader Parent { get; set }
    Sys.Int32 _position { get; set }
    Sys.Int32 readHead { get; set }
    Sys.Int32 BytesRemaining { get }
    Sys.Int32 Position { get; set }
  -- 方法 --
    public static Hazel.MessageReader GetSized(Sys.Int32 minSize)
    public static Hazel.MessageReader Get(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Sys.Byte> buffer)
    public static Hazel.MessageReader Get(Hazel.MessageReader source)
    public Hazel.MessageReader ReadMessage()
    public Hazel.MessageReader ReadMessageAsNewBuffer()
    public virtual Sys.Void Recycle()
```

**关键结构**：

- 每个包以 **1 字节 Tag** 开头（即 `InnerNet.Tags` 的值）
- `MessageReader.Parent` / `MessageWriter.messageStarts`(Stack) 表明支持**嵌套子消息**
- `SendOption`: `None` / `Reliable` —— Hazel 的可靠传输标记

**每个 Tag 对应的负载格式，可从 `InnerNetClient` 的 `HandleGameData` 系列方法签名继续还原。**

---

## 八、★ 权威常量（运行期真值，非文档推断）

> 由 `ProtoDump` 插件从**运行中的游戏**反射导出。
> 元数据只能告诉我们「存在哪些标签」，**数值只有运行期才知道**。
> 导出时间：2026-10-04 14:08:55

### 8.1 InnerNet.Tags 完整真值表（26 个）

| 标签 | 值 | 十进制 |
|---|---|---|
| `HostGame` | `0x00` | 0 |
| `JoinGame` | `0x01` | 1 |
| `StartGame` | `0x02` | 2 |
| `RemoveGame` | `0x03` | 3 |
| `RemovePlayer` | `0x04` | 4 |
| `GameData` | `0x05` | 5 |
| `GameDataTo` | `0x06` | 6 |
| `JoinedGame` | `0x07` | 7 |
| `EndGame` | `0x08` | 8 |
| `AlterGame` | `0x0A` | 10 |
| `KickPlayer` | `0x0B` | 11 |
| `WaitForHost` | `0x0C` | 12 |
| `Redirect` | `0x0D` | 13 |
| `ReselectServer` | `0x0E` | 14 |
| `GetGameListV2` | `0x10` | 16 |
| `ReportPlayer` | `0x11` | 17 |
| `QuickMatch` | `0x12` | 18 |
| `QuickMatchHost` | `0x13` | 19 |
| `SetGameSession` | `0x14` | 20 |
| `SetActivePodType` | `0x15` | 21 |
| `QueryPlatformIds` | `0x16` | 22 |
| `QueryLobbyInfo` | `0x17` | 23 |
| `EndGameHostMigration` | `0x18` | 24 |
| `HostModdedGame` | `0x19` | 25 |
| `PackedGameDataTo` | `0x1A` | 26 |
| `ServerDebugAlert` | `0xFF` | 255 |

**与旧版公开文档的关键差异**：

- `0x00` ~ `0x11` **与文档完全一致** → 早期标签稳定，文档可用
- **`GetGameList` (0x09) 在 v19 已不存在** → 文档里有、实际没有，`0x09` 成为空位
- `0x12` ~ `0x1A` 及 `0xFF` 为新增 → 全部**追加**，不破坏向后兼容
- 结论：**必须从运行中的游戏读，不能只信文档**（本次实测的直接教训）

### 8.2 RpcCalls（63 个）

```
PlayAnimation = 0
CompleteTask = 1
SyncSettings = 2
SetInfected = 3
Exiled = 4
CheckName = 5
SetName = 6
CheckColor = 7
SetColor = 8
SetHat_Deprecated = 9
SetSkin_Deprecated = 10
ReportDeadBody = 11
MurderPlayer = 12
SendChat = 13
StartMeeting = 14
SetScanner = 15
SendChatNote = 16
SetPet_Deprecated = 17
SetStartCounter = 18
EnterVent = 19
ExitVent = 20
SnapTo = 21
CloseMeeting = 22
VotingComplete = 23
CastVote = 24
ClearVote = 25
AddVote = 26
CloseDoorsOfType = 27
SetTasks = 29
ClimbLadder = 31
UsePlatform = 32
SendQuickChat = 33
BootFromVent = 34
UpdateSystem = 35
SetVisor_Deprecated = 36
SetNamePlate_Deprecated = 37
SetLevel = 38
SetHatStr = 39
SetSkinStr = 40
SetPetStr = 41
SetVisorStr = 42
SetNamePlateStr = 43
SetRole = 44
ProtectPlayer = 45
Shapeshift = 46
CheckMurder = 47
CheckProtect = 48
Pet = 49
CancelPet = 50
CheckZipline = 51
UseZipline = 52
TriggerSpores = 53
CheckSpore = 54
CheckShapeshift = 55
RejectShapeshift = 56
LobbyTimeExpiring = 60
ExtendLobbyTimer = 61
CheckVanish = 62
StartVanish = 63
CheckAppear = 64
StartAppear = 65
QueueOverruleVotes = 66
SpiritGuideMessage = 67
```

### 8.3 GameDataTypes（9 个值）

```
Invalid = 0x00
DataFlag = 0x01
RpcFlag = 0x02
SpawnFlag = 0x04
DespawnFlag = 0x05
SceneChangeFlag = 0x06
ReadyFlag = 0x07
ChangeSettingsFlag_Deprecated = 0x08
XboxDeclareXuid = 0xCF
```

> 元数据里只能看到 4 个名字，实际有 9 个值 —— 又一处必须运行期确认的地方。

### 8.4 本地游戏的运行期状态

```
★ GameId=32 (0x00000020)  ClientId=-1  HostId=0  NetworkMode=LocalGame  GameState=NotJoined
```

### 8.5 ★ 构造 JoinGame 包所需的全部信息（现已齐备）

```
01              # SendOption = Reliable
0003            # Nonce (uint16 大端，自增)
040001          # Hazel 消息: 长度=0x0004, tag=0x01 (JoinGame)
20000000        # GameId (int32 小端)
```

**9 个字节，就能向游戏发起加入请求。**
