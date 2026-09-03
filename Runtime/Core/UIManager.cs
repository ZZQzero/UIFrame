using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YooAsset;

namespace UIFrame
{
    sealed class UIManager
    {
        readonly Dictionary<Type, UIPanel> _opened = new Dictionary<Type, UIPanel>();
        readonly Dictionary<Type, UIPanel> _cached = new Dictionary<Type, UIPanel>();
        readonly Dictionary<Type, UIOpenRequest> _loading = new Dictionary<Type, UIOpenRequest>();
        readonly Dictionary<Type, List<UIPanel>> _toasts = new Dictionary<Type, List<UIPanel>>();
        readonly Dictionary<Type, List<UIPanel>> _toastIdle = new Dictionary<Type, List<UIPanel>>();
        readonly Dictionary<UIPanel, CancellationTokenSource> _toastTimers =
            new Dictionary<UIPanel, CancellationTokenSource>();
        readonly HashSet<UILoadRequest> _toastLoading = new HashSet<UILoadRequest>();
        readonly TipsChannel _tips = new TipsChannel();
        readonly List<TipsWaitItem> _tipsDrain = new List<TipsWaitItem>(8);
        readonly List<UIPanel> _windowStack = new List<UIPanel>();
        readonly List<UIPanel> _popupStack = new List<UIPanel>();
        bool _toastPumping;
        int _toastSuppressPump;

        UIFrameRoot _root;
        UILoader _loader;
        bool _inited;

        public bool IsInited => _inited;
        public Camera UICamera => _root != null ? _root.UICamera : null;

        public void Init()
        {
            if (_inited)
            {
                return;
            }

            _loader = new UILoader();
            _root = UIFrameRoot.Create();
            _root.MaskClicked += OnMaskClicked;
            _inited = true;
            Debug.Log("[UIFrame] Init 完成");
        }

