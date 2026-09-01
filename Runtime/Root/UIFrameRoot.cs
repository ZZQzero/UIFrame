using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace UIFrame
{
    /// <summary>UI 根节点。默认 Screen Space Camera + UI Camera，可挂入 URP Camera Stack。</summary>
    sealed class UIFrameRoot : MonoBehaviour
    {
        const int DefaultUiLayerFallback = 5;
        const int CanvasSortingOrder = 100;
        const float CanvasPlaneDistance = 1f;

        static readonly UILayer[] LayerOrder =
        {
            UILayer.Window,
            UILayer.Hud,
            UILayer.Mask,
            UILayer.Popup,
            UILayer.Tips,
            UILayer.Guide,
        };

        static readonly Vector2 PortraitReference = new Vector2(1080f, 1920f);
        static readonly Vector2 LandscapeReference = new Vector2(1920f, 1080f);

        readonly Dictionary<UILayer, RectTransform> _layers = new Dictionary<UILayer, RectTransform>();

        Canvas _canvas;
        RectTransform _canvasRoot;
        CanvasScaler _rootScaler;
        Image _maskImage;
        Sprite _whiteSprite;

        Camera _uiCamera;
        Camera _baseCamera;
        int _uiLayer = -1;
        int _safeAreaBurst;

        public event Action MaskClicked;

        public Camera UICamera => _uiCamera;

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
            if (_maskImage != null)
            {
                _maskImage.gameObject.SetActive(visible);
            }
        }

        /// <summary>将 UI Camera 加入 Base Camera Stack。不改 Base / Canvas 其他参数。</summary>
        public Camera ConfigureURPCameraStack(Camera baseCamera, Camera uiCamera, int uiLayer)
        {
            baseCamera = baseCamera != null ? baseCamera : Camera.main;
            if (baseCamera == null)
            {
                Debug.LogError("[UIFrame] 配置 URP Camera Stack 失败：未找到 Base Camera。");
                return null;
            }

            if (uiCamera == baseCamera)
            {
                Debug.LogError("[UIFrame] 配置 URP Camera Stack 失败：Base Camera 与 UI Camera 不能相同。");
                return null;
            }

            if (!TryResolveUiLayer(ref uiLayer))
            {
                return null;
            }

            var cameraStack = baseCamera.GetUniversalAdditionalCameraData().cameraStack;
            if (cameraStack == null)
            {
                Debug.LogError("[UIFrame] 配置 URP Camera Stack 失败：Base Camera 的 Renderer 不支持 Camera Stack。");
                return null;
            }

            DetachFromBaseCamera();
            EnsureUICamera(uiCamera, uiLayer);

            var uiData = _uiCamera.GetUniversalAdditionalCameraData();
            uiData.renderType = CameraRenderType.Overlay;
            if (!cameraStack.Contains(_uiCamera))
            {
                cameraStack.Add(_uiCamera);
            }

            _baseCamera = baseCamera;
            BindCanvasToUICamera();
            return _uiCamera;
        }

        /// <summary>仅从 Base Camera Stack 移除 UI Camera。Canvas 模式与引用不变。</summary>
        public void DisableURPCameraStack()
        {
            DetachFromBaseCamera();
        }

        void Build()
        {
            _uiLayer = ResolveDefaultUiLayer();
            _uiCamera = CreateUICamera(_uiLayer);
            BuildCanvas();
            BuildLayers();
            BuildMask();
            BindOrientation();
        }

        void BuildCanvas()
        {
            var canvasGo = new GameObject("CanvasRoot", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            canvasGo.layer = _uiLayer;

            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.vertexColorAlwaysGammaSpace = true;
            _canvas.sortingOrder = CanvasSortingOrder;
            _canvas.planeDistance = CanvasPlaneDistance;
            _canvas.additionalShaderChannels =
                AdditionalCanvasShaderChannels.TexCoord1
                | AdditionalCanvasShaderChannels.Normal
                | AdditionalCanvasShaderChannels.Tangent;
            BindCanvasToUICamera();

            _rootScaler = canvasGo.AddComponent<CanvasScaler>();
            _rootScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _rootScaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            _rootScaler.matchWidthOrHeight = 0.5f;
            _rootScaler.referenceResolution = PortraitReference;
            canvasGo.AddComponent<GraphicRaycaster>();

            _canvasRoot = (RectTransform)canvasGo.transform;
            var listener = canvasGo.AddComponent<SafeAreaCanvasListener>();
            listener.Bind(this);
            UIRect.Stretch(_canvasRoot);
        }

        void BuildLayers()
        {
            for (var i = 0; i < LayerOrder.Length; i++)
            {
                var layer = LayerOrder[i];
                _layers[layer] = CreateLayer(_canvasRoot, layer);
            }
        }

        void BuildMask()
        {
            var parent = GetLayer(UILayer.Mask);
            var go = new GameObject("MaskDimmer", typeof(RectTransform));
            go.layer = _uiLayer;

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

            var button = go.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(OnMaskClicked);
            go.SetActive(false);
        }

        RectTransform CreateLayer(Transform parent, UILayer layer)
        {
            var go = new GameObject(layer.ToString(), typeof(RectTransform));
            go.layer = _uiLayer;

            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            UIRect.Stretch(rt);
            return rt;
        }

        void BindOrientation()
        {
            ScreenOrientationManager.Initialize();
            ScreenOrientationManager.CanvasLayoutChanged += ApplyOrientation;
            ApplyOrientation(ScreenOrientationManager.Current);
        }

        void ApplyOrientation(GameScreenOrientation orientation)
        {
            if (_rootScaler == null)
            {
                return;
            }

            var landscape = ScreenOrientationManager.GetCanvasLayout(orientation) == GameUICanvasLayout.Landscape;
            _rootScaler.referenceResolution = landscape ? LandscapeReference : PortraitReference;
            RequestSafeAreaBurst();
        }

        void LateUpdate()
        {
            if (_safeAreaBurst <= 0)
            {
                return;
            }

            _safeAreaBurst--;
            ScreenSafeArea.Refresh();
        }

        void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus)
            {
                RequestSafeAreaBurst();
            }
        }

        void RequestSafeAreaBurst()
        {
            ScreenSafeArea.Refresh();
            _safeAreaBurst = 2;
        }

        void OnMaskClicked()
        {
            MaskClicked?.Invoke();
        }

        void EnsureUICamera(Camera uiCamera, int uiLayer)
        {
            if (uiCamera != null)
            {
                _uiCamera = uiCamera;
            }
            else if (_uiCamera == null)
            {
                _uiCamera = CreateUICamera(uiLayer);
            }

            _uiLayer = uiLayer;
            SetLayerRecursively(_canvasRoot, uiLayer);
        }

        Camera CreateUICamera(int uiLayer)
        {
            var go = new GameObject("UIFrameCamera");
            go.transform.SetParent(transform, false);
            go.layer = uiLayer;

            var camera = go.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Depth;
            camera.orthographic = true;
            camera.cullingMask = 1 << uiLayer;

            var data = camera.GetUniversalAdditionalCameraData();
            data.renderType = CameraRenderType.Overlay;
            data.renderPostProcessing = false;
            data.renderShadows = false;
            return camera;
        }

        void BindCanvasToUICamera()
        {
            _canvas.renderMode = RenderMode.ScreenSpaceCamera;
            _canvas.worldCamera = _uiCamera;
        }

        void DetachFromBaseCamera()
        {
            if (_baseCamera != null && _uiCamera != null)
            {
                _baseCamera.GetUniversalAdditionalCameraData().cameraStack?.Remove(_uiCamera);
            }

            _baseCamera = null;
        }

        bool TryResolveUiLayer(ref int uiLayer)
        {
            if (uiLayer < 0)
            {
                uiLayer = _uiLayer >= 0 ? _uiLayer : ResolveDefaultUiLayer();
            }

            if (uiLayer >= 0 && uiLayer <= 31)
            {
                return true;
            }

            Debug.LogError($"[UIFrame] 配置 URP Camera Stack 失败：无效 UI Layer {uiLayer}。");
            return false;
        }

        static int ResolveDefaultUiLayer()
        {
            var layer = LayerMask.NameToLayer("UI");
            return layer >= 0 ? layer : DefaultUiLayerFallback;
        }

        static void SetLayerRecursively(Transform root, int layer)
        {
            if (root == null)
            {
                return;
            }

            root.gameObject.layer = layer;
            for (var i = 0; i < root.childCount; i++)
            {
                SetLayerRecursively(root.GetChild(i), layer);
            }
        }

        void OnDestroy()
        {
            _safeAreaBurst = 0;
            ScreenOrientationManager.CanvasLayoutChanged -= ApplyOrientation;
            DetachFromBaseCamera();

            if (_whiteSprite != null)
            {
                Destroy(_whiteSprite);
                _whiteSprite = null;
            }
        }

        sealed class SafeAreaCanvasListener : MonoBehaviour
        {
            UIFrameRoot _host;

            public void Bind(UIFrameRoot host)
            {
                _host = host;
            }

            void OnRectTransformDimensionsChange()
            {
                if (_host != null)
                {
                    _host.RequestSafeAreaBurst();
                }
            }
        }
    }
}
