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
        Canvas _canvas;
        RectTransform _canvasRoot;
        CanvasScaler _rootScaler;
        Camera _baseCamera;
        Camera _uiCamera;
        int _uiLayer;

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
            if (_maskImage == null)
            {
                return;
            }

            _maskImage.gameObject.SetActive(visible);
        }

        /// <summary>
        /// 将 UI Camera 加入 Base Camera Stack。
        /// </summary>
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

            if (uiLayer < 0)
            {
                uiLayer = _uiLayer >= 0 ? _uiLayer : LayerMask.NameToLayer("UI");
            }

            if (uiLayer < 0 || uiLayer > 31)
            {
                Debug.LogError($"[UIFrame] 配置 URP Camera Stack 失败：无效 UI Layer {uiLayer}。");
                return null;
            }

            DetachFromBaseCamera();

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

            var cameraStack = baseCamera.GetUniversalAdditionalCameraData().cameraStack;
            if (cameraStack == null)
            {
                Debug.LogError("[UIFrame] 配置 URP Camera Stack 失败：Base Camera 的 Renderer 不支持 Camera Stack。");
                return null;
            }

            var uiCameraData = _uiCamera.GetUniversalAdditionalCameraData();
            uiCameraData.renderType = CameraRenderType.Overlay;

            if (!cameraStack.Contains(_uiCamera))
            {
                cameraStack.Add(_uiCamera);
            }

            _baseCamera = baseCamera;
            BindCanvasToUICamera();
            return _uiCamera;
        }

        /// <summary>仅从 Base Camera Stack 移除 UI Camera。Canvas 保持 Screen Space Camera 与 UI Camera 引用。</summary>
        public void DisableURPCameraStack()
        {
            DetachFromBaseCamera();
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
            _uiLayer = LayerMask.NameToLayer("UI");
            if (_uiLayer < 0)
            {
                _uiLayer = 5;
            }

            _uiCamera = CreateUICamera(_uiLayer);

            var canvasGo = new GameObject("CanvasRoot", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            canvasGo.layer = _uiLayer;

            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.vertexColorAlwaysGammaSpace = true;
            _canvas.sortingOrder = 100;
            _canvas.additionalShaderChannels =
                AdditionalCanvasShaderChannels.TexCoord1
                | AdditionalCanvasShaderChannels.Normal
                | AdditionalCanvasShaderChannels.Tangent;
            _canvas.planeDistance = 1f;
            BindCanvasToUICamera();

            _rootScaler = canvasGo.AddComponent<CanvasScaler>();
            _rootScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _rootScaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            _rootScaler.matchWidthOrHeight = 0.5f;
            _rootScaler.referenceResolution = new Vector2(1080f, 1920f);
            canvasGo.AddComponent<GraphicRaycaster>();

            _canvasRoot = (RectTransform)canvasGo.transform;
            UIRect.Stretch(_canvasRoot);

            for (var i = 0; i < LayerOrder.Length; i++)
            {
                var layer = LayerOrder[i];
                _layers[layer] = CreateLayer(canvasGo.transform, layer);
            }

            BuildMask();
            BindOrientation();
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

        void BindCanvasToUICamera()
        {
            _canvas.renderMode = RenderMode.ScreenSpaceCamera;
            _canvas.worldCamera = _uiCamera;
        }

        Camera CreateUICamera(int uiLayer)
        {
            var cameraGo = new GameObject("UIFrameCamera");
            cameraGo.transform.SetParent(transform, false);
            cameraGo.layer = uiLayer;
            var uiCamera = cameraGo.AddComponent<Camera>();
            uiCamera.clearFlags = CameraClearFlags.Depth;
            uiCamera.orthographic = true;
            uiCamera.cullingMask = 1 << uiLayer;

            var cameraData = uiCamera.GetUniversalAdditionalCameraData();
            cameraData.renderType = CameraRenderType.Overlay;
            cameraData.renderPostProcessing = false;
            cameraData.renderShadows = false;
            return uiCamera;
        }

        void DetachFromBaseCamera()
        {
            if (_baseCamera == null || _uiCamera == null)
            {
                _baseCamera = null;
                return;
            }

            var cameraStack = _baseCamera.GetUniversalAdditionalCameraData().cameraStack;
            cameraStack?.Remove(_uiCamera);
            _baseCamera = null;
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
            ScreenOrientationManager.CanvasLayoutChanged -= OnOrientationChanged;
            DetachFromBaseCamera();

            if (_whiteSprite != null)
            {
                Destroy(_whiteSprite);
                _whiteSprite = null;
            }
        }
    }
}
