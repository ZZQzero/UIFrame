# Changelog

All notable changes to this package will be documented in this file.

## [Unreleased]

### Added

- Added `GamePool` static facade (`Init` / `Shutdown` / `Service`) as the optional default `GameObjectPoolService`. Named `GamePool` to avoid clashing with `UILoopScrollBase.Pool`. LoopScroll still takes an injected service via `SetPool`.
- Added `UI.Tips` for single-instance Tips-layer panels (no queue / no auto-close).
- Added `UI.Guide` for Guide-layer panels outside the Window / Popup stacks.
- Added `UIPanel.OpenCancellationToken`: cancelled when the current open ends (close or re-open), so cached panels cancel in-flight work without waiting for destroy.

### Changed

- Fail-fast: `UI.Push` / 加载失败 / 未 Register / Camera Stack 失败会抛，不再返回 null。
- Fail-fast: `Despawn` 还错对象会抛；外部 `Destroy` 作废分桶且不可 Remove 修复。
- `OnDespawned` 异常会记录 Error 并继续执行其余清理；Shutdown / Dispose 也会完成全部收尾后报告回调错误。
- UI 打开、SetPackage 与 Camera Stack 不再隐式 Init；重复 Init、非法 Tips 数量和非有限时长会抛。
- `GamePool.Init` 重复调用会抛。已删除 `ForceDispose`。
- `ScreenOrientationManager.Initialize` detects current orientation and syncs Canvas layout only; it no longer writes `Screen.orientation` until Set / Push / Pop / ResetTo.
- Documented Tips / Guide / OpenCancellationToken and LoopScroll size fallback in README and USAGE.

### Fixed

- Cancelled / Shutdown panel opens destroy the instance once and complete with `OperationCanceledException` instead of returning `null` or running `OnDestroyPanel` twice.
- `UI.Init` now rolls back a partially created manager when package binding fails; an old Root destroyed later no longer shuts down a newly initialized manager.
- Open / Toast failure cleanup logs secondary lifecycle errors without replacing the primary exception, including Shutdown triggered from panel callbacks.
- `DespawnDeferred` callback failures leave the queue so they are not retried every frame; `Trim` fails fast on externally destroyed idle instances.
- `GamePool.Init` after a direct `Service.Dispose()` now requires `Shutdown` first, so the owned pool root is not leaked.
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
