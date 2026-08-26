using System;
using System.Runtime.CompilerServices;
using UnityEngine.Pool;

namespace Game.Pooling
{
    /// <summary>
    /// 可配置的主线程托管对象池。稳态 Get/Release 不产生托管分配。
    /// </summary>
    public sealed class ManagedObjectPool<T> : IDisposable where T : class
    {
        private readonly ObjectPool<T> pool;
        private readonly Func<T> create;
        private readonly Action<T> onRent;
        private readonly Action<T> onReturn;
        private readonly Action<T> onDestroy;

        private bool disposed;
        private int totalCreated;
        private int totalDestroyed;

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

            ManagedPoolOptions value = options ?? ManagedPoolOptions.Default;
            pool = new ObjectPool<T>(
                Create,
                OnRent,
                OnReturn,
                OnDestroy,
                value.CollectionCheck,
                value.InitialCapacity,
                value.MaxSize);
        }

        public int CountAll => pool.CountAll;
        public int CountActive => pool.CountActive;
        public int CountInactive => pool.CountInactive;

        public PoolStats Stats => new(
            pool.CountAll,
            pool.CountActive,
            pool.CountInactive,
            totalCreated,
            totalDestroyed);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get()
        {
            ThrowIfDisposed();
            return pool.Get();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Release(T item)
        {
            ThrowIfDisposed();

            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            pool.Release(item);
        }

        public void Clear()
        {
            ThrowIfDisposed();
            pool.Clear();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            pool.Dispose();
        }

        private T Create()
        {
            T item = create();
            if (item == null)
            {
                throw new InvalidOperationException("Pool factory returned null.");
            }

            totalCreated++;
            return item;
        }

        private void OnRent(T item)
        {
            onRent?.Invoke(item);
        }

        private void OnReturn(T item)
        {
            onReturn?.Invoke(item);
        }

        private void OnDestroy(T item)
        {
            totalDestroyed++;
            onDestroy?.Invoke(item);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(ManagedObjectPool<T>));
            }
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
