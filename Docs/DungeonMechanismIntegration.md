# 副本机制业务接入规范

本文约束普通副本、任务副本、特殊地下城、塔类副本、赫拉斯研究所和安图恩普通征伐等副本业务。

P0 的功能与业务语义基线是 `D:\DXF_ServerS4A12_dungeon_bug_20260723` 中已经实测的副本和任务系统；`D:\DXF_ServerS4A12_dungeon_p0_source_20260726` 是其哈希一致的只读迁移副本。最新 `upstream/main` 只作为承载新版背包和主仓修复的目标平台，不能反向删减迁移源中已经验证的副本或任务能力。

本文中的规则以该迁移源、当前 86JP 实现、`D:\DXF_CODEX_JC\dungeon_bug.md` 的真机纠错记录和自动回归为准。TrinityCore、rAthena、OpenMU 与 Agones 只提供状态机、所有权、超时和 reserve/commit 的设计参考，不是兼容目标，也不能替代 PVF、ETC、当前客户端协议或本项目代码证据。

背包规范只用于参考单一真源、租约、锁边界和事务提交原则。副本系统不复用 `InventoryLease`、背包 Repository 或背包持久化模型。

## 一、P0 范围与兼容边界

P0 建立以下基础能力：

1. 区分共享物理副本实例与玩家参与状态。
2. 为实例、参与局、房间和事件建立不可混淆的身份。
3. 将 Run、Settlement、Room/Encounter 拆成独立状态机。
4. 建立事件 envelope、效果所有权和幂等账本。
5. 将通关收敛为唯一 clear commit 协议。
6. 建立 typed Quest-Dungeon bridge、进本任务快照和事务化任务进度。
7. 阻止旧 timer、旧 await、旧组队 relay 作用于新局或其他实例。

兼容决定：

- `PlayerContext.CurrentRun` 继续是玩家参与副本的唯一入口。
- 当前类名 `DungeonRun` 在 P0 保留，正式语义是 `DungeonParticipantRun` 的具体兼容实现。不要为了名称一次性改动全仓调用点。
- `dungeon_bug_20260723` 中已验证的副本和任务能力必须形成迁移清单并逐项落到新仓；已在最新上游存在的能力核对后复用，缺失能力按小批次迁入统一生命周期。
- 不因架构整理重写客户端协议或 PVF 解析；迁移源中旧背包调用必须改接最新版 `InventoryService`，但业务结果不能静默丢弃。
- P1/P2 不在本文预先虚构完整模型。P0 实机通过后，再按真实剩余缺口单独修订。

## 二、真源与所有权

```text
共享物理实例
  DungeonInstance
    -> 共享选择快照
    -> 最终房间模板与 RoomInstanceId
    -> 世界效果账本
    -> 唯一 DungeonCleared 事实

玩家参与状态
  PlayerContext.CurrentRun -> DungeonRun
    -> RunId + RunGeneration
    -> 个人任务快照、Buff、掉落资格、统计和结算
    -> 当前 RoomState 投影

只读配置
  PVF/ETC -> PvfLib -> GameWorld 投影

跨局状态
  专用 Service/Repository

网络投影
  Handler -> Coordinator -> Builder/Sender
```

所有权规则：

- 地图模板、世界召唤、世界门、世界传送目标属于 `DungeonInstance` 或 Room scope。
- 任务进度、个人 Buff、个人掉落、经验、卡牌和结算包属于 Player scope。
- 安图恩解锁、每日次数等跨局数据属于 Persistent scope，不能塞进 `DungeonRun`。
- 配置对象只读且可缓存；运行态必须每实例或每参与者新建。

## 三、身份模型

每个参与局至少暴露：

| 字段 | 所有者 | 语义 |
| --- | --- | --- |
| `PartyDungeonInstanceId` | `DungeonInstance` | 本次共享物理实例 ID；单人同样有值 |
| `RunId` | `DungeonRun` | 某玩家本次参与 ID |
| `RunGeneration` | `PlayerContext`/`DungeonRun` | 同一会话单调递增；新局建立后旧 generation 永久失效 |
| `RoomInstanceId` | `DungeonInstanceRoom`/`RoomState` | 最终房间模板实例 ID，不等同坐标 |
| `SourceEventId` | 事件 envelope | 同一事实重放时保持不变 |

