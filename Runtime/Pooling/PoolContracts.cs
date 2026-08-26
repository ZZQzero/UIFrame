using System;

namespace Game.Pooling
{
    /// <summary>
    /// 纯托管对象的可选生命周期接口。
    /// </summary>
    public interface IManagedPoolable
    {
        void OnRent();
        void OnReturn();
    }

    /// <summary>
    /// GameObject 从池中取出或归还时的生命周期接口。
    /// </summary>
    public interface IPoolable
    {
        void OnSpawned();
        void OnDespawned();
    }

    public enum PoolGroup
    {
        Default = 0,
        Role,
        UI,
        Effect
    }

    public sealed class ManagedPoolOptions
    {
        public const int DefaultMaxSize = 128;

        public int InitialCapacity { get; }
        public int MaxSize { get; }
        public bool CollectionCheck { get; }

        public ManagedPoolOptions(
            int initialCapacity = 0,
            int maxSize = DefaultMaxSize,
            bool collectionCheck = true)
        {
            if (initialCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(initialCapacity));
            }

            if (maxSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxSize));
            }

            if (initialCapacity > maxSize)
            {
                throw new ArgumentException(
                    "Initial capacity cannot be greater than max size.",
                    nameof(initialCapacity));
            }

            InitialCapacity = initialCapacity;
            MaxSize = maxSize;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            CollectionCheck = collectionCheck;
#else
            CollectionCheck = false;
#endif
        }

        public static ManagedPoolOptions Default { get; } = new();
    }

    public sealed class GameObjectPoolOptions
    {
        public const int DefaultMaxSize = ManagedPoolOptions.DefaultMaxSize;
        public const int DefaultPrewarmPerFrame = 8;

        public int InitialCapacity { get; }
        public int MaxSize { get; }
        public int PrewarmPerFrame { get; }
        public bool CollectionCheck { get; }
        public PoolGroup Group { get; }

        public GameObjectPoolOptions(
            int initialCapacity = 0,
            int maxSize = DefaultMaxSize,
            int prewarmPerFrame = DefaultPrewarmPerFrame,
            bool collectionCheck = true,
            PoolGroup group = PoolGroup.Default)
        {
            if (prewarmPerFrame <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(prewarmPerFrame));
            }

            var managedOptions =
                new ManagedPoolOptions(initialCapacity, maxSize, collectionCheck);
            InitialCapacity = managedOptions.InitialCapacity;
            MaxSize = managedOptions.MaxSize;
            CollectionCheck = managedOptions.CollectionCheck;
            PrewarmPerFrame = prewarmPerFrame;
            Group = group;
        }

        internal ManagedPoolOptions ToManagedOptions()
        {
            return new ManagedPoolOptions(
                InitialCapacity,
                MaxSize,
                CollectionCheck);
        }

        public static GameObjectPoolOptions Default { get; } = new();
    }

    public readonly struct PoolStats
    {
        public int CountAll { get; }
        public int CountActive { get; }
        public int CountInactive { get; }
        public int TotalCreated { get; }
        public int TotalDestroyed { get; }

        public PoolStats(
            int countAll,
            int countActive,
            int countInactive,
            int totalCreated,
            int totalDestroyed)
        {
            CountAll = countAll;
            CountActive = countActive;
            CountInactive = countInactive;
            TotalCreated = totalCreated;
            TotalDestroyed = totalDestroyed;
        }
    }

    public readonly struct PrefabPoolStats
    {
        public PoolStats Pool { get; }
        public int PeakActive { get; }
        public int SynchronousExpansionCount { get; }

        public int CountAll => Pool.CountAll;
        public int CountActive => Pool.CountActive;
        public int CountInactive => Pool.CountInactive;

        public PrefabPoolStats(
            PoolStats pool,
            int peakActive,
            int synchronousExpansionCount)
        {
            Pool = pool;
            PeakActive = peakActive;
            SynchronousExpansionCount = synchronousExpansionCount;
        }
    }
}
