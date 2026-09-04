using System;
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

        CancellationTokenSource _openCts;

        /// <summary>当前这次打开的生命周期令牌。关闭、重新打开或销毁时取消。</summary>
        protected CancellationToken OpenCancellationToken =>
            _openCts?.Token ?? destroyCancellationToken;

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
            _openCts = CancellationTokenSource.CreateLinkedTokenSource(
                destroyCancellationToken);
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
            CancelOpenScope();
            try
            {
                OnClose();
            }
            finally
            {
                CompleteOpen();
            }
        }

        internal void DispatchDestroy()
        {
            CancelOpenScope();
            try
            {
                OnDestroyPanel();
            }
            finally
            {
                CompleteOpen();
            }
        }

        void OnDestroy()
        {
            CancelOpenScope();
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
            if (_openCts == null)
            {
                return;
            }

            _openCts.Cancel();
            _openCts.Dispose();
            _openCts = null;
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
