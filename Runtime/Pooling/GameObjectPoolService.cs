using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Unity.Profiling;
using YooAsset;

namespace Game.Pooling
{
    /// <summary>
    /// 按 YooAsset location 分桶的主线程 GameObject 对象池。
    /// </summary>
    public sealed partial class GameObjectPoolService : IDisposable
    {
        private readonly IPrefabProvider prefabProvider;
        private static readonly ProfilerMarker SpawnProfilerMarker =
            new("Game.Pooling.Spawn");
        private static readonly ProfilerMarker DespawnProfilerMarker =
            new("Game.Pooling.Despawn");
        private readonly Transform poolRoot;
        private readonly bool ownsPoolRoot;
        private readonly int ownerThreadId;
        private readonly Dictionary<string, PoolBucket> buckets =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingLoad> pendingLoads =
            new(StringComparer.Ordinal);
        private readonly Dictionary<PoolGroup, Transform> groupRoots = new();

        private bool disposed;
        private int lifecycleCallbackDepth;

        public GameObjectPoolService(ResourcePackage package, Transform poolRoot = null)
            : this(new YooAssetPrefabProvider(package), poolRoot)
        {
        }

        public GameObjectPoolService(IPrefabProvider prefabProvider, Transform poolRoot = null)
        {
            this.prefabProvider =
                prefabProvider ?? throw new ArgumentNullException(nameof(prefabProvider));
            ownerThreadId = Thread.CurrentThread.ManagedThreadId;

            if (poolRoot == null)
            {
                var rootObject = new GameObject("[GameObjectPool]");
                this.poolRoot = rootObject.transform;
                ownsPoolRoot = true;
            }
            else
            {
                this.poolRoot = poolRoot;
                ownsPoolRoot = false;
            }
        }

        public int PoolCount => buckets.Count;

        public bool IsDisposed => disposed;

        public async UniTask PrepareAsync(
            string location,
            GameObjectPoolOptions options = null,
            CancellationToken cancellationToken = default)
        {
            EnsureUsable();
            ValidateLocation(location);
            cancellationToken.ThrowIfCancellationRequested();
            await GetOrCreateBucketAsync(location, options, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async UniTask PrewarmAsync(
            string location,
            int targetCount,
            GameObjectPoolOptions options = null,
            CancellationToken cancellationToken = default)
        {
            EnsureUsable();
            ValidateLocation(location);

            if (targetCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetCount));
            }

            cancellationToken.ThrowIfCancellationRequested();
            PoolBucket bucket =
                await GetOrCreateBucketAsync(location, options, cancellationToken);
            EnsureUsable();
            cancellationToken.ThrowIfCancellationRequested();
            bucket.EnsureAvailable();
            int cappedTarget = Math.Min(targetCount, bucket.Options.MaxSize);
            if (bucket.Pool.CountAll >= cappedTarget)
            {
                return;
            }

            int rentCapacity = Math.Max(0, cappedTarget - bucket.Pool.CountActive);
            var rented = new List<PooledInstanceMarker>(rentCapacity);
            int createdThisFrame = 0;
            bucket.BeginPrewarm();
            try
            {
                while (bucket.Pool.CountAll < cappedTarget)
                {
                    EnsureUsable();
                    cancellationToken.ThrowIfCancellationRequested();
                    bucket.EnsureAvailable();
                    int countAllBefore = bucket.Pool.CountAll;
                    PooledInstanceMarker marker = bucket.Pool.Get();
                    if (marker == null)
                    {
                        bucket.RegisterMissingInactiveInstance();
                        throw new InvalidOperationException(
                            $"An inactive pooled instance of '{location}' was destroyed externally.");
                    }

                    rented.Add(marker);
                    bool wasCreated = bucket.Pool.CountAll > countAllBefore;
                    if (!wasCreated)
                    {
                        continue;
                    }

                    createdThisFrame++;

                    if (createdThisFrame >= bucket.Options.PrewarmPerFrame &&
                        bucket.Pool.CountAll < cappedTarget)
                    {
                        createdThisFrame = 0;
                        await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
                    }
                }
            }
            finally
            {
                try
                {
                    for (int i = 0; i < rented.Count; i++)
                    {
                        bucket.ReturnPrewarmed(rented[i]);
                    }
                }
                finally
                {
                    bucket.EndPrewarm();
                }
            }
        }

