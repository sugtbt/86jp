# 副本机制业务接入规范

本文约束普通副本、特殊地下城、任务副本、赫拉斯研究所和安图恩普通征伐等副本业务的新增与迁移方式。

本文只参考背包规范中的分层、统一入口和禁止旁路原则。副本系统与背包系统是独立系统，不复用背包的 `InventoryLease`、Repository、刷新器或持久化模型。

## 一、目标与真源

副本在线真源：

```text
单局状态 = PlayerContext.CurrentRun -> DungeonRun
单房状态 = DungeonRun.RoomStates -> RoomState
只读配置 = PVF/ETC -> PvfLib/GameWorld 投影
跨局进度 = 专用持久化 Service/Repository
协议投影 = Network/Builders
生命周期分发 = Dungeon Handler -> DungeonMechanismCoordinator
```

基本原则：

1. 机制按 PVF/ETC 词条、资源关系或通用机制类型匹配，不按某一个 dungeon/map/monster ID 生效。
2. `DungeonRun` 是单局状态唯一真源；返回城镇、断线、切角或开始新局后，不得保留旧局状态。
3. `RoomState` 保存每房状态和一次性标记，不使用进程级静态集合记录房间进度。
4. Anton 解锁、每日次数等跨局状态必须由独立服务持久化，不能塞进 `DungeonRun`。
5. Handler 只解析命令、维护通用流程、调用一次机制入口并执行返回结果，不承载具体副本规则。

## 二、代码分层

### 1. PvfLib

目录：`Tool/PvfLib/Models/`

职责：忠实解析 PVF 字段并保留字段作用域、顺序和重复项。

要求：

- 使用结构化模型保存坐标、权重、条件和重复列表。
- 区分 DGN 顶层、maze、map 和内嵌节点作用域。
- 缺失词条与合法的 `0` 必须使用不同状态表达。
- 解析器不得判断“这是某个副本，所以这样处理”。
- 新增字段必须有 synthetic parser 自测和至少一个真实 PVF 样本验证。

### 2. GameWorld

目录：`Server/DfoServer/GameWorld/`

职责：把 PVF 解析结果投影为服务器只读副本配置，例如迷宫、房间、怪物模板、条件目标和地图覆盖候选。

要求：

- 投影不得发送网络包或修改玩家状态。
- 不重复解析已经由 PvfLib 类型化的原始字符串。
- 随机选择使用 `ServerRandom`；需要与客户端共享序列时使用房间 `DnfLcg`。
- 配置解析失败应关闭对应机制并记录诊断，不能回退到某张图的硬编码 ID。

### 3. Game/Dungeon

目录：`Server/DfoServer/Game/Dungeon/`

职责：副本机制配置、单局运行态、状态转换和纯业务结果。

要求：

- 配置对象只读并可缓存；运行态每局新建。
- 状态转换方法必须幂等，返回“是否首次变化”和结构化结果。
- 不直接依赖 `EnhancedClientSession`，不直接发包。
- 不把协议包号、handler 地址或客户端偏移写进业务模型。
- 相同机制共享模型，例如条件目标、计量条、限时条件和条件召唤，不为每张副本复制一套字段。

### 4. 机制协调器

目录：`Server/DfoServer/Network/Handlers/Dungeon/`

职责：连接单局业务、客户端命令和协议 sender。

统一生命周期名称：

| 生命周期 | 作用 |
| --- | --- |
| `RunCreated` | 初始化本局可用机制运行态 |
| `SelectionConfigured` | 根据 maze、任务和 Boss 候选完成选本配置 |
| `SelectionCloned` | 组队成员复制配置，运行态必须是独立副本 |
| `MoveMapPreparing` | 计算传送目标和地图覆盖，不发送 START_MAP |
| `StartMapPreparing` | 向房间模板追加配置驱动的 actor |
| `RoomStateCreated` | 登记房间级机制状态 |
| `StartMapSent` | START_MAP 发出后发送依赖客户端房间容器的通知 |
| `PassiveObjectDestroyed` | 推进由被动物件消失触发的配置动作链，不直接结算 |
| `MonsterKilled` | 推进击杀、能量、条件目标、塔和特殊 Boss 逻辑 |
| `CharacterDied` | 区分普通死亡与配置驱动的脚本死亡终点 |
| `DungeonCleared` | 通关瞬间推进跨图状态和一次性奖励预留 |
| `ResultPreparing` | 结算包前生成依赖客户端结算字段的业务结果 |
| `RunEnding` | 取消定时器、清临时 Buff、结束单局 |
| `CommandReceived` | 分发已确认的副本专用 CMD |

