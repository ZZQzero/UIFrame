using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace UIFrame
{
    /// <summary>UI 根节点。默认使用 Overlay Canvas，也可切换到 URP Camera Stack。</summary>
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
        bool _ownsUICamera;
        int _baseCameraCullingMask;
        int _uiCameraCullingMask;
        CameraClearFlags _uiCameraClearFlags;
        float _uiCameraDepth;
        bool _uiCameraAllowHdr;
        bool _uiCameraAllowMsaa;
        bool _uiCameraOrthographic;
        float _uiCameraNearClipPlane;
        float _uiCameraFarClipPlane;
        CameraRenderType _baseCameraRenderType;
        CameraRenderType _uiCameraRenderType;
        bool _uiCameraRenderPostProcessing;
        bool _uiCameraRenderShadows;

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
                uiLayer = LayerMask.NameToLayer("UI");
            }

            if (uiLayer < 0 || uiLayer > 31)
            {
                Debug.LogError($"[UIFrame] 配置 URP Camera Stack 失败：无效 UI Layer {uiLayer}。");
                return null;
            }

            ReleaseURPCameraStack(destroyOwnedCamera: true);

            _baseCamera = baseCamera;
            _uiCamera = uiCamera;
            _ownsUICamera = _uiCamera == null;
            if (_ownsUICamera)
            {
                var cameraGo = new GameObject("UIFrameCamera");
                cameraGo.transform.SetParent(transform, false);
                _uiCamera = cameraGo.AddComponent<Camera>();
            }

            var layerMask = 1 << uiLayer;
            _baseCameraCullingMask = _baseCamera.cullingMask;
            _uiCameraCullingMask = _uiCamera.cullingMask;
            _uiCameraClearFlags = _uiCamera.clearFlags;
            _uiCameraDepth = _uiCamera.depth;
            _uiCameraAllowHdr = _uiCamera.allowHDR;
            _uiCameraAllowMsaa = _uiCamera.allowMSAA;
            _uiCameraOrthographic = _uiCamera.orthographic;
            _uiCameraNearClipPlane = _uiCamera.nearClipPlane;
            _uiCameraFarClipPlane = _uiCamera.farClipPlane;

            var baseCameraData = _baseCamera.GetUniversalAdditionalCameraData();
            var uiCameraData = _uiCamera.GetUniversalAdditionalCameraData();
            _baseCameraRenderType = baseCameraData.renderType;
            _uiCameraRenderType = uiCameraData.renderType;
            _uiCameraRenderPostProcessing = uiCameraData.renderPostProcessing;
            _uiCameraRenderShadows = uiCameraData.renderShadows;

            baseCameraData.renderType = CameraRenderType.Base;
            uiCameraData.renderType = CameraRenderType.Overlay;
            uiCameraData.renderPostProcessing = false;
            uiCameraData.renderShadows = false;

            _baseCamera.cullingMask &= ~layerMask;
            _uiCamera.cullingMask = layerMask;
            _uiCamera.clearFlags = CameraClearFlags.Depth;
            _uiCamera.depth = _baseCamera.depth + 1f;
            _uiCamera.allowHDR = false;
            _uiCamera.allowMSAA = false;
            _uiCamera.orthographic = true;
            _uiCamera.nearClipPlane = 0.01f;
            _uiCamera.farClipPlane = 10f;

            var cameraStack = baseCameraData.cameraStack;
            if (cameraStack == null)
            {
                Debug.LogError("[UIFrame] 配置 URP Camera Stack 失败：Base Camera 的 Renderer 不支持 Camera Stack。");
                ReleaseURPCameraStack(destroyOwnedCamera: true);
                return null;
            }

            if (!cameraStack.Contains(_uiCamera))
            {
                cameraStack.Add(_uiCamera);
            }

            SetLayerRecursively(_canvasRoot, uiLayer);
            _canvas.renderMode = RenderMode.ScreenSpaceCamera;
            _canvas.worldCamera = _uiCamera;
            _canvas.planeDistance = 1f;
            return _uiCamera;
        }

        public void DisableURPCameraStack()
        {
            ReleaseURPCameraStack(destroyOwnedCamera: true);
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

            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;
            _canvas.additionalShaderChannels =
                AdditionalCanvasShaderChannels.TexCoord1
                | AdditionalCanvasShaderChannels.Normal
                | AdditionalCanvasShaderChannels.Tangent;

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

        void ReleaseURPCameraStack(bool destroyOwnedCamera)
        {
            if (_baseCamera != null)
            {
                var baseCameraData = _baseCamera.GetUniversalAdditionalCameraData();
                var cameraStack = baseCameraData.cameraStack;
                if (cameraStack != null && _uiCamera != null)
                {
                    cameraStack.Remove(_uiCamera);
                }

                baseCameraData.renderType = _baseCameraRenderType;
                _baseCamera.cullingMask = _baseCameraCullingMask;
            }

            if (_uiCamera != null)
            {
                var uiCameraData = _uiCamera.GetUniversalAdditionalCameraData();
                uiCameraData.renderType = _uiCameraRenderType;
                uiCameraData.renderPostProcessing = _uiCameraRenderPostProcessing;
                uiCameraData.renderShadows = _uiCameraRenderShadows;
                _uiCamera.cullingMask = _uiCameraCullingMask;
                _uiCamera.clearFlags = _uiCameraClearFlags;
                _uiCamera.depth = _uiCameraDepth;
                _uiCamera.allowHDR = _uiCameraAllowHdr;
                _uiCamera.allowMSAA = _uiCameraAllowMsaa;
                _uiCamera.orthographic = _uiCameraOrthographic;
                _uiCamera.nearClipPlane = _uiCameraNearClipPlane;
                _uiCamera.farClipPlane = _uiCameraFarClipPlane;
            }

            if (_canvas != null)
            {
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvas.worldCamera = null;
            }

            if (destroyOwnedCamera && _ownsUICamera && _uiCamera != null)
            {
                Destroy(_uiCamera.gameObject);
            }

            _baseCamera = null;
            _uiCamera = null;
            _ownsUICamera = false;
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
            ReleaseURPCameraStack(destroyOwnedCamera: false);

            if (_whiteSprite != null)
            {
                Destroy(_whiteSprite);
                _whiteSprite = null;
            }
        }
    }
}
