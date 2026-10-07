# 游戏存档与背包示例

`GameRepositories.cs` 是可复制到业务程序集的示例，不会因位于 `Samples~` 中而自动进入游戏。它使用通用 `UIFrame.Sqlite`，不依赖照片系统。推荐游戏数据、图库索引、备份任务各自使用数据库文件，分别拥有 schema 和生命周期；它们仍共享原生线程池与磁盘，分库不等于性能隔离。

## 创建与使用

应用负责创建父目录、选择 `CreateNew` 或 `OpenExistingReadWrite`，并持有数据库。只对新库执行 `GameSchema.CreateAsync`；打开既有库执行 `ValidateAsync`。当前 application_id 为 1430669127、schema 为 2。示例不提供 schema 1 的自动迁移，验证不匹配会失败并保留原文件；已有数据的应用需实现并验证自己的显式迁移。

```csharp
var inventory = await InventorySession.OpenAsync(gameDatabase);
await inventory.ApplyAsync("quest:player-1:quest-42", new[] {
    new InventoryChange("gold", 100), new InventoryChange("ore", 3)
});
await inventory.PurchaseAsync("purchase:order-123", "gold", 25, "potion", 2);
await inventory.ApplyAsync("craft:order-124", new[] {
    new InventoryChange("ore", -3), new InventoryChange("sword", 1)
});
long potions = inventory.GetQuantity("potion"); // 只读内存，不访问数据库
```

OperationId 应标识一次业务操作，并在核对时使用同一 ID；失败后不要换个 ID 自动重发。购买同时扣币和加道具，合成同时扣材料和加产物，任何余额不足、整数溢出或重复 ID 都回滚整批，不更新内存或发出事件。`QueryReceiptAsync` 返回原操作的版本及各道具增减、提交后余额，不存在返回 null。配方、价格、权限、背包容量、装备实例与服务器权威校验由业务补充；本地事务不提供防作弊能力。

一次操作接受 1–64 种道具，ID 为 1–512 UTF-8 字节且非空白、不含 NUL，必须能严格编码为 UTF-8（不接受未配对的 UTF-16 代理项），每种道具只能出现一次，Delta 不能为零。构造变更、内存读取和数据库操作遵循同一 ID 校验规则。64 种的限制确保扣增、回执及结果查询留在核心的一批 200 条 SQL 内；超过时明确失败，不能自动拆分原子购买或合成。数量为非负 Int64；缺少某种道具时内存查询返回 0。

## 事务、内存与事件

一个 `InventorySession` 拥有一个内存视图，按顺序完成事务、内存发布和同步 `Changed` 通知。并发命令在 session 内排队，读数量不访问 SQLite。业务应集中通过一个 session 修改背包；若另一 session 或 repository 已提交，旧视图的版本条件使下一次写入明确失败。调用方处理冲突后显式 `ReloadAsync`，成功加载才整体替换视图。分页加载中检测到版本变化也明确失败，不自动重试。所有背包写入必须维护 `inventory_state.revision`；绕过仓库直接修改表会破坏此约定。

`Changed` 在提交和内存发布后调用，不保证 Unity 主线程。处理器应短小，使用同步 `Action`，不要传入 `async void`；将 Unity UI 工作交给主线程。处理器不能同步等待同一 session 的新命令或 Reload，否则会等待自己尚未释放的命令槽位。处理器抛错时向原调用方传播同一异常，停止该次通知的后续处理器；已提交的数据和内存不回滚，后续独立命令仍可执行。

原生事务的成功点是 COMMIT；session 成功还要求内存发布及通知完成。因此调用报错不总意味着数据库未提交，例如提交后的结果映射或事件可能失败。遇到这类情况应查询原 OperationId 回执并显式重新加载内存；`CommitOutcomeUnknown` 需先按核心契约关闭故障库，确认旧所有者结束后重开并核对。排队期间取消不提交；已提交后的迟到取消不能撤销事务。示例不自动重试或补发奖励。

session 不关闭共享数据库。应用结束时停止产生新命令，等待已发起的命令和查询，再关闭数据库；最后按通用模块生命周期关闭 runtime。事件订阅随业务界面生命周期解除。操作回执持续保留以支持去重，正式产品需根据可核对操作的期限制定显式归档策略。

## 存档版本

`SaveRepository.SaveAsync(slot, revision, payload)` 只允许首次写入或严格更大的非负 revision。同版和旧版都报事务条件失败，原 payload 保留；这避免异步旧存档晚到覆盖新状态。`LoadVersionedAsync` 返回 revision 和 payload，缺少槽位返回 null；原 `LoadAsync` 仍只返回 payload。业务负责分配版本和构造完整存档快照。此规则不自动合并多设备或多个作者的存档，也不是跨库事务。

payload 接受 0–`SaveRepository.MaximumPayloadBytes` 字节，当前上限为 1,048,512（1 MiB 减 64 字节），为版本号、列名与结果编码头预留空间，保证成功写入的 payload 可通过带版本查询读取。超过时在提交前明确抛出 `ArgumentOutOfRangeException`，不改变已有存档。更大存档需由业务实现显式分块或文件存储协议；不能只放宽此常量而保留核心 1 MiB 单页限制。该限制不改变 SQLite 通用 API 的参数额度。

## 验证

`Native~/tests/validate_managed.py` 会编译并执行该示例及 `sample_runner.cs`。覆盖版本及大小边界存档、重复操作、余额不足、原子购买 / 合成、并发提交、过期视图、显式重载、通知异常身份与提交状态、排队取消与输入快照、分页背包、64 项边界、整数溢出，以及关闭重开后的存档、背包和回执。
