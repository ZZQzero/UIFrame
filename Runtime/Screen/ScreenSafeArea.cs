using System;
using UnityEngine;

namespace UIFrame
{
    /// <summary>
    /// Unity <see cref="Screen.safeArea"/> 的四边 inset（屏幕像素，原点左下）。
    /// </summary>
    public readonly struct ScreenSafeAreaInsets
    {
        public readonly float Left;
        public readonly float Right;
        public readonly float Bottom;
        public readonly float Top;
        public readonly Rect SafeRect;

        public ScreenSafeAreaInsets(float left, float right, float bottom, float top, Rect safeRect)
        {
            Left = left;
            Right = right;
            Bottom = bottom;
            Top = top;
            SafeRect = safeRect;
        }
    }

    /// <summary>
    /// 只读 Unity 的 <see cref="Screen.safeArea"/>。
    /// Editor / 测试可用 <see cref="SetOverride"/> 模拟。
    /// <see cref="Current"/> 只返回缓存，不会 Refresh；由 UIFrameRoot 在方向/尺寸/焦点变化后短窗口刷新。
    /// </summary>
    public static class ScreenSafeArea
    {
        static ScreenSafeAreaInsets _current;
        static Rect? _overrideSafeRect;
        static bool _ready;

        public static event Action Changed;

        public static bool IsReady => _ready;

        /// <summary>最近一次 <see cref="Refresh"/> 的结果。不会读取屏幕或发 <see cref="Changed"/>。</summary>
        public static ScreenSafeAreaInsets Current => _current;

        /// <summary>覆盖 <see cref="Screen.safeArea"/>。传 null 取消覆盖。</summary>
        public static void SetOverride(Rect? safeRect)
        {
            _overrideSafeRect = safeRect;
            Refresh();
        }

        /// <summary>
        /// 重新读取安全区。值未变时不发 <see cref="Changed"/>。
        /// </summary>
        public static bool Refresh()
        {
            var screenW = Screen.width;
            var screenH = Screen.height;
            if (screenW <= 0 || screenH <= 0)
            {
                return false;
            }

            var safe = _overrideSafeRect ?? Screen.safeArea;
            safe = ClampToScreen(safe, screenW, screenH);
            if (_ready && safe == _current.SafeRect)
            {
                return false;
            }

            _current = FromSafeRect(safe, screenW, screenH);
            _ready = true;
            Changed?.Invoke();
            return true;
        }

        /// <summary>清覆盖和缓存，保留监听。不通知 Fitter。</summary>
        public static void Shutdown()
        {
            _overrideSafeRect = null;
            _current = default;
            _ready = false;
        }

        static ScreenSafeAreaInsets FromSafeRect(Rect safe, int screenW, int screenH)
        {
            return new ScreenSafeAreaInsets(
                safe.xMin,
                screenW - safe.xMax,
                safe.yMin,
                screenH - safe.yMax,
                safe);
        }

        static Rect ClampToScreen(Rect safe, int screenW, int screenH)
        {
            var x = Mathf.Clamp(safe.x, 0f, screenW);
            var y = Mathf.Clamp(safe.y, 0f, screenH);
            var w = Mathf.Clamp(safe.width, 0f, screenW - x);
            var h = Mathf.Clamp(safe.height, 0f, screenH - y);
            return new Rect(x, y, w, h);
        }
    }
}
