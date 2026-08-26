using UnityEngine;
using UnityEngine.UI;

namespace UIFrame
{
    [DisallowMultipleComponent]
    public abstract class UILoopScrollBase<TArgs> :
        UIPanel<TArgs>,
        LoopScrollPrefabSource,
        LoopScrollDataSource
    {
        [SerializeField]
        LoopScrollRect _scrollRect;

        protected LoopScrollRect ScrollRect => _scrollRect;

        protected sealed override void OnCreate()
        {
            if (_scrollRect == null)
            {
                Debug.LogError($"[UIFrame] {name} 未绑定 {nameof(LoopScrollRect)}。", this);
                return;
            }

            _scrollRect.prefabSource = this;
            _scrollRect.dataSource = this;
            OnLoopScrollCreated();
        }

        /// <summary>循环列表创建完成，可在这里绑定其他事件。</summary>
        protected virtual void OnLoopScrollCreated()
        {
        }

        /// <summary>获取或创建指定索引的单元格。</summary>
        public abstract GameObject GetObject(int index);

        /// <summary>回收单元格。</summary>
        public abstract void ReturnObject(Transform item);

        /// <summary>刷新指定索引的单元格数据。</summary>
        public abstract void ProvideData(Transform item, int index);
    }
}