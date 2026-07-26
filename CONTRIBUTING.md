# 贡献指南

感谢你对项目的关注！为了提高协作效率，请在提交 PR 前阅读以下内容。

## 提交前准备

1. **Rebase 到最新 main**：提交前确保你的分支已经 rebase 到最新的 main，避免冲突
2. **确保编译通过**：`dotnet build Server/DfoServer/DfoServer.csproj -c Debug`
3. **跑相关自测**：改动涉及背包/货币/任务时，跑对应的 `--selftest-*` 自测（列表见 Program.cs）
4. **一个 PR 只做一件事**：不要在一个 PR 里混合多个不相关的功能或修复
5. **副本机制改动**：先阅读 `Docs/DungeonMechanismIntegration.md`，按统一生命周期、PVF/ETC 配置识别和共享回归清单接入

## 工程红线

以下每条都对应过真实事故，审查时会重点盯：

1. **Handler 层禁止直连 SQL**（不 import `Microsoft.Data.Sqlite`）——协议层只做
   解析→Store→ACK→刷新，数据访问写到 `Game/Inventory/` 的 Store 层
2. **货币列只能经 `CurrencyService.Grant*/TrySpend*`**——禁止 `UPDATE accounts SET cera=...`
   之类绝对值写入或"先读再算再写"（曾导致点券清零）
3. **删物品分两层**——handler/业务层调 Store 的现成方法（如 `TryDeleteItem`）；
   在 Store 层新增删除逻辑时必须复用 `InventoryDbPrimitives.DeleteItem`（内含排序锁清理），
   禁止自写 `DELETE FROM character_items`（曾留下孤儿排序锁）
4. **禁止 `INSERT OR REPLACE INTO character_items`**——REPLACE 会把未列出的列清成默认值
5. 涉及多张表的复合写必须在**同一事务**内（参考收集箱的 lambda 注入写法）
6. 扣费失败必须返回失败 ACK，禁止兜底吞掉错误
7. **不要自己 `new Random()`**——项目里的随机数只有两个合法来源：
   - 掉落这类"客户端会拿同一个种子自己再算一遍"的随机，必须用当前房间的
     `DnfLcg`（种子已经随开图包发给客户端，双方按同一序列取数；换了来源，
     客户端算出来的结果就和服务端对不上了）；
   - 其余由服务端自己拍板、结果只体现在后续下发数据里的随机（选哪套迷宫、
     抽哪只冠军怪、出哪个神秘商店 NPC 等），用 `Infrastructure/ServerRandom`。
   另外 `System.Random` 不是线程安全的：两个线程同时调用会把它弄坏，
   坏掉之后会一直返回 0——这也是要收口到 ServerRandom（内部加锁）的原因
8. **需要"每隔一段时间做某事"时，注册进 `Infrastructure/ClockService`，
   不要自己开线程写 while 循环**。时钟回调里只做三类事：读数据、结算、
   给在线玩家发包。不要把"只有时钟跑过才正确"的数据写进库——服务器停机
   再开时，时钟不会补跑错过的时刻，这种数据就永久缺一块（正确做法参考
   每日重置系统：数据在被读到的时候自己判断过期并补算，时钟只负责提醒在线玩家）
9. **改到共享代码时，路过这段代码的旧功能必须复测**——自己的新功能跑通
   不算完：diff 里改过的每个条件、每个分支，原本跑在上面的功能都要重新
   验证一遍且行为不变（有自测的跑自测，没有的实机复测）。如果改变旧行为
   正是本 PR 的目的，在描述里写明改了什么、为什么。曾有 PR 为保护新功能
   收窄了共享的外观刷新条件，导致所有角色穿脱装备后外观不再更新

## 数据库变更

- 新增表：在 `Sqlite/item_schema.sql` 中添加 `CREATE TABLE IF NOT EXISTS`
- 新增列/删列/改约束：**两边都要写**——
  1) `Sqlite/item_schema.sql` 保持"新库的完整最终形态"；
  2) `Sqlite/SqliteMigrations.cs` 的 `Steps` 末尾追加下一个版本号（旧库靠它升级，
     已发布的条目禁止修改）。
  加列用 `EnsureColumns`，删列用 `DropColumnsIfExist`，改约束参考 v6 的表重建模板。
  详细守则见 `SqliteMigrations.cs` 头部注释。

## 协议改动

涉及新增或修改包格式时，请在 PR 描述中简要说明字段来源：
- PVF 数据（标注文件路径）
- 抓包实测（标注包体 hex 示例）
- 推测/参考（说明参考了什么）

## 项目结构

```
Server/DfoServer/
  Game/Inventory/          背包系统（拆分为多个 Store）
  Game/CharacterData/      角色数据 Repository
  Game/Dungeon/            副本逻辑
  Game/Skills/             技能系统
  Network/Handlers/        协议处理（按域拆分子目录）
  Network/Builders/        封包构建
  Network/Protocol/        协议分发
  GameWorld/               PVF 只读数据
```

## 社区交流

Discord: https://discord.gg/3wct6SZp