`DungeonId + RoomKey` 不能代替实例身份。同一队伍、同一坐标甚至同一副本 ID 都可能属于不同局。

异步动作执行前必须同时核对：

```text
session 仍有效
CurrentRun 引用相同
RunId 相同
RunGeneration 相同
PartyDungeonInstanceId 相同
房间动作还需 RoomInstanceId 相同
```

## 四、独立状态机

### Run

```text
Created -> Selecting -> Active -> ClearCommitting -> Cleared -> Ending -> Ended
```

合法旁路：未通关返城、断线或换角时，`Created/Selecting/Active/ClearCommitting -> Ending`。

规则：

- 转换只能通过 `DungeonRun` 的显式方法完成，Handler 不直接赋值。
- 相同目标的重复转换是 no-op，并返回“未首次改变”。
- `Cleared`、`Ending`、`Ended` 不得回退到战斗态。
- `ClearCommitting` 失败时保持该状态并释放执行租约，后续使用同一 clear token 重试；不得创建第二个 clear 事实。
- `Ended` 是终止态。

### Settlement

```text
NotStarted -> Preparing -> ResultShown -> CardsRevealed -> Completed
```

规则：

- 只有 Run=`Cleared` 才能从 `NotStarted` 进入 `Preparing`。
- 并发或重复 `SET_PLAY_RESULT` 只能有一个 preparation executor。
- 持久化失败时保持 `Preparing` 并允许同 token 重试，不回退到 `NotStarted` 或改写 Run 状态。
- 无翻牌流程的塔类副本可从 `ResultShown` 直接进入 `Completed`。

### Room/Encounter

Room 和 Encounter 使用独立状态，不能继续塞进 Run 或 Settlement 枚举。

最低 Room 状态：`Created -> Active -> Cleared -> Closed`。

最低 Encounter 状态：`NotStarted -> Active -> Succeeded/Failed`。`Succeeded` 与 `Failed` 均为终止态。

## 五、事件 envelope

生命周期事件至少携带：

```text
SourceEventId
RunId
RunGeneration
PartyDungeonInstanceId
RoomInstanceId（房间外事件可为空）
SourcePlayerId
AffectedPlayerId（世界事件可为空）
SourceActorId / SourceActorCode（无 actor 时为空）
Cause
OccurredTick
```

规则：

- 本地击杀和组队 relay 必须传播同一个 `SourceEventId`，不能为每个队员重造“另一件事实”。
- 事件 envelope 是事实载体，不包含协议包体。
- Handler 负责把请求转成 typed event；机制代码不读取原始 body，除非该机制本身就是已确认的专用 CMD 解析器。
- 同 EventId 重放必须由效果账本和任务 inbox 去重。

## 六、效果所有权与执行

每个效果使用：

```text
EffectScope = Instance | Party | Room | Player | Persistent
EffectId = SourceEventId + EffectKind + ScopeTarget
```

执行状态：

```text
Absent -> Reserved -> Committed
                   -> Failed（可重新 Reserved）
```

规则：

- reserve 在所属对象锁内完成；网络、数据库和其他 I/O 在锁外执行。
- 同一 `EffectId` 同时只能有一个 executor。
- `Committed` 永远 no-op，不重复召唤、开门、奖励或推进任务。
- 可重试失败必须释放执行租约但保留稳定 EffectId。
- 世界召唤、开门和地图改变使用 Instance/Room scope，只执行一次。
- 任务、个人奖励和个人通知使用 Player scope，每名参与者各执行一次。
- 跨局持久化使用 Persistent scope，并由专用 Repository 在事务内提交。

协议发送无法提供数据库级 exactly-once。对无 ACK 的通知，应按当前客户端证据选择 at-most-once 或可重放策略，并在 sender 注释说明；不得假装网络发送具备事务语义。

## 七、选本、任务与组队

### 共享选择

