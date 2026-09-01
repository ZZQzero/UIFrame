using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Pooling;
using UnityEngine;
using UnityEngine.UI;

namespace UIFrame
{
    [DisallowMultipleComponent]
    public abstract class UILoopScrollMultiBase<TArgs> :
        UIPanel<TArgs>,
        LoopScrollMultiDataSource
    {
        [SerializeField]
        LoopScrollRectMulti _scrollRect;

        readonly LoopScrollPoolSource _poolSource = new();

        protected LoopScrollRectMulti ScrollRect => _scrollRect;

        protected LoopScrollPoolSource PoolSource => _poolSource;

        protected GameObjectPoolService Pool => _poolSource.Pool;

        public void SetPool(GameObjectPoolService pool)
        {
            _poolSource.SetPool(pool);
        }

        public UniTask PrepareCellsAsync(
            IReadOnlyList<string> locations,
            GameObjectPoolOptions options = null,
            CancellationToken cancellationToken = default)
        {
            return _poolSource.PrepareLocationsAsync(locations, options, cancellationToken);
        }

        public UniTask PrewarmCellsAsync(
            IReadOnlyList<string> locations,
            int count,
            GameObjectPoolOptions options = null,
            CancellationToken cancellationToken = default)
        {
            return _poolSource.PrewarmLocationsAsync(locations, count, options, cancellationToken);
        }

        protected sealed override void OnCreate()
        {
            if (_scrollRect == null)
            {
                Debug.LogError($"[UIFrame] {name} 未绑定 {nameof(LoopScrollRectMulti)}。", this);
                return;
            }

            _poolSource.Bind(_scrollRect, GetCellLocation);
            _scrollRect.dataSource = this;
            OnLoopScrollCreated();
        }

        /// <summary>关闭时还池。覆写须调用 base。</summary>
        protected override void OnClose()
        {
            LoopScrollPoolSource.Clear(_scrollRect);
        }

        /// <summary>销毁面板时还池。覆写须调用 base。</summary>
        protected override void OnDestroyPanel()
        {
            LoopScrollPoolSource.Clear(_scrollRect);
        }

        /// <summary>循环列表创建完成，可在这里绑定其他事件。</summary>
        protected virtual void OnLoopScrollCreated()
        {
        }

        /// <summary>根据索引返回对应类型的 Cell location。</summary>
        protected abstract string GetCellLocation(int index);

        /// <summary>刷新指定索引的单元格数据。</summary>
        public abstract void ProvideData(Transform item, int index);
    }
}
