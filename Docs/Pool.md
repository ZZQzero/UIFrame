# 高性能通用对象池

对象池分为两层：

- `ManagedObjectPool<T>`：纯托管对象池，支持工厂和生命周期回调。
- `GameObjectPoolService`：按 YooAsset location 分桶的 Prefab 实例池。

对象池不负责初始化 YooAsset，也不会替代 UIFrame 已有的 `UIPanel`
缓存。建议用于 `UIItem`、循环列表项、特效、弹道等多实例对象。

进程内默认入口是静态类 `GamePool`（避免和 `UILoopScrollBase.Pool` 撞名）。
测试和隔离场景请直接 `new GameObjectPoolService`。
LoopScroll / UIItem 仍通过 `SetPool` 注入，不要在框架内部写死 `GamePool.Service`。

```csharp
GamePool.Init(package, persistRoot); // persistRoot 须比 UI.Shutdown 更久，不要用 UIFrameRoot
SetPool(GamePool.Service);

UI.Shutdown();
GamePool.Shutdown();
```

已 Init、或上次直接 `Dispose` 了 `Service` 还没 `Shutdown` 时再 `Init` 会抛。未 Init 时读 `GamePool.Service` 也会抛。不要绕过 `Shutdown` 去 `Dispose` 默认池。未 Init 时 `GamePool.Shutdown` 是空操作。

`persistRoot` 由宿主提供常驻节点（例如 `DontDestroyOnLoad` 的 Launch）。不要把池根
挂在 UIFrameRoot 下：退出顺序是先 `UI.Shutdown`（面板 `OnDestroyPanel` 还要还池），
再 `GamePool.Shutdown`。

## 纯托管对象

```csharp
var pool = new ManagedObjectPool<MyData>(
    create: () => new MyData(),
    onRent: item => item.Activate(),
    onReturn: item => item.Reset(),
    onDestroy: item => item.Dispose(),
    options: new ManagedPoolOptions(initialCapacity: 16, maxSize: 256));

MyData data = pool.Get();
// 使用 data
pool.Release(data);
```

对于无参数构造且实现 `IManagedPoolable` 的高频对象，可以使用无字典查询的
静态泛型入口：

```csharp
MyMessage message = StaticManagedPool<MyMessage>.Get();
StaticManagedPool<MyMessage>.Release(message);
```

`OnReturn` 表示重置以便复用，`onDestroy` 才表示对象被池永久丢弃。不要把两种
语义都放进同一个 `Dispose`。

## YooAsset GameObject

调用方先初始化 `ResourcePackage`，再将它注入池服务：

```csharp
ResourcePackage package = YooAssets.GetPackage("DefaultPackage");
var options = new GameObjectPoolOptions(
    initialCapacity: 16,
    maxSize: 128,
    prewarmPerFrame: 8,
    group: PoolGroup.UI);

var pool = new GameObjectPoolService(package, poolRoot);
await pool.PrewarmAsync("PlayerItem", 32, options, destroyCancellationToken);

PlayerItem item = await pool.SpawnAsync<PlayerItem>(
    "PlayerItem",
    contentRoot,
    cancellationToken: destroyCancellationToken);

pool.DespawnImmediate(item.gameObject);
```

同一 location 第一次使用时确定 `GameObjectPoolOptions`。后续可以不再传配置；如果显式
传入不同配置，服务会抛出异常，避免调用顺序悄悄改变池行为。

已完成首次加载或预热后，可以使用同步入口：

```csharp
if (!pool.TrySpawn("PlayerItem", contentRoot, out PlayerItem item))
{
    // location 尚未加载；先 await PrepareAsync、PrewarmAsync 或 SpawnAsync。
}
```

