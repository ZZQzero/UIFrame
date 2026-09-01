using UnityEngine;

namespace UIFrame
{
    /// <summary>
    /// 按安全区把自身 RectTransform 锚到 Canvas 像素矩形内。
    /// 挂在面板的内容节点上，不要挂在 Layer 或会铺满刘海的背景/遮罩上。
    /// 父节点必须是铺满 Canvas 的 Stretch，否则会套两次或坐标不对。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    [AddComponentMenu("UIFrame/Safe Area Fitter")]
    public sealed class SafeAreaFitter : MonoBehaviour
    {
        const DrivenTransformProperties DrivenProperties =
            DrivenTransformProperties.Anchors |
            DrivenTransformProperties.AnchoredPosition |
            DrivenTransformProperties.SizeDelta;

        [SerializeField] bool padLeft = true;
        [SerializeField] bool padRight = true;
        [SerializeField] bool padBottom = true;
        [SerializeField] bool padTop = true;

        RectTransform _rect;
        Canvas _rootCanvas;
        DrivenRectTransformTracker _tracker;
        bool _applying;
        float _appliedXMin = float.NaN;
        float _appliedYMin;
        float _appliedXMax;
        float _appliedYMax;

#if UNITY_EDITOR
        bool _warnedParent;
#endif

        void Awake()
        {
            _rect = (RectTransform)transform;
        }

        void OnEnable()
        {
            if (_rect == null)
            {
                _rect = transform as RectTransform;
            }

            _rootCanvas = null;
            _appliedXMin = float.NaN;
            var canvas = GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                _rootCanvas = canvas.rootCanvas;
            }

            if (_rect != null)
            {
                _tracker.Add(this, _rect, DrivenProperties);
            }
#if UNITY_EDITOR
            WarnIfParentNotStretched();
#endif
            ScreenSafeArea.Changed += Apply;
            Apply();
        }

        void OnDisable()
        {
            ScreenSafeArea.Changed -= Apply;
            _tracker.Clear();
        }

        void OnRectTransformDimensionsChange()
        {
            if (isActiveAndEnabled)
            {
                Apply();
            }
        }

        /// <summary>勾选要避开的边。原生壳已扣除顶底栏时，可关掉 Top/Bottom。</summary>
        public void SetPads(bool left, bool right, bool bottom, bool top)
        {
            padLeft = left;
            padRight = right;
            padBottom = bottom;
            padTop = top;
            if (isActiveAndEnabled)
            {
                Apply();
            }
        }

        /// <summary>只读 <see cref="ScreenSafeArea.Current"/>，不调用 Refresh。</summary>
        public void Apply()
        {
            if (_rect == null)
            {
                _rect = transform as RectTransform;
            }

            if (_applying || _rect == null || !ScreenSafeArea.IsReady)
            {
                return;
            }

            if (_rootCanvas == null)
            {
                var canvas = GetComponentInParent<Canvas>();
                if (canvas != null)
                {
                    _rootCanvas = canvas.rootCanvas;
                }
            }

            if (_rootCanvas == null || _rootCanvas.renderMode == RenderMode.WorldSpace)
            {
                return;
            }

            var pixel = _rootCanvas.pixelRect;
            if (pixel.width <= 0f || pixel.height <= 0f)
            {
                Canvas.ForceUpdateCanvases();
                pixel = _rootCanvas.pixelRect;
                if (pixel.width <= 0f || pixel.height <= 0f)
                {
                    return;
                }
            }

            var safe = ScreenSafeArea.Current.SafeRect;
            var xMin = padLeft ? (safe.xMin - pixel.x) / pixel.width : 0f;
            var yMin = padBottom ? (safe.yMin - pixel.y) / pixel.height : 0f;
            var xMax = padRight ? (safe.xMax - pixel.x) / pixel.width : 1f;
            var yMax = padTop ? (safe.yMax - pixel.y) / pixel.height : 1f;

            xMin = Mathf.Clamp01(xMin);
            yMin = Mathf.Clamp01(yMin);
            xMax = Mathf.Clamp01(Mathf.Max(xMin, xMax));
            yMax = Mathf.Clamp01(Mathf.Max(yMin, yMax));

            if (!float.IsNaN(_appliedXMin) &&
                Mathf.Approximately(_appliedXMin, xMin) &&
                Mathf.Approximately(_appliedYMin, yMin) &&
                Mathf.Approximately(_appliedXMax, xMax) &&
                Mathf.Approximately(_appliedYMax, yMax))
            {
                return;
            }

            _applying = true;
            _rect.anchorMin = new Vector2(xMin, yMin);
            _rect.anchorMax = new Vector2(xMax, yMax);
            _rect.offsetMin = Vector2.zero;
            _rect.offsetMax = Vector2.zero;
            _appliedXMin = xMin;
            _appliedYMin = yMin;
            _appliedXMax = xMax;
            _appliedYMax = yMax;
            _applying = false;
        }

#if UNITY_EDITOR
        void WarnIfParentNotStretched()
        {
            if (_warnedParent || _rect == null)
            {
                return;
            }

            _warnedParent = true;
            var parent = _rect.parent as RectTransform;
            if (parent == null)
            {
                return;
            }

            if (Mathf.Approximately(parent.anchorMin.x, 0f) &&
                Mathf.Approximately(parent.anchorMin.y, 0f) &&
                Mathf.Approximately(parent.anchorMax.x, 1f) &&
                Mathf.Approximately(parent.anchorMax.y, 1f) &&
                parent.offsetMin == Vector2.zero &&
                parent.offsetMax == Vector2.zero)
            {
                return;
            }

            Debug.LogWarning(
                "[UIFrame] SafeAreaFitter 的父节点不是铺满 Canvas 的 Stretch，可能套两次安全区。",
                this);
        }
#endif
    }
}
