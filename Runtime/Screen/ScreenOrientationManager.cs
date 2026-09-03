using System;
using System.Collections.Generic;
using UnityEngine;

namespace UIFrame
{
    public static class ScreenOrientationManager
    {
        static readonly Stack<GameScreenOrientation> Stack = new Stack<GameScreenOrientation>();
        static bool _initialized;

        /// <summary>当前生效的方向。</summary>
        public static GameScreenOrientation Current { get; private set; } = GameScreenOrientation.Portrait;

        /// <summary>为 true 时，方向变更会通过 <see cref="CanvasLayoutChanged"/> 通知 Canvas 同步布局。</summary>
        public static bool SyncCanvasLayout { get; set; } = true;

        /// <summary>方向已应用。UIFrameRoot 通过该事件同步 Canvas 参考分辨率。</summary>
        public static event Action<GameScreenOrientation> CanvasLayoutChanged;

        /// <summary>
        /// 初始化方向状态并同步当前 Canvas 布局，不修改 Unity 的屏幕方向配置。
        /// 只有显式调用 Set / Push / Pop / ResetTo 才会写入 Screen.orientation。
        /// </summary>
        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Current = DetectCurrentOrientation();
            if (SyncCanvasLayout)
            {
                CanvasLayoutChanged?.Invoke(Current);
            }
        }

        public static void Shutdown()
        {
            Stack.Clear();
            CanvasLayoutChanged = null;
            SyncCanvasLayout = true;
            Current = GameScreenOrientation.Portrait;
            _initialized = false;
        }

        public static void SetPortrait() => Set(GameScreenOrientation.Portrait);

        public static void SetLandscape() => Set(GameScreenOrientation.Landscape);

        /// <summary>直接设置为指定方向（不入栈）。</summary>
        public static void Set(GameScreenOrientation orientation)
        {
            if (MarkInitialized())
            {
                ApplyInternal(orientation, force: true);
                return;
            }

            ApplyInternal(orientation, force: false);
        }

        /// <summary>压入新方向。离开时调用 <see cref="Pop"/> 恢复。</summary>
        public static void Push(GameScreenOrientation orientation)
        {
            var initializedNow = MarkInitialized();
            Stack.Push(Current);
            ApplyInternal(orientation, force: initializedNow);
        }

        /// <summary>弹出并恢复上一方向。栈空时回退到竖屏。</summary>
        public static void Pop()
        {
            var initializedNow = MarkInitialized();
            var hasPrevious = Stack.Count > 0;
            var next = hasPrevious ? Stack.Pop() : GameScreenOrientation.Portrait;
            ApplyInternal(next, force: initializedNow || !hasPrevious);
        }

        /// <summary>清空方向栈并设为指定方向。</summary>
        public static void ResetTo(GameScreenOrientation orientation)
        {
            Stack.Clear();
            MarkInitialized();
            ApplyInternal(orientation, force: true);
        }

        /// <summary>仅重新通知 Canvas 同步布局（不改 Screen.orientation）。</summary>
        public static void SyncCanvasLayoutNow()
        {
            Initialize();
            CanvasLayoutChanged?.Invoke(Current);
        }

        public static bool IsLandscape(GameScreenOrientation orientation)
        {
            return orientation == GameScreenOrientation.Landscape
                   || orientation == GameScreenOrientation.AutoLandscape;
        }

        public static GameUICanvasLayout GetCanvasLayout(GameScreenOrientation orientation)
        {
            return IsLandscape(orientation)
                ? GameUICanvasLayout.Landscape
                : GameUICanvasLayout.Portrait;
        }

        static bool MarkInitialized()
        {
            if (_initialized)
            {
                return false;
            }

            Initialize();
            return true;
        }

        static GameScreenOrientation DetectCurrentOrientation()
        {
            switch (Screen.orientation)
            {
                case ScreenOrientation.LandscapeLeft:
                case ScreenOrientation.LandscapeRight:
                    return GameScreenOrientation.Landscape;

                case ScreenOrientation.Portrait:
                case ScreenOrientation.PortraitUpsideDown:
                    return GameScreenOrientation.Portrait;

                default:
                    return Screen.width > Screen.height
                        ? GameScreenOrientation.Landscape
                        : GameScreenOrientation.Portrait;
            }
        }

        static void ApplyInternal(GameScreenOrientation orientation, bool force)
        {
            if (!force && Current == orientation)
            {
                return;
            }

            Current = orientation;
            ApplyToUnity(orientation);
            if (SyncCanvasLayout)
            {
                CanvasLayoutChanged?.Invoke(Current);
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log($"[ScreenOrientation] Applied: {orientation}");
#endif
        }

        static void ApplyToUnity(GameScreenOrientation orientation)
        {
            Screen.autorotateToPortrait = false;
            Screen.autorotateToPortraitUpsideDown = false;
            Screen.autorotateToLandscapeLeft = false;
            Screen.autorotateToLandscapeRight = false;

            switch (orientation)
            {
                case GameScreenOrientation.Portrait:
                    Screen.autorotateToPortrait = true;
                    Screen.orientation = ScreenOrientation.Portrait;
                    break;

                case GameScreenOrientation.Landscape:
                    Screen.autorotateToLandscapeLeft = true;
                    Screen.autorotateToLandscapeRight = true;
                    Screen.orientation = ScreenOrientation.LandscapeLeft;
                    break;

                case GameScreenOrientation.AutoPortrait:
                    Screen.autorotateToPortrait = true;
                    Screen.autorotateToPortraitUpsideDown = true;
                    Screen.orientation = ScreenOrientation.AutoRotation;
                    break;

                case GameScreenOrientation.AutoLandscape:
                    Screen.autorotateToLandscapeLeft = true;
                    Screen.autorotateToLandscapeRight = true;
                    Screen.orientation = ScreenOrientation.AutoRotation;
                    break;

                default:
                    Screen.autorotateToPortrait = true;
                    Screen.orientation = ScreenOrientation.Portrait;
                    break;
            }
        }
    }
}
