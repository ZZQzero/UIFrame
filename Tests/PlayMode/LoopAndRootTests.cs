using System;
using System.Collections;
using Cysharp.Threading.Tasks;
using Game.Pooling;
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

        sealed class AddressProvider : IPrefabProvider, IPrefabHandle
        {
            public string LoadedLocation;
            public UniTask<IPrefabHandle> LoadAsync(string location)
            {
                LoadedLocation = location;
                return UniTask.FromResult<IPrefabHandle>(this);
            }
            public GameObject Instantiate(Transform parent)
            {
                var cell = new GameObject("address-cell", typeof(RectTransform));
                cell.transform.SetParent(parent, false);
                return cell;
            }
            public void Dispose() { }
        }

        [TestCase(false, "cell")] [TestCase(true, "cell")]
        [TestCase(false, " cell ")] [TestCase(true, " cell ")]
        public void PreparedCellAddressIsUsedWithoutRewriting(bool prewarm, string location)
        {
            var provider = new AddressProvider();
            using var pool = new GameObjectPoolService(provider);
            var source = new LoopScrollPoolSource();
            source.SetPool(pool);
            if (prewarm) source.PrewarmLocationsAsync(new[] { location }, 1).GetAwaiter().GetResult();
            else source.PrepareLocationsAsync(new[] { location }).GetAwaiter().GetResult();
            Assert.AreEqual(location, provider.LoadedLocation);
            source.SetLocation(location);
            var cell = source.GetObject(0);
            source.ReturnObject(cell.transform);
            source.SetLocation(_ => location);
            Assert.AreSame(cell, source.GetObject(0));
            source.ReturnObject(cell.transform);
            Assert.AreEqual(1, pool.PoolCount);
            Assert.Throws<ArgumentException>(() => source.SetLocation(" "));
        }

        [Test] public void FixedScrollbarSizeRejectsInvalidValuesWithoutChangingState()
        {
            var root = new GameObject("scrollbar-test", typeof(RectTransform));
            root.SetActive(false);
            try
            {
                var scroll = root.AddComponent<LoopVerticalScrollRect>();
                foreach (float valid in new[] { 0f, 0.25f, 1f })
                {
                    scroll.fixedHorizontalScrollbarSize = valid;
                    scroll.fixedVerticalScrollbarSize = valid;
                    foreach (float invalid in new[] { -1f, 2f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                    {
                        Assert.Throws<ArgumentOutOfRangeException>(() => scroll.fixedHorizontalScrollbarSize = invalid);
                        Assert.Throws<ArgumentOutOfRangeException>(() => scroll.fixedVerticalScrollbarSize = invalid);
                        Assert.AreEqual(valid, scroll.fixedHorizontalScrollbarSize);
                        Assert.AreEqual(valid, scroll.fixedVerticalScrollbarSize);
                    }
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [UnityTest] public IEnumerator InvalidScrollRequestThrowsBeforeStoppingExistingCoroutines()
        {
            var root = new GameObject("scroll-request-test", typeof(RectTransform));
            root.SetActive(false);
            try
            {
                ((RectTransform)root.transform).sizeDelta = new Vector2(100f, 100f);
                var scroll = root.AddComponent<LoopVerticalScrollRect>();
                scroll.horizontal = false;
                var content = new GameObject("content", typeof(RectTransform));
                content.transform.SetParent(root.transform, false);
                scroll.content = (RectTransform)content.transform;
                scroll.content.anchorMin = new Vector2(0, 1);
                scroll.content.anchorMax = Vector2.one;
                scroll.content.pivot = new Vector2(0.5f, 1);
                var source = new CellSource { Size = 20f };
                scroll.prefabSource = source;
                scroll.dataSource = source;
                scroll.totalCount = 20;
                root.SetActive(true);
                scroll.RefillCells();
                bool continued = false;
                IEnumerator ExistingWork() { yield return null; continued = true; }
                scroll.StartCoroutine(ExistingWork());
                foreach (int index in new[] { -1, 20 })
                {
                    Assert.Throws<ArgumentOutOfRangeException>(() => scroll.ScrollToCell(index, 1));
                    Assert.Throws<ArgumentOutOfRangeException>(() => scroll.ScrollToCellWithinTime(index, 1));
                }
                foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
                {
                    Assert.Throws<ArgumentOutOfRangeException>(() => scroll.ScrollToCell(0, invalid));
                    Assert.Throws<ArgumentOutOfRangeException>(() => scroll.ScrollToCellWithinTime(0, invalid));
                }
                Assert.Throws<ArgumentException>(() => scroll.ScrollToCellWithinTime(
                    0, 1, mode: LoopScrollRectBase.ScrollMode.JustAppear));
                yield return null;
                yield return null;
                Assert.IsTrue(continued);
                Assert.DoesNotThrow(() => scroll.ScrollToCell(0, 1));
                Assert.DoesNotThrow(() => scroll.ScrollToCellWithinTime(0, 1));
                scroll.StopAllCoroutines();
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
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