Handler 中同一生命周期只能有一个公共机制入口。本地击杀和组队转发必须调用同一个 `MonsterKilled` 入口，不能分别维护业务清单。

### 5. Protocol Builder 与 Sender

目录：

```text
Server/DfoServer/Network/Builders/Dungeon/
Server/DfoServer/Network/Handlers/Dungeon/*NotificationSender.cs
```

职责：

- Builder 只按已确认字段布局生成 body。
- Sender 统一选择 `cmd`、`type`，构造 envelope 并发送。
- 业务协调器只传递结构化参数、机制名和原因。

禁止：

- 在多个机制类中重复保存同一个包号。
- 在 Handler 内手写副本专用 body。
- 因包名相似就猜字段。
- 将旧台服 handler 地址直接套到当前 S4A14 客户端。

协议字段来源必须写入代码注释和 PR 描述：当前客户端 DMP/handler、同版本抓包、跨版本对照或受控枚举测试。

## 三、机制匹配规则

### 1. 可接受的匹配依据

- DGN/MAP 明确词条及其结构化内容。
- ETC 中的副本列表、权重、计量值和时间。
- LST 注册关系及资源路径表达的机制类型。
- 任务条件反向解析出的 dungeon/map/monster 关系。
- 多个 PVF 特征的组合，例如 maze 标记 + 事件坐标 + 条件被动物件 + named monster。

### 2. 不可接受的匹配依据

```csharp
if (run.DungeonId == 152) { ... }
if (mapId == 14082 || mapId == 14084) { ... }
if (monsterCode == 58502) { ... }
```

协议常量、已确认的固定运行时 key 和无配置来源的客户端 ABI 常量可以保留，但必须集中定义并注明证据。内容资产 ID 应优先从 PVF/ETC 或已有数据服务解析。

### 3. 配置失败

- 缺字段：关闭该机制，不扩大匹配范围。
- 解析异常：记录 dungeon、maze、map、字段路径和异常。
- 不允许用“常见副本 ID”兜底。
- 不允许在异常时对所有房间发送试验包。

## 四、运行态与并发

1. `DungeonRun.SyncRoot` 保护房间集合、击杀集合以及同局并发修改。
2. 锁内禁止 `await`；锁内只计算和预留结果，发包及数据库调用在锁外完成。
3. 一次性行为必须在发送前原子预留，例如：
   - 条件门通知；
   - 隐藏 Boss 召唤；
   - 结算奖励；
   - 房间事件推进；
   - 定时器完成。
4. ClockService 回调必须同时校验 session、`CurrentRun` 引用和 timer version，旧局回调不得作用于新局。
5. 组队成员复制配置时，不共享可变计数器、HashSet 或运行态实例。

## 五、结算规则

普通结算入口仍由 `DungeonSettlementHandler.TryClearDungeon` 统一执行。机制模块不得直接复制以下流程：

- `ENABLE_CLEAR_DUNGEON`；
- 秘密商店；
- 任务清图同步；
- 结算阶段切换；
- 翻牌和经验结算。

机制只返回结构化通关请求：

```text
ShouldClear + Reason + BossCode
```

要求：

- 通关请求可重复产生，最终入口必须幂等。
- 默认 Boss 终点、PVF clear condition、任务 NPC、隐藏 Boss 等来源都汇合到同一个结算入口。
- 依赖 `SET_PLAY_RESULT` 字段的奖励在 `ResultPreparing` 计算，但奖励预留必须单局一次。