        public void Shutdown(bool destroyRoot = true)
        {
            if (!_inited)
            {
                return;
            }

            _inited = false;

            foreach (var req in _loading.Values)
            {
                req.Cancelled = true;
                req.Completion.TrySetResult(null);
            }

            _loading.Clear();
            foreach (var req in _toastLoading)
            {
                req.Cancelled = true;
            }

            _toastLoading.Clear();
            _tipsDrain.Clear();
            _tips.ResetRuntime(_tipsDrain);
            RejectToastWaits(_tipsDrain);
            CancelAllToastTimers();
            _windowStack.Clear();
            _popupStack.Clear();

            var closing = new List<UIPanel>(16);
            foreach (var panel in _opened.Values)
            {
                if (panel != null)
                {
                    closing.Add(panel);
                }
            }

            foreach (var list in _toasts.Values)
            {
                if (list == null)
                {
                    continue;
                }

                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i] != null)
                    {
                        closing.Add(list[i]);
                    }
                }
            }

            for (var i = 0; i < closing.Count; i++)
            {
                ClosePanel(closing[i], destroy: true);
            }

            _opened.Clear();
            _toasts.Clear();
            DestroyToastIdle();

            closing.Clear();
            foreach (var panel in _cached.Values)
            {
                if (panel != null)
                {
                    closing.Add(panel);
                }
            }

            _cached.Clear();
            for (var i = 0; i < closing.Count; i++)
            {
                DestroyPanel(closing[i]);
            }

            if (_root != null)
            {
                _root.MaskClicked -= OnMaskClicked;
                var root = _root;
                _root = null;
                if (destroyRoot && root != null)
                {
                    UnityEngine.Object.Destroy(root.gameObject);
                }
            }

            _loader = null;
            ScreenOrientationManager.Shutdown();
            ScreenSafeArea.Shutdown();
            Debug.Log("[UIFrame] Shutdown 完成");
        }

        public void SetPackage(ResourcePackage package)
        {
            if (_loader == null)
            {
                Debug.LogWarning("[UIFrame] 尚未 Init，无法绑定 ResourcePackage。");
                return;
            }

            if (package == null)
            {
                Debug.LogWarning("[UIFrame] SetPackage 收到空包。");
                return;
            }

            _loader.SetPackage(package);
            Debug.Log($"[UIFrame] 已绑定 ResourcePackage: {package.PackageName}");
        }

        public void SetPackage(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName))
            {
                Debug.LogWarning("[UIFrame] packageName 为空。");
                return;
            }

            try
            {
                SetPackage(YooAssets.GetPackage(packageName));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIFrame] 绑定 ResourcePackage 失败: {ex.Message}");
            }
        }

        public Camera ConfigureURPCameraStack(Camera baseCamera, Camera uiCamera, int uiLayer)
        {
            if (_root == null)
            {
                Debug.LogWarning("[UIFrame] UIFrameRoot 未就绪，无法配置 URP Camera Stack。");
                return null;
            }

            return _root.ConfigureURPCameraStack(baseCamera, uiCamera, uiLayer);
        }

        public void DisableURPCameraStack()
        {
            if (_root != null)
            {
                _root.DisableURPCameraStack();
            }
        }

        internal void TryBindPackage()
        {
            try
            {
                if (!YooAssets.IsInitialized)
                {
                    Debug.LogWarning("[UIFrame] YooAsset 未初始化。资源就绪后请调用 UI.SetPackage。");
                    return;
                }

                var packages = YooAssets.GetPackages();
                if (packages == null || packages.Count == 0)
                {
                    Debug.LogWarning("[UIFrame] 没有可用的 ResourcePackage。请调用 UI.SetPackage。");
                    return;
                }

                _loader.SetPackage(packages[0]);
                if (packages.Count > 1)
                {
                    Debug.LogWarning(
                        $"[UIFrame] 存在多个 ResourcePackage，已绑定第一个: {packages[0].PackageName}。可用 UI.SetPackage 指定。");
                }
                else
                {
                    Debug.Log($"[UIFrame] 已绑定 ResourcePackage: {packages[0].PackageName}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIFrame] 绑定 ResourcePackage 失败: {ex.Message}");
            }
        }

        public void ConfigureTips(int maxVisible, int maxQueued, float defaultDuration)
        {
            _tipsDrain.Clear();
            _tips.Configure(maxVisible, maxQueued, defaultDuration, _tipsDrain);
            RejectToastWaits(_tipsDrain);
            TrimToastIdle();
            if (_inited)
            {
                PumpToastQueue();
            }
        }

        public UniTask<TPanel> Open<TPanel, TArgs>(UIOpenMode mode, TArgs args)
            where TPanel : UIPanel<TArgs>
        {
            var type = typeof(TPanel);
            if (!_inited)
            {
                Debug.LogError("[UIFrame] 请先调用 UI.Init()");
                return UniTask.FromResult<TPanel>(null);
            }

            if (mode == UIOpenMode.Toast)
            {
                return OpenToast<TPanel>(type, args, duration: null);
            }

            if (_loading.TryGetValue(type, out var inflight))
            {
                // 同 Type 合并为一次加载：后一次 Args/Mode 生效，并撤销已取消。
                inflight.Cancelled = false;
                inflight.Args = args;
                inflight.Mode = mode;
                return AwaitInflight<TPanel>(inflight);
            }

            if (!UIPanelCatalog.TryResolve(type, mode, out var bind))
            {
                return UniTask.FromResult<TPanel>(null);
            }

            if (TryReusePanel(type, bind, mode, args, out var reused))
            {
                return UniTask.FromResult(reused as TPanel);
            }

            return OpenCore<TPanel, TArgs>(type, bind, mode, args);
        }

        async UniTask<TPanel> AwaitInflight<TPanel>(UIOpenRequest inflight)
            where TPanel : UIPanel
        {
            var merged = await inflight.Completion.Task;
            return merged as TPanel;
        }

        bool TryReusePanel(
            Type type,
            UIPanelBind bind,
            UIOpenMode mode,
            object args,
            out UIPanel panel)
        {
            if (_opened.TryGetValue(type, out panel))
            {
                if (panel != null)
                {
                    ShowReusedPanelSafely(type, bind, mode, args, ref panel);
                    return true;
                }

                _opened.Remove(type);
            }

            TryRemoveDeadCache(type);
            if (!_cached.TryGetValue(type, out panel) || panel == null)
            {
                panel = null;
                return false;
            }

            _cached.Remove(type);
            if (!string.Equals(panel.Location, bind.Location, StringComparison.Ordinal))
            {
                DestroyPanel(panel);
                panel = null;
                return false;
            }

            ShowReusedPanelSafely(type, bind, mode, args, ref panel);
            return true;
        }

        void ShowReusedPanelSafely(
            Type type,
            UIPanelBind bind,
            UIOpenMode mode,
            object args,
            ref UIPanel panel)
        {
            try
            {
                ApplyBind(panel, bind);
                ApplyAndShow(panel, mode, args);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UIFrame] 打开失败 {type.Name}: {ex}");
                if (panel != null)
                {
                    ClosePanel(panel, destroy: true);
                }

                panel = null;
            }
        }

        async UniTask<TPanel> OpenCore<TPanel, TArgs>(
            Type type,
            UIPanelBind initialBind,
            UIOpenMode mode,
            TArgs args)
            where TPanel : UIPanel<TArgs>
        {
            var req = new UIOpenRequest
            {
                Mode = mode,
                Args = args,
            };
            _loading[type] = req;

            UIPanel panel = null;
            try
            {
                panel = await LoadPanel(type, initialBind, req);
                if (req.Cancelled || panel == null)
                {
                    if (panel != null)
                    {
                        DestroyPanel(panel);
                    }

                    req.Completion.TrySetResult(null);
                    return null;
                }

                if (!UIPanelCatalog.TryResolve(type, req.Mode, out var finalBind))
                {
                    DestroyPanel(panel);
                    req.Completion.TrySetResult(null);
                    return null;
                }

                if (!string.Equals(panel.Location, finalBind.Location, StringComparison.Ordinal))
                {
                    Debug.LogError(
                        $"[UIFrame] {type.Name} 加载期间注册地址发生变化: {panel.Location} -> {finalBind.Location}，请重新打开。");
                    DestroyPanel(panel);
                    req.Completion.TrySetResult(null);
                    return null;
                }

                ApplyBind(panel, finalBind);
                ApplyAndShow(panel, req.Mode, req.Args);
                req.Completion.TrySetResult(panel);
                return panel as TPanel;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UIFrame] 打开失败 {type.Name}: {ex}");
                if (panel != null)
                {
                    ClosePanel(panel, destroy: true);
                }

                req.Completion.TrySetResult(null);
                return null;
            }
            finally
            {
                _loading.Remove(type);
            }
        }

        internal UniTask<TPanel> Toast<TPanel>(object args, float? duration)
            where TPanel : UIPanel
        {
            return OpenToast<TPanel>(typeof(TPanel), args, duration);
        }

        async UniTask<TPanel> OpenToast<TPanel>(Type type, object args, float? duration)
            where TPanel : UIPanel
        {
            var resolved = _tips.ResolveDuration(duration);
            if (!_tips.HasFreeSlot(CountVisibleToasts()))
            {
                if (_tips.Settings.MaxVisible <= 0 || _tips.Settings.MaxQueued <= 0)
                {
                    return null;
                }

                var wait = new TipsWaitItem
                {
                    PanelType = type,
                    Args = args,
                    Duration = resolved,
                };
                if (!_tips.TryEnqueue(wait, out var dropped))
                {
                    return null;
                }

                RejectToastWait(dropped);
                var queued = await wait.Completion.Task;
                return queued as TPanel;
            }

            var panel = await ShowToast(type, args, resolved);
            return panel as TPanel;
        }

        async UniTask<UIPanel> ShowToast(Type type, object args, float duration)
        {
            UILoadRequest req = null;
            UIPanel panel = null;
            var slotHeld = false;
            try
            {
                if (!UIPanelCatalog.TryResolve(type, UIOpenMode.Toast, out var bind))
                {
                    return null;
                }

                _tips.BeginInFlight();
                slotHeld = true;
                panel = TakeToastIdle(type, bind);
                if (panel == null)
                {
                    req = new UILoadRequest { PanelType = type };
                    _toastLoading.Add(req);
                    panel = await LoadPanel(type, bind, req);
                    if (req.Cancelled || panel == null)
                    {
                        if (panel != null)
                        {
                            DestroyPanel(panel);
                        }

                        return null;
                    }
                }

                if (!_inited)
                {
                    DestroyPanel(panel);
                    return null;
                }

                PresentToast(panel, args, duration);
                return panel;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UIFrame] Toast 打开失败 {type.Name}: {ex}");
                ReleaseToastAfterFailure(panel);
                return null;
            }
            finally
            {
                if (req != null)
                {
                    _toastLoading.Remove(req);
                }

                if (slotHeld)
                {
                    _tips.EndInFlight();
                }

                PumpToastQueue();
            }
        }

        void PresentToast(UIPanel panel, object args, float duration)
        {
            panel.OpenMode = UIOpenMode.Toast;
            panel.ApplyArgs(args);
            AttachToLayer(panel, _root.GetLayer(panel.Layer));
            panel.transform.SetAsLastSibling();
            panel.gameObject.SetActive(true);
            AddVisibleToast(panel);
            panel.DispatchOpen();
            if (!IsVisibleToast(panel) || TipsChannel.IsSticky(duration))
            {
                return;
            }

            StartToastTimer(panel, duration);
        }

        async UniTask<UIPanel> LoadPanel(
            Type type,
            UIPanelBind bind,
            UILoadRequest req = null)
        {
            var parent = _root.GetLayer(bind.Layer);
            var panel = await _loader.Load(type, bind.Location, parent, req);
            if (panel == null)
            {
                return null;
            }

            ApplyBind(panel, bind);
            try
            {
                panel.DispatchCreate();
            }
            catch (Exception)
            {
                DestroyPanel(panel);
                throw;
            }

            return panel;
        }

        static void ApplyBind(UIPanel panel, UIPanelBind bind)
        {
            panel.Layer = bind.Layer;
            panel.Group = bind.Group;
            panel.Location = bind.Location;
            panel.CacheOnClose = bind.Cache;
        }

        void ApplyAndShow(UIPanel panel, UIOpenMode mode, object args)
        {
            var wasWindowTop = WindowTop == panel;
            var wasWindow = _windowStack.Remove(panel);
            _popupStack.Remove(panel);
            _cached.Remove(panel.PanelType);

            if (mode == UIOpenMode.Push)
            {
                CloseAllPopups(destroy: false);
            }

            panel.OpenMode = mode;
            panel.ApplyArgs(args);

            if (mode == UIOpenMode.Push)
            {
                var prev = WindowTop;
                if (prev != null && prev != panel && prev.gameObject.activeSelf)
                {
                    PausePanel(prev);
                }

                MoveToStackEnd(_windowStack, panel);
            }
            else
            {
                if (wasWindow && wasWindowTop)
                {
                    var next = WindowTop;
                    if (next != null && !next.gameObject.activeSelf)
                    {
                        ResumePanel(next);
                    }
                }

                if (mode == UIOpenMode.Popup)
                {
                    MoveToStackEnd(_popupStack, panel);
                }
            }

            _opened[panel.PanelType] = panel;
            AttachToLayer(panel, _root.GetLayer(panel.Layer));
            panel.transform.SetAsLastSibling();
            panel.gameObject.SetActive(true);
            panel.DispatchOpen();
            RefreshMask();
        }

        public void Back()
        {
            if (_popupStack.Count > 0)
            {
                ClosePanel(PopupTop, destroy: false);
                return;
            }

            if (_windowStack.Count > 0)
            {
                ClosePanel(WindowTop, destroy: false);
            }
        }

        public void Close(Type panelType, bool destroy)
        {
            if (panelType == null)
            {
                return;
            }

            _toastSuppressPump++;
            try
            {
                CloseCore(panelType, destroy);
            }
            finally
            {
                _toastSuppressPump--;
                PumpToastQueue();
            }
        }

        void CloseCore(Type panelType, bool destroy)
        {

            if (_loading.TryGetValue(panelType, out var req))
            {
                req.Cancelled = true;
            }

            CancelToastLoads(panelType);
            _tipsDrain.Clear();
            _tips.DrainWhere(item => item != null && item.PanelType == panelType, _tipsDrain);
            RejectToastWaits(_tipsDrain);

            if (_toasts.TryGetValue(panelType, out var toasts) && toasts != null && toasts.Count > 0)
            {
                var closing = new List<UIPanel>(toasts.Count);
                for (var i = 0; i < toasts.Count; i++)
                {
                    closing.Add(toasts[i]);
                }

                for (var i = 0; i < closing.Count; i++)
                {
                    ClosePanel(closing[i], destroy);
                }
            }

            if (destroy)
            {
                DestroyToastIdle(panelType);
            }

            if (_opened.TryGetValue(panelType, out var opened) && opened != null)
            {
                ClosePanel(opened, destroy);
                return;
            }

            TryRemoveDeadCache(panelType);
            if (destroy && _cached.TryGetValue(panelType, out var cached) && cached != null)
            {
                _cached.Remove(panelType);
                DestroyPanel(cached);
            }
        }

        public void CloseInstance(UIPanel panel, bool destroy)
        {
            ClosePanel(panel, destroy);
        }

        public void CloseGroup(UIGroup group, bool destroy)
        {
            _toastSuppressPump++;
            try
            {
                CloseGroupCore(group, destroy);
            }
            finally
            {
                _toastSuppressPump--;
                PumpToastQueue();
            }
        }

        void CloseGroupCore(UIGroup group, bool destroy)
        {
            foreach (var kv in _loading)
            {
                if (!UIPanelCatalog.TryResolve(kv.Key, kv.Value.Mode, out var bind))
                {
                    continue;
                }

                if (bind.Group == group)
                {
                    kv.Value.Cancelled = true;
                }
            }

            foreach (var req in _toastLoading)
            {
                if (req.PanelType != null
                    && UIPanelCatalog.TryResolve(req.PanelType, UIOpenMode.Toast, out var toastBind)
                    && toastBind.Group == group)
                {
                    req.Cancelled = true;
                }
            }

            _tipsDrain.Clear();
            _tips.DrainWhere(
                item => item != null
                        && UIPanelCatalog.TryResolve(item.PanelType, UIOpenMode.Toast, out var waitBind)
                        && waitBind.Group == group,
                _tipsDrain);
            RejectToastWaits(_tipsDrain);

            var buffer = new List<UIPanel>(16);
            CollectGroup(_opened.Values, group, buffer);
            for (var i = 0; i < buffer.Count; i++)
            {
                ClosePanel(buffer[i], destroy);
            }

            buffer.Clear();
            CollectGroup(_cached.Values, group, buffer);
            if (destroy)
            {
                for (var i = 0; i < buffer.Count; i++)
                {
                    var cached = buffer[i];
                    if (cached == null)
                    {
                        continue;
                    }

                    _cached.Remove(cached.PanelType);
                    DestroyPanel(cached);
                }
            }

            buffer.Clear();
            foreach (var kv in _toasts)
            {
                CollectGroup(kv.Value, group, buffer);
            }

            for (var i = 0; i < buffer.Count; i++)
            {
                ClosePanel(buffer[i], destroy);
            }

            if (destroy)
            {
                buffer.Clear();
                foreach (var kv in _toastIdle)
                {
                    CollectGroup(kv.Value, group, buffer);
                }

                for (var i = 0; i < buffer.Count; i++)
                {
                    var idle = buffer[i];
                    RemoveToastIdle(idle);
                    DestroyPanel(idle);
                }
            }
        }

        public void ClearCache()
        {
            var buffer = new List<UIPanel>(16);
            foreach (var kv in _cached)
            {
                if (kv.Value != null)
                {
                    buffer.Add(kv.Value);
                }
            }

            _cached.Clear();
            for (var i = 0; i < buffer.Count; i++)
            {
                DestroyPanel(buffer[i]);
            }

            DestroyToastIdle();
        }

        public TPanel Get<TPanel>() where TPanel : UIPanel
        {
            var type = typeof(TPanel);
            if (_opened.TryGetValue(type, out var panel))
            {
                if (panel != null)
                {
                    return panel as TPanel;
                }

                _opened.Remove(type);
            }

            if (_toasts.TryGetValue(type, out var toasts) && toasts != null)
            {
                for (var i = toasts.Count - 1; i >= 0; i--)
                {
                    if (toasts[i] != null)
                    {
                        return toasts[i] as TPanel;
                    }

                    toasts.RemoveAt(i);
                }

                if (toasts.Count == 0)
                {
                    _toasts.Remove(type);
                }
            }

            return null;
        }

        public bool IsOpen<TPanel>() where TPanel : UIPanel
        {
            return Get<TPanel>() != null;
        }

        void ClosePanel(UIPanel panel, bool destroy)
        {
            if (panel == null)
            {
                return;
            }

            var type = panel.PanelType;
            var wasWindowTop = WindowTop == panel;
            _windowStack.Remove(panel);
            _popupStack.Remove(panel);

            if (panel.OpenMode == UIOpenMode.Toast)
            {
                CancelToastTimer(panel);
                var wasVisible = RemoveVisibleToast(panel);
                if (!wasVisible)
                {
                    if (destroy)
                    {
                        RemoveToastIdle(panel);
                        DestroyPanel(panel);
                    }

                    return;
                }

                try
                {
                    panel.DispatchClose();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[UIFrame] OnClose 异常 {type.Name}: {ex}");
                }

                if (destroy || !panel.CacheOnClose)
                {
                    DestroyPanel(panel);
                }
                else
                {
                    ReturnToastIdle(panel);
                }

                PumpToastQueue();
                return;
            }

            if (_opened.TryGetValue(type, out var opened) && opened == panel)
            {
                _opened.Remove(type);
            }

            _cached.Remove(type);

            try
            {
                panel.DispatchClose();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UIFrame] OnClose 异常 {type.Name}: {ex}");
            }

            if (destroy || !panel.CacheOnClose)
            {
                DestroyPanel(panel);
            }
            else
            {
                panel.gameObject.SetActive(false);
                _cached[type] = panel;
            }

            if (wasWindowTop)
            {
                var top = WindowTop;
                if (top != null)
                {
                    ResumePanel(top);
                }
            }

            RefreshMask();
        }

        void PumpToastQueue()
        {
            if (!_inited || _toastPumping || _toastSuppressPump > 0)
            {
                return;
            }

            _toastPumping = true;
            try
            {
                while (_tips.HasFreeSlot(CountVisibleToasts()) && _tips.TryDequeue(out var item))
                {
                    DispatchQueuedToast(item).Forget();
                }
            }
            finally
            {
                _toastPumping = false;
            }
        }

        async UniTaskVoid DispatchQueuedToast(TipsWaitItem item)
        {
            UIPanel panel = null;
            try
            {
                panel = await ShowToast(item.PanelType, item.Args, item.Duration);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UIFrame] 队列 Toast 打开失败: {ex}");
            }
            finally
            {
                item.Completion.TrySetResult(panel);
                PumpToastQueue();
            }
        }

        int CountVisibleToasts()
        {
            var count = 0;
            foreach (var list in _toasts.Values)
            {
                if (list == null)
                {
                    continue;
                }

                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i] != null)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        void AddVisibleToast(UIPanel panel)
        {
            var type = panel.PanelType;
            if (!_toasts.TryGetValue(type, out var list))
            {
                list = new List<UIPanel>();
                _toasts[type] = list;
            }

            list.Add(panel);
        }

        bool RemoveVisibleToast(UIPanel panel)
        {
            var type = panel.PanelType;
            if (!_toasts.TryGetValue(type, out var list))
            {
                return false;
            }

            var removed = list.Remove(panel);
            if (list.Count == 0)
            {
                _toasts.Remove(type);
            }

            return removed;
        }

        bool IsVisibleToast(UIPanel panel)
        {
            return panel != null
                   && _toasts.TryGetValue(panel.PanelType, out var list)
                   && list != null
                   && list.Contains(panel);
        }

        UIPanel TakeToastIdle(Type type, UIPanelBind bind)
        {
            if (!_toastIdle.TryGetValue(type, out var list) || list.Count == 0)
            {
                return null;
            }

            for (var i = list.Count - 1; i >= 0; i--)
            {
                var panel = list[i];
                list.RemoveAt(i);
                if (panel == null)
                {
                    continue;
                }

                if (!string.Equals(panel.Location, bind.Location, StringComparison.Ordinal))
                {
                    DestroyPanel(panel);
                    continue;
                }

                ApplyBind(panel, bind);
                if (list.Count == 0)
                {
                    _toastIdle.Remove(type);
                }

                return panel;
            }

            if (list.Count == 0)
            {
                _toastIdle.Remove(type);
            }

            return null;
        }

        bool IsToastIdle(UIPanel panel)
        {
            return panel != null
                   && _toastIdle.TryGetValue(panel.PanelType, out var list)
                   && list != null
                   && list.Contains(panel);
        }

        void ReleaseToastAfterFailure(UIPanel panel)
        {
            if (panel == null)
            {
                return;
            }

            if (IsVisibleToast(panel))
            {
                ClosePanel(panel, destroy: true);
                return;
            }

            if (!IsToastIdle(panel))
            {
                DestroyPanel(panel);
            }
        }

        void ReturnToastIdle(UIPanel panel)
        {
            if (panel == null)
            {
                return;
            }

            var cap = _tips.Settings.MaxVisible;
            if (cap <= 0)
            {
                DestroyPanel(panel);
                return;
            }

            var type = panel.PanelType;
            if (!_toastIdle.TryGetValue(type, out var list))
            {
                list = new List<UIPanel>();
                _toastIdle[type] = list;
            }

            while (list.Count >= cap)
            {
                var oldest = list[0];
                list.RemoveAt(0);
                DestroyPanel(oldest);
            }

            panel.gameObject.SetActive(false);
            list.Add(panel);
        }

        void RemoveToastIdle(UIPanel panel)
        {
            if (panel == null)
            {
                return;
            }

            if (!_toastIdle.TryGetValue(panel.PanelType, out var list))
            {
                return;
            }

            list.Remove(panel);
            if (list.Count == 0)
            {
                _toastIdle.Remove(panel.PanelType);
            }
        }

        void DestroyToastIdle(Type type = null)
        {
            if (type != null)
            {
                if (!_toastIdle.TryGetValue(type, out var list))
                {
                    return;
                }

                DestroyToastIdleList(list);
                _toastIdle.Remove(type);
                return;
            }

            foreach (var list in _toastIdle.Values)
            {
                DestroyToastIdleList(list);
            }

            _toastIdle.Clear();
        }

        void DestroyToastIdleList(List<UIPanel> list)
        {
            if (list == null)
            {
                return;
            }

            for (var i = 0; i < list.Count; i++)
            {
                DestroyPanel(list[i]);
            }

            list.Clear();
        }

        void TrimToastIdle()
        {
            var cap = _tips.Settings.MaxVisible;
            var extra = new List<UIPanel>(4);
            foreach (var kv in _toastIdle)
            {
                var list = kv.Value;
                if (list == null)
                {
                    continue;
                }

                while (list.Count > cap)
                {
                    var last = list[list.Count - 1];
                    list.RemoveAt(list.Count - 1);
                    extra.Add(last);
                }
            }

            for (var i = 0; i < extra.Count; i++)
            {
                DestroyPanel(extra[i]);
            }
        }

        void StartToastTimer(UIPanel panel, float duration)
        {
            CancelToastTimer(panel);
            var cts = new CancellationTokenSource();
            _toastTimers[panel] = cts;
            RunToastTimer(panel, duration, cts.Token).Forget();
        }

        async UniTaskVoid RunToastTimer(UIPanel panel, float duration, CancellationToken token)
        {
            var cancelled = await UniTask.Delay(
                TimeSpan.FromSeconds(duration),
                DelayType.UnscaledDeltaTime,
                PlayerLoopTiming.Update,
                token).SuppressCancellationThrow();
            if (cancelled || !_inited)
            {
                return;
            }

            ClosePanel(panel, destroy: false);
        }

        void CancelToastTimer(UIPanel panel)
        {
            if (panel == null || !_toastTimers.TryGetValue(panel, out var cts))
            {
                return;
            }

            _toastTimers.Remove(panel);
            cts.Cancel();
            cts.Dispose();
        }

        void CancelAllToastTimers()
        {
            foreach (var cts in _toastTimers.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _toastTimers.Clear();
        }

        void CancelToastLoads(Type panelType)
        {
            if (panelType == null)
            {
                return;
            }

            foreach (var req in _toastLoading)
            {
                if (req.PanelType == panelType)
                {
                    req.Cancelled = true;
                }
            }
        }

        static void RejectToastWait(TipsWaitItem item)
        {
            item?.Completion.TrySetResult(null);
        }

        static void RejectToastWaits(List<TipsWaitItem> items)
        {
            if (items == null)
            {
                return;
            }

            for (var i = 0; i < items.Count; i++)
            {
                RejectToastWait(items[i]);
            }

            items.Clear();
        }

        void CloseAllPopups(bool destroy)
        {
            while (_popupStack.Count > 0)
            {
                ClosePanel(_popupStack[_popupStack.Count - 1], destroy);
            }
        }

        void DestroyPanel(UIPanel panel)
        {
            if (panel == null)
            {
                return;
            }

            try
            {
                panel.DispatchDestroy();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UIFrame] OnDestroyPanel 异常 {panel.PanelType.Name}: {ex}");
            }

            var handle = panel.AssetHandle;
            panel.AssetHandle = null;
            UnityEngine.Object.Destroy(panel.gameObject);
            UILoader.Release(handle);
        }

        void PausePanel(UIPanel panel)
        {
            if (panel == null)
            {
                return;
            }

            try
            {
                panel.DispatchPause();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UIFrame] OnPause 异常 {panel.PanelType.Name}: {ex}");
            }

            panel.gameObject.SetActive(false);
        }

        void ResumePanel(UIPanel panel)
        {
            if (panel == null)
            {
                return;
            }

            panel.gameObject.SetActive(true);
            panel.transform.SetAsLastSibling();
            try
            {
                panel.DispatchResume();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UIFrame] OnResume 异常 {panel.PanelType.Name}: {ex}");
            }
        }

        static void MoveToStackEnd(List<UIPanel> stack, UIPanel panel)
        {
            stack.Remove(panel);
            stack.Add(panel);
        }

        static void AttachToLayer(UIPanel panel, RectTransform parent)
        {
            if (panel == null || parent == null)
            {
                return;
            }

            var t = panel.transform;
            if (t.parent != parent)
            {
                t.SetParent(parent, false);
            }

            SetLayerRecursively(t, parent.gameObject.layer);
        }

        static void SetLayerRecursively(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            for (var i = 0; i < root.childCount; i++)
            {
                SetLayerRecursively(root.GetChild(i), layer);
            }
        }

        void TryRemoveDeadCache(Type type)
        {
            if (!_cached.TryGetValue(type, out var cached))
            {
                return;
            }

            if (cached != null)
            {
                return;
            }

            _cached.Remove(type);
        }

        static void CollectGroup(IEnumerable<UIPanel> source, UIGroup group, List<UIPanel> dest)
        {
            if (source == null)
            {
                return;
            }

            foreach (var panel in source)
            {
                if (panel != null && panel.Group == group)
                {
                    dest.Add(panel);
                }
            }
        }

        void OnMaskClicked()
        {
            var top = PopupTop;
            if (top != null && top.CloseOnMaskClick)
            {
                Back();
            }
        }

        void RefreshMask()
        {
            if (_root == null)
            {
                return;
            }

            _root.SetMaskVisible(_popupStack.Count > 0);
        }

        UIPanel WindowTop => _windowStack.Count > 0 ? _windowStack[_windowStack.Count - 1] : null;

        UIPanel PopupTop => _popupStack.Count > 0 ? _popupStack[_popupStack.Count - 1] : null;
    }
}
