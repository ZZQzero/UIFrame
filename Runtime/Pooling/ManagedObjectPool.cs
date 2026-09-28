using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Game.Pooling
{
    /// <summary>主线程托管对象池。稳态 Get/Release 不产生托管分配。</summary>
    public sealed class ManagedObjectPool<T> : IDisposable where T : class
    {
        readonly List<T> inactive;
        readonly Func<T> create;
        readonly Action<T> onRent;
        readonly Action<T> onReturn;
        readonly Action<T> onDestroy;
        readonly int maxSize;
        readonly bool collectionCheck;
        bool disposed;
        bool inCallback;
        int activeCount;
        int totalCreated;
        int totalDestroyed;

        public ManagedObjectPool(
            Func<T> create,
            Action<T> onRent = null,
            Action<T> onReturn = null,
            Action<T> onDestroy = null,
            ManagedPoolOptions options = null)
        {
            this.create = create ?? throw new ArgumentNullException(nameof(create));
            this.onRent = onRent;
            this.onReturn = onReturn;
            this.onDestroy = onDestroy;
            var value = options ?? ManagedPoolOptions.Default;
            inactive = new List<T>(value.InitialCapacity);
            maxSize = value.MaxSize;
            collectionCheck = value.CollectionCheck && UIFrame.UIFrameSafety.CollectionChecks;
        }

        public int CountAll => activeCount + inactive.Count;
        public int CountActive => activeCount;
        public int CountInactive => inactive.Count;
        public PoolStats Stats => new(CountAll, CountActive, CountInactive, totalCreated, totalDestroyed);

        public T Get()
        {
            EnsureUsable();
            T item;
            if (inactive.Count > 0) item = TakeInactive();
            else
            {
                inCallback = true;
                try { item = create(); }
                finally { inCallback = false; }
                if (item == null) throw new InvalidOperationException("Pool factory returned null.");
                totalCreated++;
            }
            activeCount++;
            try { Invoke(onRent, item); }
            catch
            {
                activeCount--;
                try { DestroyItem(item); }
                catch (Exception cleanupError) { UnityEngine.Debug.LogException(cleanupError); }
                throw;
            }
            return item;
        }

        public void Release(T item)
        {
            EnsureUsable();
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (collectionCheck)
                foreach (var idle in inactive)
                    if (ReferenceEquals(idle, item))
                        throw new InvalidOperationException("Object has already been released to this pool.");
            Invoke(onReturn, item);
            activeCount--;
            if (inactive.Count < maxSize) inactive.Add(item);
            else DestroyItem(item);
        }

        /// <summary>逐个移除并销毁闲置对象；失败即停止，未处理对象保留。</summary>
        public void Clear()
        {
            EnsureUsable();
            while (inactive.Count > 0) DestroyItem(TakeInactive());
        }

        public void Dispose()
        {
            if (disposed) return;
            EnsureUsable();
            disposed = true;
            var failure = new UIFrame.CleanupFailure();
            while (inactive.Count > 0)
            {
                var item = TakeInactive();
                try { DestroyItem(item); }
                catch (Exception exception) { failure.Capture(exception); }
            }
            failure.Throw();
        }

        T TakeInactive()
        {
            var index = inactive.Count - 1;
            var item = inactive[index];
            inactive.RemoveAt(index);
            return item;
        }

        void DestroyItem(T item)
        {
            totalDestroyed++;
            Invoke(onDestroy, item);
        }

        void Invoke(Action<T> callback, T item)
        {
            if (callback == null) return;
            inCallback = true;
            try { callback(item); }
            finally { inCallback = false; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void EnsureUsable()
        {
            if (disposed) throw new ObjectDisposedException(nameof(ManagedObjectPool<T>));
            if (inCallback) throw new InvalidOperationException("Pool cannot be mutated inside its lifecycle callbacks.");
        }
    }

    /// <summary>
    /// 无参数、高频托管对象的静态泛型快捷池。
    /// </summary>
    public static class StaticManagedPool<T>
        where T : class, IManagedPoolable, new()
    {
        private static readonly ManagedObjectPool<T> Pool = new(
            static () => new T(),
            static item => item.OnRent(),
            static item => item.OnReturn());

        public static PoolStats Stats => Pool.Stats;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Get()
        {
            return Pool.Get();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Release(T item)
        {
            Pool.Release(item);
        }

        public static void Clear()
        {
            Pool.Clear();
        }
    }
}
