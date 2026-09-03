using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YooAsset;

namespace UIFrame
{
    /// <summary>UIFrame 静态门面。业务只通过这里打开/关闭面板。</summary>
    public static class UI
    {
        static UIManager _manager;
        static bool _shuttingDown;
        static TipsSettings _tipsSettings = TipsSettings.Default;

        public static bool IsInited => _manager != null && _manager.IsInited;
        public static Camera UICamera => IsInited ? _manager.UICamera : null;

        /// <summary>创建 Root。若 YooAsset 已初始化且只有一个包，会自动绑定。</summary>
        public static void Init()
        {
            if (!EnsureInit())
            {
                return;
            }

            _manager.TryBindPackage();
        }

        /// <summary>创建 Root 并绑定指定 YooAsset 包。</summary>
        public static void Init(ResourcePackage package)
        {
            if (!EnsureInit())
            {
                return;
            }

            _manager.SetPackage(package);
        }

        /// <summary>创建 Root 并按包名绑定 YooAsset 包。</summary>
        public static void Init(string packageName)
        {
            if (!EnsureInit())
            {
                return;
            }

            _manager.SetPackage(packageName);
        }

        /// <summary>资源系统就绪后绑定 YooAsset 包。可在 <see cref="Init()"/> 之后再调用。</summary>
        public static void SetPackage(ResourcePackage package)
        {
            if (!EnsureInit())
            {
                return;
            }

            _manager.SetPackage(package);
        }

        /// <summary>按包名绑定 YooAsset 包。</summary>
        public static void SetPackage(string packageName)
        {
            if (!EnsureInit())
            {
                return;
            }

            _manager.SetPackage(packageName);
        }

        /// <summary>将 UI Camera 加入 Base Camera Stack。默认 Base=Camera.main，复用已有 UI Camera。</summary>
        public static Camera ConfigureURPCameraStack(
            Camera baseCamera = null,
            Camera uiCamera = null,
            int uiLayer = -1)
        {
            if (!EnsureInit())
            {
                return null;
            }

            return _manager.ConfigureURPCameraStack(baseCamera, uiCamera, uiLayer);
        }

        /// <summary>仅从 Base Camera Stack 移除 UI Camera。Canvas 模式与引用不变。</summary>
        public static void DisableURPCameraStack()
        {
            if (IsInited)
            {
                _manager.DisableURPCameraStack();
            }
        }

        /// <summary>
        /// 完整关闭 UIFrame：取消加载、销毁已打开与缓存面板、释放 Handle，并销毁 Root。
        /// 注册目录会保留，之后可再次调用 Init。
        /// </summary>
        public static void Shutdown()
        {
            ShutdownInternal(destroyRoot: true);
        }

        internal static void NotifyRootDestroyed()
        {
            ShutdownInternal(destroyRoot: false);
        }

        static void ShutdownInternal(bool destroyRoot)
        {
            if (_shuttingDown || _manager == null)
            {
                return;
            }

            _shuttingDown = true;
            try
            {
                var manager = _manager;
                _manager = null;
                manager.Shutdown(destroyRoot);
            }
            finally
            {
                _shuttingDown = false;
            }
        }

        /// <summary>注册地址与分组。关闭默认进缓存，不释放内存。</summary>
        public static void Register<TPanel>(string location, UIGroup group = UIGroup.Scene)
            where TPanel : UIPanel
        {
            UIPanelCatalog.Register<TPanel>(location, group);
        }

        /// <summary>显式指定关闭后是否缓存。cache: false 时 Close 会销毁并释放 Handle。</summary>
        public static void Register<TPanel>(string location, UIGroup group, bool cache)
            where TPanel : UIPanel
        {
            UIPanelCatalog.Register<TPanel>(location, group, cache);
        }

        public static UniTask<TPanel> Hud<TPanel>() where TPanel : UIPanel<UINone>
        {
            return Open<TPanel, UINone>(UIOpenMode.Hud, UINone.Value);
        }