`TrySpawn` 只查询已经存在的分桶。池内有闲置实例时直接复用，池空时通过已持有
的 Prefab Handle 同步实例化；location 尚未加载或仍在加载时返回 `false`，不会
偷偷触发同步 YooAsset 加载。该接口适合必须立即返回 Cell 的循环列表
`GetObject`。**`false` 只表示分桶尚未准备**，不会抛。空 location、错线程、
已 Dispose、在 `IPoolable` 回调里取还，会抛。`TrySpawn<T>` 取出后缺组件会抛，
实例已是取出状态，不还回池。还错对象是 `Despawn` 的事，不是 `TrySpawn`。
外部 `Destroy` 了池对象时，`OnDestroy` 打 Error 并从活跃/闲置集合摘掉；分桶
还在，下一次 Spawn 拿出还活着的实例或新建。循环列表的 `GetObject` / `SpawnLoaded`
在尚未 Prepare 时抛，不要和 `TrySpawn` 的 `false` 混为一谈。

`IPoolable` 回调规则：

1. 获取时重置父节点及局部变换，激活对象，然后按组件顺序调用 `OnSpawned`。
2. 归还时按相反顺序调用 `OnDespawned`，隐藏对象并移动到池根节点。
3. 回调组件列表仅在实例首次创建时通过
   `GetComponentsInChildren<IPoolable>(true)` 扫描并缓存，稳态取还不会重复查询。
4. 某个 `OnDespawned` 抛错时立刻停，其余回调不跑；实例不还回闲置区，仍留在活跃集合和场景里，这次 `Despawn` 把异常抛给调用方。
   `OnSpawned` 抛错不回滚，半成品留在场景里且已是取出状态，这次 Spawn 仍抛给调用方；之后可以正常 `DespawnImmediate`。
   回调里不能再取还、预热、拆桶或 Dispose。
5. 缺组件时抛错，已取出的实例不还回池。

## 显式回收与分组

`PoolGroup` 提供 `Default`、`Role`、`UI` 和 `Effect`。不同 location 仍有独立
容量与资源句柄，但闲置实例会挂到对应的 `[Group]` 子节点，便于层级观察和批量
管理。

```csharp
// 核心回收始终在当前调用中完成。
pool.DespawnImmediate(item.gameObject);

// 仅在明确需要避开当前帧 UI 重建时，延迟到 LastPostLateUpdate。
pool.DespawnDeferred(item.gameObject);

// 回收组内所有活跃实例。
pool.DespawnGroup(PoolGroup.Effect);

// 组内仍有活跃对象时返回 false；明确终止时可强制移除。
bool removed = pool.TryRemoveGroup(PoolGroup.UI);
pool.TryRemoveGroup(PoolGroup.UI, force: true);
```

`Despawn` 与 `DespawnImmediate` 都是同步回收。还错对象、重复还、在 `IPoolable`
回调里还，都会抛。`DespawnDeferred` 延迟到 LastPostLateUpdate；等待期间再
`DespawnDeferred` 同一对象会抛，但可以用 `DespawnImmediate` 立刻还。
`DespawnGroup(..., deferred: true)` 对已经在排队的实例会跳过。延迟回收按条还：
无效票（已 Immediate、已 Destroy、桶已拆）丢掉；某条 `OnDespawned` 失败则打日志、该条不还栈，其余继续。
循环列表不得使用延迟回收。

## 加载、取消与释放

- 同一 location 的并发请求共享一次 YooAsset 加载。
- 单个调用的 `CancellationToken` 只取消该调用的等待，不会取消其他等待者。
- 即使最后一个等待者取消，已经开始的共享加载仍会完成并保留分桶，直到主动移除
  或释放服务。
- 每个分桶持有一个 `AssetHandle`，直到分桶真正移除。
- `TryRemovePool` 在仍有活跃实例、正在加载或正在跨帧预热时返回 `false`。
  回调里调用会抛。
- `TryRemoveGroup` 按组释放闲置实例和句柄；组内正在加载或仍有活跃实例时拒绝
  普通移除。预热进行中时即使传入 `force: true` 也会拒绝移除。
