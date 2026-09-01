using System;
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

        /// <summary>关闭后是否进缓存（隐藏、保留实例与 YooAsset Handle）。默认 true。</summary>
        internal bool CacheOnClose { get; set; } = true;

        /// <summary>Popup 点遮罩是否关闭。Toast / Hud 忽略。</summary>
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

        internal abstract void DispatchOpen();

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
            OnClose();
        }

        internal void DispatchDestroy()
        {
            OnDestroyPanel();
        }

        void OnDisable()
        {
            OnDisabled();
        }

        /// <summary>Unity 禁用该组件时。泛型子类应覆写此方法，不要声明 OnDisable。</summary>
        protected virtual void OnDisabled()
        {
        }

        void OnDestroy()
        {
            OnDestroyed();
            var handle = AssetHandle;
            if (handle == null)
            {
                return;
            }

            AssetHandle = null;
            UILoader.Release(handle);
        }

        /// <summary>Unity 销毁该组件时。泛型子类应覆写此方法，不要声明 OnDestroy。</summary>
        protected virtual void OnDestroyed()
        {
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