        public static UniTask<TPanel> Hud<TPanel, TArgs>(TArgs args) where TPanel : UIPanel<TArgs>
        {
            return Open<TPanel, TArgs>(UIOpenMode.Hud, args);
        }

        public static UniTask<TPanel> Push<TPanel>() where TPanel : UIPanel<UINone>
        {
            return Open<TPanel, UINone>(UIOpenMode.Push, UINone.Value);
        }

        /// <summary>
        /// 打开窗口。同一类型若正在加载，后一次 Args/Mode 覆盖前一次，两次 await 拿到同一块面板。
        /// </summary>
        public static UniTask<TPanel> Push<TPanel, TArgs>(TArgs args) where TPanel : UIPanel<TArgs>
        {
            return Open<TPanel, TArgs>(UIOpenMode.Push, args);
        }

        public static UniTask<TPanel> Popup<TPanel>() where TPanel : UIPanel<UINone>
        {
            return Open<TPanel, UINone>(UIOpenMode.Popup, UINone.Value);
        }

        public static UniTask<TPanel> Popup<TPanel, TArgs>(TArgs args) where TPanel : UIPanel<TArgs>
        {
            return Open<TPanel, TArgs>(UIOpenMode.Popup, args);
        }

        /// <summary>
        /// 打开 Tips 层面板。同类型单实例，不进 Toast 队列，也不进 Window / Popup 栈。
        /// 需要多实例自动关闭请用 <see cref="Toast{TPanel}"/>。
        /// </summary>
        public static UniTask<TPanel> Tips<TPanel>() where TPanel : UIPanel<UINone>
        {
            return Open<TPanel, UINone>(UIOpenMode.Tips, UINone.Value);
        }

        /// <summary>
        /// 打开 Tips 层面板。同类型单实例，不进 Toast 队列，也不进 Window / Popup 栈。
        /// 需要多实例自动关闭请用 <see cref="Toast{TPanel, TArgs}"/>。
        /// </summary>
        public static UniTask<TPanel> Tips<TPanel, TArgs>(TArgs args) where TPanel : UIPanel<TArgs>
        {
            return Open<TPanel, TArgs>(UIOpenMode.Tips, args);
        }

        /// <summary>打开引导层面板，叠在 Tips 之上。不进 Window / Popup 栈。</summary>
        public static UniTask<TPanel> Guide<TPanel>() where TPanel : UIPanel<UINone>
        {
            return Open<TPanel, UINone>(UIOpenMode.Guide, UINone.Value);
        }

        /// <summary>打开引导层面板，叠在 Tips 之上。不进 Window / Popup 栈。</summary>
        public static UniTask<TPanel> Guide<TPanel, TArgs>(TArgs args) where TPanel : UIPanel<TArgs>
        {
            return Open<TPanel, TArgs>(UIOpenMode.Guide, args);
        }

        /// <summary>
        /// 配置 Tips 层：同时可见条数、等待队列长度、默认自动关闭秒数。
        /// <paramref name="defaultDuration"/> ≤ 0 表示常驻到手动关闭。队列满时丢掉最旧等待项。
        /// Shutdown 后仍保留该配置。
        /// </summary>
        public static void ConfigureTips(
            int maxVisible = TipsSettings.DefaultMaxVisible,
            int maxQueued = TipsSettings.DefaultMaxQueued,
            float defaultDuration = TipsSettings.DefaultDurationSeconds)
        {
            _tipsSettings = new TipsSettings(maxVisible, maxQueued, defaultDuration);
            if (!IsInited)
            {
                return;
            }

            _manager.ConfigureTips(
                _tipsSettings.MaxVisible,
                _tipsSettings.MaxQueued,
                _tipsSettings.DefaultDuration);
        }

