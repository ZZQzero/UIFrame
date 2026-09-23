using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace UIFrame.Regression
{
    public class LoopAndRootTests
    {
        sealed class CellSource : LoopScrollPrefabSource, LoopScrollDataSource, LoopScrollMultiDataSource
        {
            public int Created;
            public float Size;
            public GameObject GetObject(int index)
            {
                Created++;
                var cell = new GameObject("Cell-" + index, typeof(RectTransform));
                ((RectTransform)cell.transform).sizeDelta = new Vector2(Size, Size);
                return cell;
            }
            public void ReturnObject(Transform trans) => UnityEngine.Object.Destroy(trans.gameObject);
            public void ProvideData(Transform transform, int index) { }
        }

        [TestCase(typeof(LoopVerticalScrollRect))]
        [TestCase(typeof(LoopVerticalScrollRectMulti))]
        [TestCase(typeof(LoopHorizontalScrollRect))]
        [TestCase(typeof(LoopHorizontalScrollRectMulti))]
        public void InfiniteRefillRejectsZeroCellAfterFirstAllocation(Type type)
        {
            var root = new GameObject("loop-test", typeof(RectTransform));
            root.SetActive(false);
            try
            {
                ((RectTransform)root.transform).sizeDelta = new Vector2(100f, 100f);
                var scroll = (LoopScrollRectBase)root.AddComponent(type);
                var content = new GameObject("content", typeof(RectTransform));
                content.transform.SetParent(root.transform, false);
                scroll.content = (RectTransform)content.transform;
                var source = new CellSource();
                scroll.prefabSource = source;
                if (scroll is LoopScrollRect single) single.dataSource = source;
                if (scroll is LoopScrollRectMulti multi) multi.dataSource = source;
                scroll.totalCount = -1;
                Assert.Throws<InvalidOperationException>(() => scroll.RefillCells());
                Assert.AreEqual(1, source.Created, "非法尺寸不能继续分配 Cell");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test] public void InvalidGridCannotBeSkippedOnSecondRefill()
        {
            var root = new GameObject("grid-test", typeof(RectTransform));
            root.SetActive(false);
            try
            {
                var scroll = root.AddComponent<LoopVerticalScrollRect>();
                var content = new GameObject("content", typeof(RectTransform));
                content.transform.SetParent(root.transform, false);
                var grid = content.AddComponent<GridLayoutGroup>();
                grid.constraint = GridLayoutGroup.Constraint.Flexible;
                scroll.content = (RectTransform)content.transform;
                Assert.Throws<InvalidOperationException>(() => scroll.RefillCells());
                Assert.Throws<InvalidOperationException>(() => scroll.RefillCells());
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [UnityTest] public IEnumerator ExistingEventSystemIsRejectedBeforeCreatingRoot()
        {
            var existing = new GameObject("external-event-system");
            existing.AddComponent<EventSystem>();
            try
            {
                int before = UnityEngine.Object.FindObjectsByType<UIFrameRoot>(FindObjectsSortMode.None).Length;
                Assert.Throws<InvalidOperationException>(() => UIFrameRoot.Create());
                Assert.AreEqual(before, UnityEngine.Object.FindObjectsByType<UIFrameRoot>(FindObjectsSortMode.None).Length);
                Assert.IsNotNull(existing.GetComponent<EventSystem>());
            }
            finally { UnityEngine.Object.Destroy(existing); }
            yield return null;
        }
    }
}
