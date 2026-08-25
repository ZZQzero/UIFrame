using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace UIFrame
{
    /// <summary>独立 Overlay Canvas。层是普通 RectTransform，靠兄弟顺序叠放，避免嵌套 Canvas / 多余 Raycaster。</summary>
    sealed class UIFrameRoot : MonoBehaviour
    {
        static readonly UILayer[] LayerOrder =
        {
            UILayer.Window,
            UILayer.Hud,
            UILayer.Mask,
            UILayer.Popup,
            UILayer.Tips,
            UILayer.Guide,
        };

        readonly Dictionary<UILayer, RectTransform> _layers = new Dictionary<UILayer, RectTransform>();

        Image _maskImage;
        Sprite _whiteSprite;
        CanvasScaler _rootScaler;

        public event Action MaskClicked;

        public static UIFrameRoot Create()
        {
            var go = new GameObject("UIFrameRoot");
            DontDestroyOnLoad(go);
            var root = go.AddComponent<UIFrameRoot>();
            root.Build();
            return root;
        }

        public RectTransform GetLayer(UILayer layer)
        {
            return _layers.TryGetValue(layer, out var t) ? t : null;
        }

        public void SetMaskVisible(bool visible)
        {
            if (_maskImage == null)
            {
                return;
            }

            _maskImage.gameObject.SetActive(visible);
        }

        void ApplyOrientation(GameScreenOrientation orientation)
        {
            var layout = ScreenOrientationManager.GetCanvasLayout(orientation);
            var size = layout == GameUICanvasLayout.Landscape
                ? new Vector2(1920f, 1080f)
                : new Vector2(1080f, 1920f);
            if (_rootScaler != null)
            {
                _rootScaler.referenceResolution = size;
            }
        }

        void Build()
        {
            var canvasGo = new GameObject("CanvasRoot", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            canvas.additionalShaderChannels =
                AdditionalCanvasShaderChannels.TexCoord1
                | AdditionalCanvasShaderChannels.Normal
                | AdditionalCanvasShaderChannels.Tangent;

            _rootScaler = canvasGo.AddComponent<CanvasScaler>();
            _rootScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _rootScaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            _rootScaler.matchWidthOrHeight = 0.5f;
            _rootScaler.referenceResolution = new Vector2(1080f, 1920f);
            canvasGo.AddComponent<GraphicRaycaster>();

            var canvasRt = (RectTransform)canvasGo.transform;
            UIRect.Stretch(canvasRt);

            for (var i = 0; i < LayerOrder.Length; i++)
            {
                var layer = LayerOrder[i];
                _layers[layer] = CreateLayer(canvasGo.transform, layer);
            }

            BuildMask();
            BindOrientation();
        }

        static RectTransform CreateLayer(Transform parent, UILayer layer)
        {
            var go = new GameObject(layer.ToString(), typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            UIRect.Stretch(rt);
            return rt;
        }

        void BuildMask()
        {
            var parent = GetLayer(UILayer.Mask);
            var go = new GameObject("MaskDimmer", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            UIRect.Stretch(rt);

            var tex = Texture2D.whiteTexture;
            _whiteSprite = Sprite.Create(
                tex,
                new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f),
                100f);
            _whiteSprite.name = "UIFrameWhite";

            _maskImage = go.AddComponent<Image>();
            _maskImage.sprite = _whiteSprite;
            _maskImage.color = new Color(0f, 0f, 0f, 0.55f);
            _maskImage.raycastTarget = true;

            var maskButton = go.AddComponent<Button>();
            maskButton.transition = Selectable.Transition.None;
            maskButton.onClick.AddListener(OnMaskClicked);
            go.SetActive(false);
        }

        void OnMaskClicked()
        {
            MaskClicked?.Invoke();
        }

        void BindOrientation()
        {
            ScreenOrientationManager.Initialize();
            ScreenOrientationManager.CanvasLayoutChanged += OnOrientationChanged;
            ApplyOrientation(ScreenOrientationManager.Current);
        }

        void OnOrientationChanged(GameScreenOrientation orientation)
        {
            ApplyOrientation(orientation);
        }

        void OnDestroy()
        {
            ScreenOrientationManager.CanvasLayoutChanged -= OnOrientationChanged;

            if (_whiteSprite != null)
            {
                Destroy(_whiteSprite);
                _whiteSprite = null;
            }
        }
    }
}