        /// <summary>
        /// 打开 Tips Toast。<paramref name="duration"/> 为空用 ConfigureTips 的默认值；
        /// ≤ 0 常驻到手动关闭。可见已满时只入队，出队后才加载。
        /// </summary>
        public static UniTask<TPanel> Toast<TPanel>(float? duration = null)
            where TPanel : UIPanel<UINone>
        {
            return Toast<TPanel, UINone>(UINone.Value, duration);
        }

        /// <summary>
        /// 打开 Tips Toast。<paramref name="duration"/> 为空用 ConfigureTips 的默认值；
        /// ≤ 0 常驻到手动关闭。可见已满时只入队，出队后才加载。
        /// </summary>
        public static UniTask<TPanel> Toast<TPanel, TArgs>(TArgs args, float? duration = null)
            where TPanel : UIPanel<TArgs>
        {
            if (!EnsureInit())
            {
                return UniTask.FromResult<TPanel>(null);
            }

            return _manager.Toast<TPanel>(args, duration);
        }

        public static void Back()
        {
            if (!IsInited)
            {
                return;
            }

            _manager.Back();
        }

        /// <summary>关闭面板。默认隐藏进缓存，不 Destroy、不释放 Handle。</summary>
        public static void Close<TPanel>(bool destroy = false) where TPanel : UIPanel
        {
            Close(typeof(TPanel), destroy);
        }

        /// <summary>关闭面板。默认隐藏进缓存；<paramref name="destroy"/> 为 true 时才释放内存。</summary>
        public static void Close(Type panelType, bool destroy = false)
        {
            if (!IsInited)
            {
                return;
            }

            _manager.Close(panelType, destroy);
        }

        public static void CloseInstance(UIPanel panel, bool destroy = false)
        {
            if (!IsInited)
            {
                return;
            }

            _manager.CloseInstance(panel, destroy);
        }

        /// <summary>关闭并销毁，释放 GameObject 与 YooAsset Handle。</summary>
        public static void Destroy<TPanel>() where TPanel : UIPanel
        {
            Close<TPanel>(destroy: true);
        }

        /// <summary>关闭并销毁，释放 GameObject 与 YooAsset Handle。</summary>
        public static void Destroy(Type panelType)
        {
            Close(panelType, destroy: true);
        }

        /// <summary>
        /// 关闭该分组下已打开的面板。默认进缓存；切场景要释放内存时传 <paramref name="destroy"/> true。
        /// </summary>
        public static void CloseGroup(UIGroup group, bool destroy = false)
        {
            if (!IsInited)
            {
                return;
            }

            _manager.CloseGroup(group, destroy);
        }

        /// <summary>销毁所有已关闭进缓存的面板（含 Tips 闲置 Toast），释放内存。不影响当前打开的界面。</summary>
        public static void ClearCache()
        {
            if (!IsInited)
            {
                return;
            }

            _manager.ClearCache();
        }

        public static TPanel Get<TPanel>() where TPanel : UIPanel
        {
            return IsInited ? _manager.Get<TPanel>() : null;
        }

        public static bool IsOpen<TPanel>() where TPanel : UIPanel
        {
            return IsInited && _manager.IsOpen<TPanel>();
        }

        static bool EnsureInit()
        {
            if (_shuttingDown)
            {
                return false;
            }

            if (_manager != null && _manager.IsInited)
            {
                return true;
            }

            _manager = new UIManager();
            _manager.Init();
            _manager.ConfigureTips(
                _tipsSettings.MaxVisible,
                _tipsSettings.MaxQueued,
                _tipsSettings.DefaultDuration);
            return true;
        }

        static UniTask<TPanel> Open<TPanel, TArgs>(UIOpenMode mode, TArgs args)
            where TPanel : UIPanel<TArgs>
        {
            if (!EnsureInit())
            {
                return UniTask.FromResult<TPanel>(null);
            }

            return _manager.Open<TPanel, TArgs>(mode, args);
        }
    }
}
