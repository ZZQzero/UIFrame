# GameScene 用法

进程内场景入口是 `Game.Scene.GameScene`。业务只通过它加载、激活、卸场；不要直接
`SceneManager.LoadScene`，也不要自己握 YooAsset `SceneHandle`。

和 UI 的 `CloseGroup` 不是同一件事：`GameScene` 管 Unity 场景，`UI.CloseGroup` 关面板。

---

## 1. 启动与关闭

```csharp
GameScene.Init(package);

await GameScene.SwitchAsync("Home");

await GameScene.ShutdownAsync();
```

`Launch` 在资源包装好之后 `Init`。退出时先看 `IsInited` 再 `ShutdownAsync`：未 Init 会抛，
和 `UI.Shutdown`（未 Init 直接返回）不同。

### 注意

- 必须先 Init。未 Init 读 `IsBusy` / `ActiveId` / `Progress` 或调加载 API 都会抛。
- 重复 Init 会抛。
- **`ShutdownAsync` 只清静态状态**（表、`ActiveId`、loader），**不走 YooAsset 卸场**。
  进程结束时由 Unity 拆域。不要为了“对齐 Audio”去补卸场。
- 关闭时若有进行中的加载，会等它结束（失败也吞掉），再清静态。
- 退出路径在调用前先看 `IsBusy`，或 `await WaitForIdleAsync()`。
- `WaitForIdleAsync` 可与加载调用方及其它等待方并发等待同一次操作；只等待结束，原始失败由加载调用方接收，不代表加载成功，也不会自动激活预加载场景。

---

## 2. 怎么选接口

| 场景 | API | 加载 | 激活 | 其它已加载场 |
|------|-----|------|------|----------------|
| 切主场景（YooAsset ↔ YooAsset） | `SwitchAsync` | Additive | 成功后才登记为 Active | 只卸上一份 Active；其它 Additive 留下 |
| 从内置壳进内容场 | `LoadAsync(..., Single)` | Single | 成功后才登记 | 引擎卸掉壳；再清表里其它场 |
| 回 Build Settings 内置壳 | `LoadBuiltinAsync` | Single（`SceneManager`） | 只写 `ActiveId`，**不进 `Loaded`** | 清掉全部 YooAsset 句柄 |
| 重载当前场 | `ReloadAsync` | 在表里走 Yoo Single；否则走内置 Single | 同上 | 清掉旧句柄 |
| 加一块（副本、Chunk） | `LoadAsync(..., Additive)` | Additive | 不激活、不改 `ActiveId` | 留下 |
| 整场替换（YooAsset） | `LoadAsync(..., Single)` | Single | 成功后才登记 | 先卸表里其它场 |
| 预先加载、稍后亮 | `PreloadAsync` | 指定 mode，`allowSceneActivation = false` | 不激活 | — |
| 亮已加载 / 预加载 | `ActivateAsync` | 必须已在表里 | 成功后才改 `ActiveId` | Single 激活成功后再清其它句柄 |
| 卸一块 | `UnloadAsync` | — | 若是 Active 则 `ActiveId = null` | — |

```csharp
await GameScene.SwitchAsync("Home");
await GameScene.LoadAsync("Chunk", LoadSceneMode.Additive);
await GameScene.SwitchAsync("Battle");   // Chunk 还在，只卸 Home

await GameScene.PreloadAsync("Boss", LoadSceneMode.Single);
await GameScene.ActivateAsync("Boss");   // Single：先激活，再清其它句柄
```

内置首场景（不热更的 Launch / Boot）不要放进 YooAsset。进出内容场：

```csharp
await GameScene.LoadAsync("Ludo", LoadSceneMode.Single);
await GameScene.LoadBuiltinAsync("Launch");
await GameScene.ReloadAsync();
```

`LoadBuiltinAsync` 之后 `ActiveId` 是壳名，但 `IsLoaded(壳名)` 为 false。不要对壳调
`UnloadAsync` / `SwitchAsync("Launch")`。

若 `ActiveId` 已是内置壳（不在 `Loaded`），`SwitchAsync` 会拒绝执行；从壳进入内容场请显式使用
`LoadAsync(..., Single)`。冷启尚未写过 `ActiveId` 时，`SwitchAsync` 仍是 Additive，也不会自动替换内置壳。

进度：

```csharp
await GameScene.SwitchAsync("Battle", p => loadingBar.Set(p));
float p = GameScene.Progress; // 与回调同一值；操作结束归 0
```

---

## 3. 登记时机

正常路径：**加载成功 → 激活成功 → 再写入 `Loaded` / `ActiveId`。**

未激活成功的场不算已加载，同一地址可以再进。内置壳只更新 `ActiveId`，不进 `Loaded`。

| 失败点 | 表 / Active | 引擎侧 |
|--------|-------------|--------|
| `Load` 失败 | 上一份 Active 不动 | 不会去卸上一份 |
| `Switch` / `LoadSingle` 激活失败 | 新场**不登记** | 未登记的 handle 会 `Unload` 丢掉 |
| 已登记场 `Activate` 失败 | **不从字典摘** | 仍占着那一场 |
| `Switch` 卸旧失败 | 新已是 Active，旧仍在表里 | 旧场还在 |
| `LoadBuiltin` 成功 | `Loaded` 清空 | 引擎 Single 已卸掉 Yoo 场 |

`LoadSingle` 会在激活前卸掉表里其它场（新地址可以还不在表里）。激活失败时旧场可能
已经卸完，`ActiveId` 为 null，新场未登记。清掉错误后可以再 `Load` 同一地址。

未登记 handle 的卸场再失败只打日志，仍把原来的激活异常抛给等待方。

失败不做回滚：已经卸掉的旧场不会自动再加载。

---

## 4. 约束

- 空 / 空白地址抛 `ArgumentException`。
- 进行中禁止再 `Switch` / `Load` / `Preload` / `Activate` / `Unload` / `LoadBuiltin` / `Reload`（`IsBusy`）。
- 存在尚未激活的预加载场景时，禁止新的 `Switch` / `Load` / `Preload` / `LoadBuiltin` / `Reload` 和所有 `Unload`。先显式 `ActivateAsync`，不自动激活或释放唯一可激活句柄。
- 已加载（含预加载）的地址不能再 `Switch` / `Load` / `Preload`。预加载要用 `ActivateAsync`；重开当前场用 `ReloadAsync`。
- 未加载不能 `Activate` / `Unload`（内置壳未登记，不能 `UnloadAsync`）。
- 比较地址用序数（区分大小写）。

---

## 5. 和 UI 的边界

```csharp
await GameScene.LoadAsync("Battle", LoadSceneMode.Single);
UI.CloseGroup(UIGroup.Scene, destroy: true);
```

| 需求 | 用 |
|------|-----|
| 切 / 加 / 卸 Unity 场景 | `GameScene` |
| 关掉本场景组面板 | `UI.CloseGroup(UIGroup.Scene)` |
| 场景 BGM | `GameAudio`，不要在 Scene 里播 |
| 场景加载进度条 | `SwitchAsync` / `LoadAsync` / `LoadBuiltinAsync` 的进度回调 |