- 队长在进入实例时根据自己的入口请求和任务冻结唯一 `DungeonSelectionSnapshot`。
- Snapshot 至少包含 maze、起点、Boss 坐标、覆盖图、通关条件定义和会改变物理地图的机制配置。
- 队员加入同一实例时应用这份 Snapshot，不重新随机 maze、Boss 或房间模板。
- 最终 START_MAP actor 模板和随机 seed 由 `DungeonInstanceRoom` 冻结；队员得到独立 `RoomState`，但物理模板必须一致。

### 队员任务不同

当前产品语义固定为：

```text
队长决定共享物理地图
每名队员冻结自己的 QuestRunSnapshot
个人任务按自己的快照推进
任务差异不拆分物理实例，也不取任务地图并集
```

若未来客户端或实机证明需要“全队任务并集”或“拆分实例”，必须作为显式产品变更，不得在机制类中偷偷改变。

## 八、通关提交协议

统一流程：

```text
mechanism/default rule
  -> ClearIntent
  -> DungeonInstance/DungeonRun 锁内 CAS 到 ClearCommitting
  -> 创建或读取唯一 DungeonCleared event/token
  -> 锁外 EffectExecutor
       - Instance/Room 世界效果一次
       - Player clear 投影逐成员一次
       - typed QuestProgressEvent
       - Persistent effect
  -> 所需效果提交后 Run=Cleared
  -> 才接受 SET_PLAY_RESULT
```

`DungeonCleared` 与 `ResultPreparing` 的边界：

- `DungeonCleared` 固定通关事实、Boss/cause 和效果 token。
- `ResultPreparing` 只处理依赖客户端 `SET_PLAY_RESULT` 字段的结算数据。
- 两者不得各自形成同一种奖励入口。
- 默认 Boss、PVF clear condition、任务 NPC、隐藏 Boss、脚本死亡终点都只产生 `ClearIntent`。

## 九、Quest-Dungeon bridge

依赖方向固定为：

```text
Dungeon fact
  -> DungeonQuestBridge
  -> typed QuestProgressEvent
  -> QuestObjectiveEvaluator
  -> QuestProgressRepository.ApplyEvent
  -> 可选 QuestDungeonDirective
  -> 统一 Dungeon settlement
```

禁止 QuestService 反向调用网络 Handler 或直接发送副本结算包。

### QuestRunSnapshot

进本时冻结：

- 活动任务 ID、slot、version 和 `QuestTrigger`；
- 会影响 maze/Boss/任务 actor 的结构性目标；
- 本局采用 snapshot 还是实时任务栏的 objective policy。

当前 P0 语义：结构性任务条件使用进本快照；普通击杀/清图进度只对进本时已活动且定义有效的任务生效。进本后新接任务不追溯本局已发生事件。

## 十、任务事务与幂等

任务进度必须满足：

- `(character_id, quest_id)` 唯一；slot 仍是客户端布局，不是业务身份。
- 活动任务行带 `version`，更新按 `quest_id + expected version/value` 做 CAS。
- 禁止 `INSERT OR REPLACE` 覆盖已有 slot 或任务。
- `QuestTrigger` 封装 packed uint 的通道读取、替换、增减和饱和规则。
- 服务端进度事件先写 progress-event inbox；同 EventId 重放返回 no-op。
- 一次事件影响多个任务时，在同一个 SQLite 事务中重读、评估、CAS 和提交。
- 客户端 trigger 没有稳定事件号时，也必须事务内重读并 CAS 重试，不能按旧快照盲写。
- PVF 任务定义缺失或解析失败时 fail closed；不能提交任务完成或奖励。

任务完成奖励涉及新版背包时，仍必须遵守：在线真源为 `InventoryService`，写操作持有当前会话拥有的 `InventoryLease`，锁内无 `await`，保存走 `InventoryPersistenceService`。副本事务 inbox 不能替代背包事务。

## 十一、Timer、await 与结束局

