using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Pooling;
using UnityEngine;
using UnityEngine.UI;

namespace UIFrame
{
    [DisallowMultipleComponent]
    public abstract class UILoopScrollBase<TArgs> :
        UIPanel<TArgs>,
        LoopScrollDataSource
    {
        [SerializeField]
        LoopScrollRect _scrollRect;

        [SerializeField]
        [Tooltip("Cell Prefab 的 YooAsset location。")]
        string _cellLocation;

        readonly LoopScrollPoolSource _poolSource = new();

        protected LoopScrollRect ScrollRect => _scrollRect;

        protected LoopScrollPoolSource PoolSource => _poolSource;

        protected GameObjectPoolService Pool => _poolSource.Pool;

        protected string CellLocation => _cellLocation;

        public void SetPool(GameObjectPoolService pool)
        {
            _poolSource.SetPool(pool);
        }

        public UniTask PrepareCellsAsync(
            GameObjectPoolOptions options = null,
            CancellationToken cancellationToken = default)
        {
            _poolSource.EnsurePool();
            return Pool.PrepareAsync(RequireCellLocation(), options, cancellationToken);
        }

        public UniTask PrewarmCellsAsync(
            int count,
            GameObjectPoolOptions options = null,
            CancellationToken cancellationToken = default)
        {
            _poolSource.EnsurePool();
            return Pool.PrewarmAsync(RequireCellLocation(), count, options, cancellationToken);
        }

        protected sealed override void OnCreate()
        {
            if (_scrollRect == null)
            {
                Debug.LogError($"[UIFrame] {name} 未绑定 {nameof(LoopScrollRect)}。", this);
                return;
            }

            if (string.IsNullOrWhiteSpace(GetCellLocation(0)))
            {
                Debug.LogError($"[UIFrame] {name} 未设置 Cell location。", this);
            }

            _poolSource.Bind(_scrollRect, GetCellLocation);
            _scrollRect.dataSource = this;
            OnLoopScrollCreated();
        }

        protected override void OnDisabled()
        {
            LoopScrollPoolSource.Clear(_scrollRect);
        }

        /// <summary>循环列表创建完成，可在这里绑定其他事件。</summary>
        protected virtual void OnLoopScrollCreated()
        {
        }

        /// <summary>指定索引对应的 Cell location。默认使用序列化字段。</summary>
        protected virtual string GetCellLocation(int index)
        {
            return _cellLocation;
        }

        /// <summary>刷新指定索引的单元格数据。</summary>
        public abstract void ProvideData(Transform item, int index);

        string RequireCellLocation()
        {
            string location = GetCellLocation(0);
            if (string.IsNullOrWhiteSpace(location))
            {
                throw new InvalidOperationException(
                    $"{name} 未设置 Cell location。");
            }

            return location.Trim();
        }
    }
}
