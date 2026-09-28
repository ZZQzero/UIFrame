using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Game.Pooling;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UIFrame.Regression
{
    public class ManagedPoolFailureTests
    {
        [Test] public void FailedClearRetainsOnlyUntouchedObjects()
        {
            var destroyed = new HashSet<object>();
            var primary = new InvalidOperationException("clear-primary");
            int rents = 0, returns = 0;
            using var pool = new ManagedObjectPool<object>(() => new object(),
                onRent: _ => rents++, onReturn: _ => returns++, onDestroy: item =>
                {
                    Assert.IsTrue(destroyed.Add(item), "不能重复销毁");
                    if (destroyed.Count == 2) throw primary;
                });
            var a = pool.Get(); var b = pool.Get(); var c = pool.Get();
            pool.Release(a); pool.Release(b); pool.Release(c);
            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(pool.Clear));
            Assert.AreEqual(1, pool.CountInactive);
            Assert.AreEqual(1, pool.CountAll);
            Assert.AreEqual(0, pool.CountActive);
            Assert.AreEqual(3, rents, "清理不能借助 Get 触发租出回调");
            Assert.AreEqual(3, returns);
            var remaining = pool.Get();
            Assert.IsFalse(destroyed.Contains(remaining));
            pool.Release(remaining);
            pool.Clear();
            Assert.AreEqual(3, destroyed.Count);
            Assert.AreEqual(0, pool.CountAll);
        }

        [Test] public void RentFailureReleasesUntransferredItemAndPreservesPrimary()
        {
            var primary = new InvalidOperationException("rent-primary");
            int destroyed = 0;
            using var pool = new ManagedObjectPool<object>(() => new object(),
                onRent: _ => throw primary, onDestroy: _ =>
                {
                    destroyed++;
                    throw new InvalidOperationException("rent-cleanup-secondary");
                });
            LogAssert.Expect(LogType.Exception, new Regex("rent-cleanup-secondary"));
            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(() => pool.Get()));
            Assert.AreEqual(1, destroyed);
            Assert.AreEqual(0, pool.CountAll);
            Assert.AreEqual(0, pool.CountActive);
        }

        [Test] public void ReturnFailureKeepsCallerOwnership()
        {
            bool fail = true;
            var primary = new InvalidOperationException("return-primary");
            using var pool = new ManagedObjectPool<object>(() => new object(), onReturn: _ =>
            { if (fail) throw primary; });
            var item = pool.Get();
            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(() => pool.Release(item)));
            Assert.AreEqual(1, pool.CountActive);
            Assert.AreEqual(0, pool.CountInactive);
            fail = false;
            pool.Release(item);
            Assert.AreSame(item, pool.Get());
            pool.Release(item);
        }

        [Test] public void CapacityAndClearPreserveActiveCount()
        {
            int destroyed = 0;
            using var pool = new ManagedObjectPool<object>(() => new object(),
                onDestroy: _ => destroyed++, options: new ManagedPoolOptions(maxSize: 1));
            var active = pool.Get(); var a = pool.Get(); var b = pool.Get();
            pool.Release(a); pool.Release(b);
            Assert.AreEqual(1, destroyed);
            Assert.AreEqual(2, pool.CountAll);
            pool.Clear();
            Assert.AreEqual(1, pool.CountAll);
            Assert.AreEqual(1, pool.CountActive);
            pool.Release(active);
            Assert.AreEqual(1, pool.CountAll);
            Assert.AreEqual(0, pool.CountActive);
        }

        [Test] public void LifecycleCallbackCannotMutateSamePool()
        {
            ManagedObjectPool<object> pool = null;
            pool = new ManagedObjectPool<object>(() => new object(), onDestroy: _ => pool.Get());
            var item = pool.Get(); pool.Release(item);
            Assert.Throws<InvalidOperationException>(pool.Clear);
            Assert.AreEqual(0, pool.CountAll);
            pool.Dispose();
        }

        [Test] public void WarmGetReleaseDoesNotAllocate()
        {
            using var pool = new ManagedObjectPool<object>(() => new object(), options: new ManagedPoolOptions(1));
            for (int i = 0; i < 100; i++) pool.Release(pool.Get());
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) pool.Release(pool.Get());
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, allocated);
        }
    }
}