        public async UniTask<GameObject> SpawnAsync(
            string location,
            Transform parent = null,
            GameObjectPoolOptions options = null,
            CancellationToken cancellationToken = default)
        {
            PoolBucket bucket =
                await GetOrCreateBucketAsync(location, options, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Spawn(bucket, parent);
        }

        public bool TrySpawn(string location, out GameObject instance)
        {
            return TrySpawn(location, null, out instance);
        }

        public bool TrySpawn(
            string location,
            Transform parent,
            out GameObject instance)
        {
            EnsureUsable();
            ValidateLocation(location);

            if (!buckets.TryGetValue(location, out PoolBucket bucket))
            {
                instance = null;
                return false;
            }

            instance = Spawn(bucket, parent);
            return true;
        }

        public bool TrySpawn<T>(string location, out T component)
            where T : Component
        {
            return TrySpawn(location, null, out component);
        }

        public bool TrySpawn<T>(
            string location,
            Transform parent,
            out T component)
            where T : Component
        {
            if (!TrySpawn(location, parent, out GameObject instance))
            {
                component = null;
                return false;
            }

            component = instance.GetComponent<T>();
            if (component != null)
            {
                return true;
            }

            DespawnImmediate(instance);
            component = null;
            return false;
        }

        public GameObject SpawnLoaded(string location, Transform parent = null)
        {
            if (TrySpawn(location, parent, out GameObject instance))
            {
                return instance;
            }

            throw new InvalidOperationException(
                $"Pool '{location}' is not prepared. Call PrepareAsync or PrewarmAsync first.");
        }

        public T SpawnLoaded<T>(string location, Transform parent = null)
            where T : Component
        {
            GameObject instance = SpawnLoaded(location, parent);
            return GetRequiredComponent<T>(instance, location);
        }

        public async UniTask<T> SpawnAsync<T>(
            string location,
            Transform parent = null,
            GameObjectPoolOptions options = null,
            CancellationToken cancellationToken = default)
            where T : Component
        {
            GameObject instance =
                await SpawnAsync(location, parent, options, cancellationToken);
            return GetRequiredComponent<T>(instance, location);
        }

        public bool Despawn(GameObject instance)
        {
            return DespawnImmediate(instance);
        }

        public bool DespawnImmediate(GameObject instance)
        {
            EnsureUsable();
            if (!TryGetActiveMarker(instance, out PooledInstanceMarker marker, out PoolBucket bucket))
            {
                return false;
            }

            return DespawnNow(marker, bucket);
        }

        private bool DespawnNow(PooledInstanceMarker marker, PoolBucket bucket)
        {
            using ProfilerMarker.AutoScope _ = DespawnProfilerMarker.Auto();
            GameObject instance = marker.gameObject;
            if (marker.State != PooledInstanceState.Active &&
                marker.State != PooledInstanceState.PendingDespawn)
            {
                return false;
            }

            marker.State = PooledInstanceState.Despawning;
            bucket.Active.Remove(marker);

            InvokeDespawnedSafely(marker.Callbacks, marker.Callbacks.Length);
            instance.SetActive(false);
            instance.transform.SetParent(bucket.StorageRoot, false);
            marker.State = PooledInstanceState.Inactive;
            bucket.Pool.Release(marker);

            return true;
        }

        private bool TryGetActiveMarker(
            GameObject instance,
            out PooledInstanceMarker marker,
            out PoolBucket bucket)
        {
            marker = null;
            bucket = null;
            return instance != null &&
                   instance.TryGetComponent(out marker) &&
                   marker.Owner == this &&
                   (marker.State == PooledInstanceState.Active ||
                    marker.State == PooledInstanceState.PendingDespawn) &&
                   buckets.TryGetValue(marker.Location, out bucket);
        }

        public bool TryGetStats(string location, out PoolStats stats)
        {
            EnsureUsable();
            ValidateLocation(location);

            if (buckets.TryGetValue(location, out PoolBucket bucket))
            {
                stats = bucket.GetStats();
                return true;
            }

            stats = default;
            return false;
        }

        public bool TryGetPrefabStats(string location, out PrefabPoolStats stats)
        {
            EnsureUsable();
            ValidateLocation(location);

            if (buckets.TryGetValue(location, out PoolBucket bucket))
            {
                stats = new PrefabPoolStats(
                    bucket.GetStats(),
                    bucket.PeakActive,
                    bucket.SynchronousExpansionCount);
                return true;
            }

            stats = default;
            return false;
        }

        public bool TryRemovePool(string location)
        {
            EnsureUsable();
            ValidateLocation(location);
            if (lifecycleCallbackDepth > 0)
            {
                return false;
            }

            if (pendingLoads.ContainsKey(location))
            {
                return false;
            }

            if (!buckets.TryGetValue(location, out PoolBucket bucket))
            {
                return true;
            }

            if (bucket.Active.Count > 0 || bucket.IsPrewarming)
            {
                return false;
            }

            buckets.Remove(location);
            bucket.Dispose(false);
            return true;
        }

        public void Dispose()
        {
            DisposeInternal(true);
        }

        public void ForceDispose()
        {
            DisposeInternal(true);
        }

        public bool TryDispose()
        {
            EnsureOwnerThread();
            if (disposed)
            {
                return true;
            }

            if (lifecycleCallbackDepth > 0)
            {
                return false;
            }

            if (pendingLoads.Count > 0)
            {
                return false;
            }

            foreach (PoolBucket bucket in buckets.Values)
            {
                if (bucket.Active.Count > 0 || bucket.IsPrewarming)
                {
                    return false;
                }
            }

            DisposeInternal(false);
            return true;
        }

        private void DisposeInternal(bool force)
        {
            EnsureOwnerThread();
            EnsureNoLifecycleMutation();
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (PoolBucket bucket in buckets.Values)
            {
                try
                {
                    bucket.Dispose(force);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }

            buckets.Clear();
            pendingDespawns.Clear();
            processingPendingDespawns = false;

            if (ownsPoolRoot && poolRoot != null)
            {
                DestroyGameObject(poolRoot.gameObject);
            }
            else
            {
                foreach (Transform groupRoot in groupRoots.Values)
                {
                    if (groupRoot != null)
                    {
                        DestroyGameObject(groupRoot.gameObject);
                    }
                }
            }

            groupRoots.Clear();
        }

        private async UniTask<PoolBucket> GetOrCreateBucketAsync(
            string location,
            GameObjectPoolOptions options,
            CancellationToken cancellationToken)
        {
            EnsureUsable();
            ValidateLocation(location);
            cancellationToken.ThrowIfCancellationRequested();

            if (buckets.TryGetValue(location, out PoolBucket bucket))
            {
                ValidateOptions(location, bucket.Options, options);
                bucket.EnsureAvailable();
                return bucket;
            }

            if (!pendingLoads.TryGetValue(location, out PendingLoad pending))
            {
                GameObjectPoolOptions value = options ?? GameObjectPoolOptions.Default;
                pending = new PendingLoad(value);
                pendingLoads.Add(location, pending);
                CompleteLoadAsync(location, pending).Forget();
            }
            else
            {
                ValidateOptions(location, pending.Options, options);
            }

            return await pending.Completion.Task.AttachExternalCancellation(cancellationToken);
        }

        private async UniTask CompleteLoadAsync(string location, PendingLoad pending)
        {
            IPrefabHandle handle = null;
            Exception loadException = null;
            try
            {
                handle = await prefabProvider.LoadAsync(location);
            }
            catch (Exception exception)
            {
                loadException = exception;
            }

            await UniTask.SwitchToMainThread();
            try
            {
                EnsureOwnerThread();
                if (loadException != null)
                {
                    pending.Completion.TrySetException(loadException);
                    return;
                }

                if (handle == null)
                {
                    throw new InvalidOperationException(
                        $"Prefab provider returned a null handle for '{location}'.");
                }

                if (disposed)
                {
                    throw new ObjectDisposedException(nameof(GameObjectPoolService));
                }

                var bucket =
                    new PoolBucket(this, location, handle, pending.Options);
                handle = null;
                buckets.Add(location, bucket);
                pending.Completion.TrySetResult(bucket);
            }
            catch (Exception exception)
            {
                handle?.Dispose();
                pending.Completion.TrySetException(exception);
            }
            finally
            {
                pendingLoads.Remove(location);
            }
        }

        private GameObject Spawn(PoolBucket bucket, Transform parent)
        {
            using ProfilerMarker.AutoScope _ = SpawnProfilerMarker.Auto();
            EnsureUsable();
            bucket.EnsureAvailable();
            int countAllBefore = bucket.Pool.CountAll;
            PooledInstanceMarker marker = bucket.Pool.Get();
            if (marker == null)
            {
                bucket.RegisterMissingInactiveInstance();
                throw new InvalidOperationException(
                    $"A pooled instance of '{bucket.Location}' was destroyed externally.");
            }
            bool createdSynchronously =
                bucket.Pool.CountAll > countAllBefore;

            marker.State = PooledInstanceState.Spawning;
            bucket.Active.Add(marker);

            Transform instanceTransform = marker.transform;
            instanceTransform.SetParent(parent, false);
            RestoreTransform(marker);
            instanceTransform.localRotation = marker.DefaultLocalRotation;
            instanceTransform.localScale = marker.DefaultLocalScale;
            marker.gameObject.SetActive(true);

            int spawnedCallbackCount = 0;
            try
            {
                InvokeSpawned(marker.Callbacks, ref spawnedCallbackCount);
                if (marker.State != PooledInstanceState.Spawning)
                {
                    throw new InvalidOperationException(
                        $"Pooled instance '{bucket.Location}' changed state during OnSpawned.");
                }

                marker.State = PooledInstanceState.Active;
                bucket.RegisterSpawn(createdSynchronously);
                return marker.gameObject;
            }
            catch
            {
                InvokeDespawnedSafely(marker.Callbacks, spawnedCallbackCount);
                marker.State = PooledInstanceState.Inactive;
                bucket.Active.Remove(marker);
                marker.gameObject.SetActive(false);
                instanceTransform.SetParent(bucket.StorageRoot, false);
                bucket.Pool.Release(marker);
                throw;
            }
        }

        private PooledInstanceMarker CreateInstance(PoolBucket bucket)
        {
            GameObject instance = bucket.Handle.Instantiate(bucket.StorageRoot);
            instance.SetActive(false);

            PooledInstanceMarker marker =
                instance.GetComponent<PooledInstanceMarker>() ??
                instance.AddComponent<PooledInstanceMarker>();
            if (marker.Owner != null && marker.Owner != this)
            {
                DestroyGameObject(instance);
                throw new InvalidOperationException(
                    $"Prefab '{bucket.Location}' is already owned by another pool.");
            }

            marker.Owner = this;
            marker.Location = bucket.Location;
            marker.State = PooledInstanceState.Inactive;
            Transform instanceTransform = instance.transform;
            RectTransform rectTransform = instanceTransform as RectTransform;
            marker.DefaultLocalPosition = instanceTransform.localPosition;
            marker.DefaultLocalRotation = instanceTransform.localRotation;
            marker.DefaultLocalScale = instanceTransform.localScale;
            marker.HasRectTransform = rectTransform != null;
            if (rectTransform != null)
            {
                marker.DefaultAnchorMin = rectTransform.anchorMin;
                marker.DefaultAnchorMax = rectTransform.anchorMax;
                marker.DefaultPivot = rectTransform.pivot;
                marker.DefaultSizeDelta = rectTransform.sizeDelta;
                marker.DefaultAnchoredPosition = rectTransform.anchoredPosition3D;
            }
            marker.Callbacks = CollectCallbacks(instance);
            return marker;
        }

        private static void RestoreTransform(PooledInstanceMarker marker)
        {
            Transform instanceTransform = marker.transform;
            if (marker.HasRectTransform && instanceTransform is RectTransform rectTransform)
            {
                rectTransform.anchorMin = marker.DefaultAnchorMin;
                rectTransform.anchorMax = marker.DefaultAnchorMax;
                rectTransform.pivot = marker.DefaultPivot;
                rectTransform.sizeDelta = marker.DefaultSizeDelta;
                rectTransform.anchoredPosition3D = marker.DefaultAnchoredPosition;
                return;
            }

            instanceTransform.localPosition = marker.DefaultLocalPosition;
        }

        private Transform GetGroupRoot(PoolGroup group)
        {
            if (groupRoots.TryGetValue(group, out Transform groupRoot) &&
                groupRoot != null)
            {
                return groupRoot;
            }

            var groupObject = new GameObject($"[{group}]");
            groupRoot = groupObject.transform;
            groupRoot.SetParent(poolRoot, false);
            groupRoots[group] = groupRoot;
            return groupRoot;
        }

        private static IPoolable[] CollectCallbacks(GameObject instance)
        {
            IPoolable[] callbacks =
                instance.GetComponentsInChildren<IPoolable>(true);
            return callbacks.Length == 0
                ? Array.Empty<IPoolable>()
                : callbacks;
        }

        private T GetRequiredComponent<T>(GameObject instance, string location)
            where T : Component
        {
            T component = instance.GetComponent<T>();
            if (component != null)
            {
                return component;
            }

            DespawnImmediate(instance);
            throw new InvalidOperationException(
                $"Pooled prefab '{location}' does not contain component {typeof(T).FullName}.");
        }

        private void InvokeSpawned(IPoolable[] callbacks, ref int completedCount)
        {
            lifecycleCallbackDepth++;
            try
            {
                for (int i = 0; i < callbacks.Length; i++)
                {
                    completedCount++;
                    callbacks[i].OnSpawned();
                }
            }
            finally
            {
                lifecycleCallbackDepth--;
            }
        }

        private void InvokeDespawnedSafely(IPoolable[] callbacks, int count)
        {
            lifecycleCallbackDepth++;
            try
            {
                int lastIndex = Math.Min(count, callbacks.Length) - 1;
                for (int i = lastIndex; i >= 0; i--)
                {
                    try
                    {
                        callbacks[i].OnDespawned();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                    }
                }
            }
            finally
            {
                lifecycleCallbackDepth--;
            }
        }

        private static void ValidateOptions(
            string location,
            GameObjectPoolOptions current,
            GameObjectPoolOptions requested)
        {
            if (requested == null)
            {
                return;
            }

            GameObjectPoolOptions value = requested;
            if (current.InitialCapacity != value.InitialCapacity ||
                current.MaxSize != value.MaxSize ||
                current.PrewarmPerFrame != value.PrewarmPerFrame ||
                current.CollectionCheck != value.CollectionCheck ||
                current.Group != value.Group)
            {
                throw new InvalidOperationException(
                    $"Pool '{location}' already exists with different options.");
            }
        }

        private static void ValidateLocation(string location)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                throw new ArgumentException(
                    "Prefab location cannot be empty.",
                    nameof(location));
            }
        }