- 所有周期/延迟任务使用 `ClockService`。
- timer 捕获完整 run identity；只比较对象引用或局部 timer version 不足以替代 generation/instance/room 校验。
- `RunEnding` 在任何清理 `await` 前建立。旧结束流程完成后，只能在 `CurrentRun` 仍是捕获 run 时置空，不能删除期间建立的新局。
- 新局建立先推进 generation，再使旧 timer、旧 await continuation 和旧 relay 永久失效。
- timer name 只用于诊断和取消，不是业务身份。

## 十二、分层与公共入口

### PvfLib

- 忠实解析字段作用域、顺序和重复项。
- 缺失与合法 `0` 分开表达。
- 不按 dungeon/map/monster ID 判断业务。

### GameWorld

- 将 PVF/ETC 投影为只读配置。
- 不发包、不改玩家状态、不重复解析原始字符串。

### Game/Dungeon

- 保存 instance、participant、room、state machine、event、effect 和纯业务结果。
- 不依赖 `EnhancedClientSession`，不写协议号。

### DungeonMechanismCoordinator

- 每个生命周期在 Handler 中只有一个公共入口。
- 本地击杀与组队 relay 调同一个 typed 入口。
- 具体机制不得复制普通结算、任务或奖励流程。

### Builder/Sender

- Builder 只序列化已确认字段。
- Sender 集中 cmd/type 与 envelope。
- 协议证据必须记录当前客户端 DMP/handler、同版本抓包、PVF/ETC 或受控枚举；旧台服只作对照。

## 十三、禁止新增

- 按单个 dungeon/map/monster/NPC/item ID 修机制。
- 用 `Party + RoomKey` 判断同一实例。
- Handler 直接写状态枚举、运行时 bool 或任务 SQL。
- 锁内 `await`、发包或长 I/O。
- 以静态字典保存单局/单房状态。
- 为每个副本复制 timer、clear 或奖励流程。
- 未确认 CMD/NOTI 注册后直接赋予业务含义。
- 用 `INSERT OR REPLACE` 保存活动任务。
- 旧局 continuation 在新 generation 上执行。
- 为迁移旧功能绕过新版背包入口。

## 十四、P0 最低验证

必须新增并通过：

1. 并发 clear 只能创建一个 DungeonCleared token，个人 clear 投影一次。
2. 并发/重复 `SET_PLAY_RESULT` 只能有一个 preparation executor。
3. 相同 EventId 重放不重复任务、掉落、召唤、开门或奖励。
4. client trigger 与 server kill/clear-map 并发不丢更新。
5. 旧 timer、旧 await、旧 RunEnding 在新局建立后全部失效。
6. 不同副本实例的队员不会收到旧 kill relay 或 move。
7. 队员任务不同仍共享同一 selection 和最终 room template。
8. 世界效果一次，个人任务逐成员一次。
9. Anton/奖励持久化中途失败可使用同 EffectId 安全重试。
10. 无效任务定义不能提交进度或完成。

共享路径回归至少包括：

```text
special-dungeon / part2 / part3
dungeon-map-fallback
dungeon-room-progress
dungeon-run
dungeon-combat-party
clear-map-quest
card-reward-flow
quest-clear / quest-trigger-counts / quest-ack-format / question-quest-branch
death-tower map/drop/protocol/quest-routing
party
```

在上述定向回归全部通过后，还必须运行当前主仓 `SelfTestRegistry` 可执行的全量自测，并逐项核对测试名称与结果。不能只用定向测试代替全量回归，也不能只报告通过数量。

必须记录最新 `upstream/main` 的改动前基线；主线已有失败和当前分支结果分别报告。P0 最终结果不得新增失败。若全量回归受环境、外部服务或既有主线问题影响，必须保留原始失败项、单独重跑受影响测试并说明证据，不能把未执行或环境失败写成通过。

## 十五、迁移纪律

1. 先迁统一入口和可证明的通用配置，不整包复制旧分支试错代码。
2. 每批写入 `dungeon_bug.md`：迁移来源、删除的旁路、保留的兼容点、测试结果和待真机项。
3. 逆向脚本文件头必须写用途、输入、版本、地址范围、输出和限制；已有工具能扩展时禁止重造。
4. 构建使用隔离输出目录。
5. 自动测试通过后才能部署独立真机测试目录；用户真机通过前不得提交 MR。
