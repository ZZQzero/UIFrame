# Changelog

All notable changes to this package will be documented in this file.

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
