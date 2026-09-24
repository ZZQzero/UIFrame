using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using UnityEngine;
using YooAsset;

namespace UIFrame
{
    /// <summary>
    /// 非泛型面板基础设施。没有业务 Data；打开参数只存在 <see cref="UIPanel{TArgs}.Args"/>。
    /// </summary>
    public abstract class UIPanel : MonoBehaviour
    {
        public Type PanelType => GetType();

        public UILayer Layer { get; internal set; }

        public UIGroup Group { get; internal set; }

        public UIOpenMode OpenMode { get; internal set; }

        public string Location { get; internal set; }

        internal AssetHandle AssetHandle { get; set; }
        internal bool DestroyDispatched { get; private set; }

        UIFrameScope _openScope;
        UIFrameScope _lifetimeScope;
        static readonly CancellationToken ClosedOpenToken = new CancellationToken(true);

        /// <summary>当前这次打开的生命周期令牌。关闭、重新打开或销毁时取消。</summary>
        protected CancellationToken OpenCancellationToken =>
            _openScope?.Token ?? ClosedOpenToken;

        /// <summary>当前打开周期的作用域，关闭或重新打开时释放。</summary>
        protected UIFrameScope OpenScope =>
            _openScope ?? throw new InvalidOperationException(
                $"[UIFrame] {PanelType.FullName} 当前没有打开作用域。");

        /// <summary>面板实例的作用域，销毁时释放。</summary>
        protected UIFrameScope LifetimeScope =>
            _lifetimeScope ??= new UIFrameScope(destroyCancellationToken);

        /// <summary>关闭后是否进缓存（隐藏、保留实例与 YooAsset Handle）。默认 true。</summary>
        internal bool CacheOnClose { get; set; } = true;

        /// <summary>Popup 点遮罩是否关闭。Toast / Tips / Hud / Guide 忽略。</summary>
        public virtual bool CloseOnMaskClick => OpenMode == UIOpenMode.Popup;

        /// <summary>首次实例化且尚未激活时调用，适合绑按钮。</summary>
        protected virtual void OnCreate()
        {
        }

        protected virtual void OnResume()
        {
        }

        protected virtual void OnPause()
        {
        }

        protected virtual void OnClose()
        {
        }

        protected virtual void OnDestroyPanel()
        {
        }

        internal abstract void ApplyArgs(object args);

        internal void DispatchOpen()
        {
            CancelOpenScope();
            _openScope = new UIFrameScope(destroyCancellationToken);
            PrepareOpen();
            DispatchOpenCore();
        }

        internal abstract void DispatchOpenCore();

        /// <summary>每次打开前调用。带返回值的面板在这里准备结果通道。</summary>
        protected virtual void PrepareOpen()
        {
        }

        /// <summary>关闭或销毁后调用。未提交的结果在这里取消。</summary>
        protected virtual void CompleteOpen()
        {
        }

        internal void DispatchCreate()
        {
            OnCreate();
        }

        internal void DispatchResume()
        {
            OnResume();
        }

        internal void DispatchPause()
        {
            OnPause();
        }

        internal void DispatchClose()
        {
            DispatchEnd(destroy: false);
        }

        internal void DispatchDestroy()
        {
            if (DestroyDispatched)
            {
                return;
            }

            DestroyDispatched = true;
            Exception failure = null;
            try
            {
                DispatchEnd(destroy: true);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            try
            {
                _lifetimeScope?.Dispose();
            }
            catch (Exception exception) when (failure != null)
            {
                Debug.LogException(new InvalidOperationException(
                    $"[UIFrame] {PanelType.FullName} LifetimeScope 收尾失败；调用方仍收到首次异常。Location={Location}", exception));
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (failure != null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        void DispatchEnd(bool destroy)
        {
            Exception failure = null;
            try
            {
                CancelOpenScope();
                if (destroy)
                    OnDestroyPanel();
                else
                    OnClose();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            try
            {
                // 结果通道必须终结，即使取消回调或业务回调失败。
                CompleteOpen();
            }
            catch (Exception exception) when (failure != null)
            {
                Debug.LogException(new InvalidOperationException(
                    $"[UIFrame] {PanelType.FullName} 收尾失败；调用方仍收到首次异常。Location={Location}", exception));
            }

            if (failure != null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        void OnDestroy()
        {
            try
            {
                CancelOpenScope();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }

            try
            {
                _lifetimeScope?.Dispose();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            if (!DestroyDispatched)
            {
                Debug.LogError(
                    $"[UIFrame] 面板 {GetType().Name} 被外部 Destroy，未经过 UI.Close/Shutdown。");
            }

            var handle = AssetHandle;
            if (handle == null)
            {
                return;
            }

            AssetHandle = null;
            UILoader.Release(handle);
        }

        void CancelOpenScope()
        {
            if (_openScope == null)
            {
                return;
            }

            var scope = _openScope;
            _openScope = null;
            scope.Dispose();
        }

        /// <summary>关闭自己。默认隐藏进缓存，不 Destroy、不释放 Handle。</summary>
        public void CloseSelf(bool destroy = false)
        {
            UI.CloseInstance(this, destroy);
        }

        /// <summary>关闭并销毁自己，释放 GameObject 与 YooAsset Handle。</summary>
        public void CloseAndDestroySelf()
        {
            UI.CloseInstance(this, destroy: true);
        }
    }
}