- `Trim(location, count)` 将闲置实例收缩到指定数量，但不释放 Prefab 句柄。
- `TryDispose()` 在存在活跃实例、加载任务或预热任务时返回 `false`。回调里调用会抛。
- `Dispose()` 会终止活跃实例并释放每个已建立分桶的 Prefab Handle。
- 池化实例不得由业务代码直接 `Destroy`，应统一调用 `Despawn`。外部 `Destroy`
  打 Error，并从活跃集合和闲置栈摘掉。下一次 `Spawn` / `Trim` / 预热只看到还活着的实例。

`GameObjectPoolService` 的公开操作必须从创建它的 Unity 主线程调用。自定义
`IPrefabProvider` 可以在后台线程完成加载；服务会在创建分桶或操作 Unity 对象前
切回主线程。

主线程检查和托管池 `CollectionCheck` 由 `UIFrameSafety` 控制。默认 Editor /
Development 打开，Release 关闭。要在正式包里打开检查，在创建池、调用红点之前
设置 `UIFrameSafety.ThreadChecks` / `CollectionChecks`。已经建好的池不会因为
之后改开关而改变行为。

如果未传入 `poolRoot`，服务会创建 `[GameObjectPool]` 根节点，并在释放服务时
销毁。跨场景使用时，应由调用方提供自己的常驻根节点并管理其生命周期。

## UIFrame 边界

UIFrame 的 `UILoader` 已经负责 `UIPanel` 的 YooAsset 句柄，`UIManager` 也会在
面板关闭时缓存实例。Toast 关掉后走 Tips 自己的按类型闲置列表，不要再将
`UIPanel` 放入本对象池，否则会产生双重所有权。

循环列表必须采用“异步准备、同步获取、同步归还”。默认源是 `LoopScrollPoolSource`，
由 `UILoopScrollBase` / `UILoopScrollMultiBase` 在 `OnCreate` 里接到 `LoopScrollRect`。
列表面板只实现 `ProvideData`（多 Prefab 再实现 `GetCellLocation`），不要自己写
`GetObject` / `ReturnObject`。

列表面板在 `OnOpen` 里做 Prepare 时，应传入 `OpenCancellationToken`（缓存关闭会取消）。
`destroyCancellationToken` 只在面板真正销毁时取消，缓存关闭不会取消。

```csharp
SetPool(pool);
var cancelled = await PrepareCellsAsync(options, OpenCancellationToken)
    .SuppressCancellationThrow();
if (cancelled) return;
// 或 PrewarmCellsAsync 预热可见数量。

ScrollRect.totalCount = items.Count;
ScrollRect.RefillCells();
```

`GetObject` 内部走 `TrySpawn`：分桶已准备则同步取出或扩容；尚未 Prepare 时抛出
明确异常，避免 LoopScroll 对空引用取 `transform`。单 Prefab 的 `PrepareCellsAsync` /
`PrewarmCellsAsync` 使用 `GetCellLocation(0)`。`ReturnObject` 走
`DespawnImmediate`。`OnClose` 和 `OnDestroyPanel` 会 `ClearCells`
把 Cell 还回池，并清掉 LoopScroll 的 temp pool 计数，因此缓存后再 `RefillCells`
不会对空 Content 取子节点。
`SpawnLoaded` 仍可用于其它必须立即拿到实例的同步路径。池空时两者都会基于已加载
Handle 同步扩容，因此快速滑动不会返回空；可见数量变化大时用 `PrewarmAsync`
分摊实例化。列表数据必须在每次展示时重新绑定，不能依赖首次创建状态。

## 性能检查

建议在目标设备 Development Build 中，用 Unity Profiler 对固定次数操作比较：

1. 预热后连续执行 `SpawnLoaded/DespawnImmediate`。
2. 对照连续执行 `Instantiate/Destroy`。
3. 检查 `GC.Alloc`、主线程耗时和实例峰值。

稳态托管池 `Get/Release` 不创建租约对象、集合或闭包。GameObject 层的首次加载
与首次实例化仍有成本，应通过预热分摊；不要为了提高命中率设置过大的
`maxSize`。它只限制闲置缓存，不限制同时活跃实例；长期不用的 location 应主动
`Trim` 或移除。
