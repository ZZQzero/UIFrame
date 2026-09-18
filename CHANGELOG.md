# Changelog

All notable changes to this package will be documented in this file.

## [Unreleased]

### Added

- `GameScene.LoadBuiltinAsync` 成功后卸表里已登记 handle（场已被 Unity 卸掉则只 Release），不登记内置场。
- Added `UI.Tips` for single-instance Tips-layer panels (no queue / no auto-close).
- Added `UI.Guide` for Guide-layer panels outside the Window / Popup stacks.
- Added `UIPanel.OpenCancellationToken`: cancelled when the current open ends (close or re-open), so cached panels cancel in-flight work without waiting for destroy.

### Changed

- UI 绑定回填只按 LocalFileId / 层级路径，找不到不猜节点；失败不半写入，重试仍失败则取消等待。
- UI 回填宿主按类型名精确匹配，找不到不改去猜同节点上的其他脚本。
- 空绑定列表默认不会覆盖仍有字段的 `.Gen.cs`；用 `×` 清空后再写入可以。
- UI 绑定刷新按 `.Gen.cs` 对齐已写入字段；未「写入脚本」的添加会保留。引用空了仍显示未定位。
- `SwitchAsync` throws when `ActiveId` is set but not in the loaded table (builtin shell). Leave the shell with `LoadAsync(..., Single)`.
- `GameScene` preload: `PreloadAsync` stays; handle 只有一次 `ActivateAsync`（预加载先放行再激活），不再拆 `ActivateScene` / `ActivatePreloadedAsync`。
- `ActivateAsync` of a preloaded Single scene now activates first, then drops other handles from the table.
- Fail-fast: `UI.Push` / 加载失败 / 未 Register / Camera Stack 失败会抛，不再返回 null。
- Fail-fast: `Despawn` 还错对象会抛。外部 `Destroy` 打 Error 并从集合摘掉该实例，分桶仍可用，不可靠 Destroy 当还池。
- `OnDespawned` 抛错时：同步 `Despawn` 立刻停、不还栈、异常给调用方；`DespawnDeferred` 打日志、该条不还、其余继续。Shutdown / Dispose 仍完成收尾。
- UI 打开、SetPackage 与 Camera Stack 不再隐式 Init；重复 Init、非法 Tips 数量和非有限时长会抛。
- `GamePool.Init` 重复调用会抛。已删除 `ForceDispose`。
- RedDot 重复绑定同一回调和传入空回调会抛，不再静默忽略。
- `ScreenOrientationManager.Initialize` detects current orientation and syncs Canvas layout only; it no longer writes `Screen.orientation` until Set / Push / Pop / ResetTo.
- `UI.Shutdown` 未 Init 时直接返回，不再当作错误抛出。
- `GameScene`：激活成功后再写入 `Loaded` / `ActiveId`；未登记 handle 激活失败会卸掉再抛原异常。`ShutdownAsync` 只清静态、不卸场。文档见 `Docs/Scene.md`。
- 本工程 `Launch` 的 Timer 初始容量改为显式小配置，不再使用 `LargeGameDefault()`。

### Fixed

- Cancelled / Shutdown panel opens destroy the instance once and complete with `OperationCanceledException` instead of returning `null` or running `OnDestroyPanel` twice.
- `UI.Init` now rolls back a partially created manager when package binding fails; an old Root destroyed later no longer shuts down a newly initialized manager.
- Open / Toast failure cleanup logs secondary lifecycle errors without replacing the primary exception, including Shutdown triggered from panel callbacks.
- `DespawnDeferred` callback failures leave the queue so they are not retried every frame; `Trim` fails fast on externally destroyed idle instances.
- `GamePool.Init` after a direct `Service.Dispose()` now requires `Shutdown` first, so the owned pool root is not leaked.
- RedDot 运行态重置会保留现有监听并刷新为 0，兼容关闭 Domain Reload 的 Play Mode。
- LoopScroll preferred width / height falls back to `RectTransform` size when LayoutUtility returns `<= 0`.

## [1.5.0] - 2026-09-01

### Added

- Added Tips toast policy: `UI.ConfigureTips(maxVisible, maxQueued, defaultDuration)` and `UI.Toast<T>(args, duration?)`.
- Visible toasts share one Tips-layer cap; overflow waits in a queue and loads only when a slot opens.
- A full queue drops the oldest waiting item. `duration <= 0` stays until CloseSelf / Close.
- Closed toasts go into a per-type idle list (same size as maxVisible) and reuse OnOpen without a second OnCreate.
- Added `UIFrameSafety` so thread and collection checks stay compiled and can be toggled at runtime. Editor / Development default on; Release default off.

### Changed

- Toast close no longer always Destroy; Shutdown, ClearCache, and explicit destroy still release instances and handles.
- Toast respects `Register(..., cache: false)` and idle overflow now evicts the oldest idle instance.
- Documented Hud/Push/Popup same-type in-flight Open merge (later Args/Mode win).
- Pool and RedDot thread checks no longer compile out of Release; they follow `UIFrameSafety.ThreadChecks`.

### Fixed

- Illegal `defaultDuration` (NaN / Infinity) falls back to 2 seconds instead of throwing in the auto-close timer.
- Close/CloseGroup copy panels into a local list so `OnClose` cannot wipe the list being iterated.
- Toast open failures after `OnOpen` no longer skip `OnClose` or destroy an instance already returned to idle.
- `maxVisible == 0` rejects new toasts immediately and cancels items already waiting in the queue.

## [1.4.0] - 2026-09-01

### Added

- Added LoopScrollPoolSource so loop-scroll cells use GameObjectPoolService TrySpawn / DespawnImmediate.
- UILoopScrollBase and UILoopScrollMultiBase now provide the default prefab source; panels only implement ProvideData.
- UILoopScrollMultiBase.PrewarmCellsAsync prepares each cell location.

### Changed

- Loop-scroll panels return cells in OnClose and OnDestroyPanel.
- OnCreate is sealed; override OnLoopScrollCreated. Override OnClose / OnDestroyPanel must call base to return cells.
- PrepareCellsAsync / PrewarmCellsAsync use GetCellLocation(0), not only the serialized field.

### Fixed

- ClearCells now resets temp-pool counters so a cached list can RefillCells after close.

## [1.3.0] - 2026-09-01

### Added

- Added ScreenSafeArea cache, Editor override, and SafeAreaFitter that maps anchors from canvas.pixelRect.
- Mask/Guide layers stay full-bleed.

### Changed

- ScreenSafeArea.Current is cache-only; UIFrameRoot refreshes on orientation, canvas resize, and focus, then for two more frames.
- SafeAreaFitter ignores self-triggered RectTransform callbacks and keeps DrivenRectTransformTracker for the enable lifetime.

### Removed

- Unused ScreenSafeArea.Top, Cutouts, and PixelsToOrthoWorld.

## [1.2.0] - 2026-08-26

### Added

- Added reusable single-prefab and multi-prefab loop-scroll panel bases.
- Added concrete horizontal and vertical loop-scroll components.

## [1.1.0] - 2026-08-25

### Added

- Added URP Camera Stack configuration with automatic or caller-provided UI Camera.
- Canvas defaults to Screen Space Camera with a persistent UI Camera and Vertex Color Always In Gamma Space.
- Camera Stack only adds/removes the UI Camera; Base Camera and Canvas references stay unchanged on detach.

## [1.0.0] - 2026-08-25

### Added

- Initial UPM package structure.
- Runtime and Editor assembly definitions.
- Panel lifecycle, stack, cache, YooAsset loading, orientation, and code-generation support.