        private void EnsureUsable()
        {
            EnsureOwnerThread();
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(GameObjectPoolService));
            }
        }

        private void EnsureNoLifecycleMutation()
        {
            if (lifecycleCallbackDepth > 0)
            {
                throw new InvalidOperationException(
                    "This pool operation cannot run from an IPoolable lifecycle callback.");
            }
        }

        private void EnsureOwnerThread()
        {
            if (!UIFrame.UIFrameSafety.ThreadChecks)
            {
                return;
            }

            if (Thread.CurrentThread.ManagedThreadId != ownerThreadId)
            {
                throw new InvalidOperationException(
                    "GameObjectPoolService can only be used from its owner Unity thread.");
            }
        }

        private static void DestroyGameObject(GameObject instance)
        {
            if (instance == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(instance);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static void DestroyPooledInstance(PooledInstanceMarker marker)
        {
            if (marker == null)
            {
                return;
            }

            marker.State = PooledInstanceState.Destroyed;
            DestroyGameObject(marker.gameObject);
        }

        internal void NotifyInstanceDestroyed(PooledInstanceMarker marker)
        {
            if (disposed || marker == null || marker.State == PooledInstanceState.Destroyed)
            {
                return;
            }

            PooledInstanceState previousState = marker.State;
            marker.State = PooledInstanceState.Destroyed;
            if (buckets.TryGetValue(marker.Location, out PoolBucket bucket))
            {
                bucket.Active.Remove(marker);
                bucket.RegisterExternalDestroy(previousState);
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.LogError(
                $"Pooled instance '{marker.Location}' was destroyed externally while in state " +
                $"{previousState}. Use DespawnImmediate or dispose the owning pool instead.");
#endif
        }

        private sealed class PendingLoad
        {
            public GameObjectPoolOptions Options { get; }
            public UniTaskCompletionSource<PoolBucket> Completion { get; } = new();

            public PendingLoad(GameObjectPoolOptions options)
            {
                Options = options;
            }
        }

        private sealed class PoolBucket
        {
            public string Location { get; }
            public IPrefabHandle Handle { get; }
            public GameObjectPoolOptions Options { get; }
            public Transform StorageRoot { get; }
            public ManagedObjectPool<PooledInstanceMarker> Pool { get; }
            public HashSet<PooledInstanceMarker> Active { get; } = new();
            public int PeakActive { get; private set; }
            public int SynchronousExpansionCount { get; private set; }
            public bool IsPrewarming => prewarmOperationCount > 0;
            private int externallyDestroyedCount;
            private int externallyDestroyedInactiveCount;
            private int prewarmOperationCount;
            private bool corrupted;
            private bool disposed;

            public PoolBucket(
                GameObjectPoolService owner,
                string location,
                IPrefabHandle handle,
                GameObjectPoolOptions options)
            {
                Location = location;
                Handle = handle;
                Options = options;
                StorageRoot = owner.GetGroupRoot(options.Group);
                Pool = new ManagedObjectPool<PooledInstanceMarker>(
                    () => owner.CreateInstance(this),
                    onDestroy: DestroyPooledInstance,
                    options: options.ToManagedOptions());
            }

            public void RegisterSpawn(bool createdSynchronously)
            {
                if (createdSynchronously)
                {
                    SynchronousExpansionCount++;
                }

                if (Active.Count > PeakActive)
                {
                    PeakActive = Active.Count;
                }
            }

            public void BeginPrewarm()
            {
                EnsureAvailable();
                prewarmOperationCount++;
            }

            public void EndPrewarm()
            {
                if (prewarmOperationCount > 0)
                {
                    prewarmOperationCount--;
                }
            }

            public void ReturnPrewarmed(PooledInstanceMarker marker)
            {
                if (marker == null)
                {
                    return;
                }

                if (disposed)
                {
                    DestroyPooledInstance(marker);
                    return;
                }

                try
                {
                    Pool.Release(marker);
                }
                catch (Exception exception)
                {
                    DestroyPooledInstance(marker);
                    Debug.LogException(exception);
                }
            }

            public void EnsureAvailable()
            {
                if (disposed)
                {
                    throw new ObjectDisposedException($"Pool '{Location}'");
                }

                if (corrupted)
                {
                    throw new InvalidOperationException(
                        $"Pool '{Location}' is corrupted because a pooled instance was destroyed " +
                        "externally. Remove the pool and prepare it again before reuse.");
                }
            }

            public void RegisterExternalDestroy(PooledInstanceState previousState)
            {
                if (corrupted)
                {
                    return;
                }

                corrupted = true;
                externallyDestroyedCount++;
                if (previousState == PooledInstanceState.Inactive)
                {
                    externallyDestroyedInactiveCount++;
                }
            }

            public void RegisterMissingInactiveInstance()
            {
                if (corrupted)
                {
                    return;
                }

                corrupted = true;
                externallyDestroyedCount++;
                externallyDestroyedInactiveCount++;
            }

            public PoolStats GetStats()
            {
                PoolStats raw = Pool.Stats;
                int countInactive =
                    Math.Max(0, raw.CountInactive - externallyDestroyedInactiveCount);
                int countActive = Active.Count;
                return new PoolStats(
                    countActive + countInactive,
                    countActive,
                    countInactive,
                    raw.TotalCreated,
                    raw.TotalDestroyed + externallyDestroyedCount);
            }

            public void Dispose(bool force)
            {
                if (disposed)
                {
                    return;
                }

                if (!force && IsPrewarming)
                {
                    throw new InvalidOperationException(
                        $"Pool '{Location}' cannot be disposed while prewarming.");
                }

                disposed = true;
                if (force)
                {
                    foreach (PooledInstanceMarker marker in Active)
                    {
                        if (marker != null)
                        {
                            DestroyPooledInstance(marker);
                        }
                    }

                    Active.Clear();
                }

                try
                {
                    Pool.Dispose();
                }
                finally
                {
                    Handle.Dispose();
                }
            }
        }
    }

    [DisallowMultipleComponent]
    internal sealed class PooledInstanceMarker : MonoBehaviour
    {
        internal GameObjectPoolService Owner;
        internal string Location;
        internal PooledInstanceState State = PooledInstanceState.Inactive;
        internal Vector3 DefaultLocalPosition;
        internal Quaternion DefaultLocalRotation;
        internal Vector3 DefaultLocalScale;
        internal bool HasRectTransform;
        internal Vector2 DefaultAnchorMin;
        internal Vector2 DefaultAnchorMax;
        internal Vector2 DefaultPivot;
        internal Vector2 DefaultSizeDelta;
        internal Vector3 DefaultAnchoredPosition;
        internal IPoolable[] Callbacks = Array.Empty<IPoolable>();

        private void OnDestroy()
        {
            Owner?.NotifyInstanceDestroyed(this);
        }
    }

    internal enum PooledInstanceState
    {
        Inactive = 0,
        Spawning,
        Active,
        PendingDespawn,
        Despawning,
        Destroyed
    }
}
