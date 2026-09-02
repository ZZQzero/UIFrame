# Changelog

All notable changes to this package will be documented in this file.

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