## 六、组队规则

1. 队长选出的 maze、Boss 房和覆盖图可以复制给成员。
2. 每名成员拥有独立 `DungeonRun`、`RoomState`、计量条、Buff 激活列表和一次性标记。
3. 击杀广播后，每名成员都必须调用统一 `MonsterKilled` 生命周期。
4. 掉落是否共享由掉落系统决定，不能因为机制状态同步而复制个人掉落。
5. 任何新机制至少验证单人、本地击杀者和非击杀队友三条路径。

## 七、试包与逆向

试验代码必须满足：

- 类名或开关明确包含 `Probe/Experimental`。
- 默认生产路径不启用，或匹配范围严格受控。
- 日志打印版本、cmd/type、body、触发配置和防重状态。
- 真机结论确认后，删除试验枚举并改成正式业务语义。

逆向脚本必须在文件头写明：

```text
用途、输入文件、目标客户端/服务端版本、输出、已知限制
```

已有脚本能完成同类工作时优先扩展，禁止无说明地重复造工具。

## 八、禁止新增的写法

禁止新增：

- Handler 直接判断单个副本、地图或怪物 ID。
- Handler 直接维护特殊副本计数器。
- 多个模块重复构造同一个协议 envelope。
- 业务模块直接调用普通结算包序列。
- 以静态字典保存单局或单房状态。
- 组队转发只推进普通清房而漏掉机制生命周期。
- 把未知 CMD/NOTI 注册后直接赋予业务含义。
- 为修一个副本修改所有副本的默认清房规则。

## 九、接入检查清单

提交前逐项确认：

1. 机制由什么 PVF/ETC 词条识别？
2. 是否存在 dungeon/map/monster ID 硬编码？
3. 配置和运行态是否分离？
4. 单局状态是否归 `DungeonRun`，单房状态是否归 `RoomState`？
5. Handler 是否只调用统一生命周期入口？
6. 单人和组队是否走相同业务方法？
7. 一次性发送和奖励是否原子防重？
8. 是否复用现有 Builder/Sender/结算入口？
9. 定时任务是否使用 `ClockService` 并校验局版本？
10. 解析失败是否关闭机制而不是扩大范围？
11. 协议字段来源是否记录？
12. 是否补 parser、纯业务、协议和共享路径回归？

最低回归范围：

```text
special-dungeon
special-dungeon-part2
special-dungeon-part3
dungeon-map-fallback
dungeon-room-progress
dungeon-run
clear-map-quest
dungeon-combat-party
dungeon-item-drop
quest-clear / quest-trigger-counts / quest-item-flow
death-tower 全套
tower-of-despair-progress
```

涉及共享入口时，原有普通副本和塔类测试全部通过才算完成。

## 十、当前迁移边界

以下是当前代码的迁移期现实，不代表新规范：

- `SpecialDungeonModule` 暂时同时包含配置和多种运行态，后续按机制拆分。
- `SpecialDungeonNotifier` 仍同时承担部分业务推进与计量条、条件召唤协议投影；共享的 Buff 和通行条件通知已经拆入统一 sender，后续按实际重复程度继续拆分。
- TimeSpiral 状态暂时直接铺在 `DungeonRun/RoomState`，后续可收进独立状态对象。
- 黄金王结算奖励已经迁移到 `SpecialDungeonSettlementCoordinator`，奖励物品和最大数量由独立掉落配置解析；公共结算 Handler 只调用 `ResultPreparing` 生命周期。
- 条件目标与条件召唤已经按 `[boss room entrance condition]` 的 hunt+summon 组合启用，不再依赖陷落村庄或斯洛特发电站的副本类型白名单。
- Anton 普通征伐保持独立持久化模块，仅通过副本生命周期门面接入，不并入单局特殊地下城状态。

迁移一个旧机制时，先减少旁路和重复入口，不要求为了形式一次性重写全部已验证代码。
