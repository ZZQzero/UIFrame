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
        private static readonly ProfilerMarker SpawnProfilerMarker = new("Game.Pooling.Spawn");
        private static readonly ProfilerMarker DespawnProfilerMarker = new("Game.Pooling.Despawn");
        private readonly Transform poolRoot;
        private readonly bool ownsPoolRoot;
        private readonly int ownerThreadId;
        private readonly Dictionary<string, PoolBucket> buckets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingLoad> pendingLoads = new(StringComparer.Ordinal);
        private readonly Dictionary<PoolGroup, Transform> groupRoots = new();

        private bool disposed;
        private int lifecycleCallbackDepth;

        public GameObjectPoolService(ResourcePackage package, Transform poolRoot = null)
            : this(new YooAssetPrefabProvider(package), poolRoot)
        {
        }

        public GameObjectPoolService(IPrefabProvider prefabProvider, Transform poolRoot = null)
        {
            this.prefabProvider = prefabProvider ?? throw new ArgumentNullException(nameof(prefabProvider));
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
            if (targetCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetCount));
            }

            PoolBucket bucket = await GetOrCreateBucketAsync(location, options, cancellationToken);
            int cappedTarget = Math.Min(targetCount, bucket.Options.MaxSize);
            if (cappedTarget == 0 || bucket.CountAll >= cappedTarget)
            {
                return;
            }

            int createdThisFrame = 0;
            bucket.BeginPrewarm();
            try
            {
                while (bucket.CountAll < cappedTarget)
                {
                    bucket.PushInactive(CreateInstance(bucket));
                    createdThisFrame++;
                    if (createdThisFrame < bucket.Options.PrewarmPerFrame ||
                        bucket.CountAll >= cappedTarget)
                    {
                        continue;
                    }

                    createdThisFrame = 0;
                    await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
                    EnsureUsable();
                    bucket.EnsureAvailable();
                }
            }
            finally
            {
                bucket.EndPrewarm();
            }
        }

        public async UniTask<GameObject> SpawnAsync(
            string location,
            Transform parent = null,
            GameObjectPoolOptions options = null,
            CancellationToken cancellationToken = default)
        {
            PoolBucket bucket = await GetOrCreateBucketAsync(location, options, cancellationToken);
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

            component = GetRequiredComponent<T>(instance, location);
            return true;
        }

        public GameObject SpawnLoaded(string location, Transform parent = null)
        {
            if (TrySpawn(location, parent, out GameObject instance))
            {
                return instance;
            }

            throw new InvalidOperationException($"Pool '{location}' is not prepared. Call PrepareAsync or PrewarmAsync first.");
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
            GameObject instance = await SpawnAsync(location, parent, options, cancellationToken);
            return GetRequiredComponent<T>(instance, location);
        }

        public void Despawn(GameObject instance)
        {
            DespawnImmediate(instance);
        }

        public void DespawnImmediate(GameObject instance)
        {
            EnsureUsable();
            EnsureNoLifecycleMutation();
            RequireMarker(instance, out PooledInstanceMarker marker, out PoolBucket bucket);
            if (marker.State != PooledInstanceState.Active &&
                marker.State != PooledInstanceState.PendingDespawn)
            {
                throw new InvalidOperationException($"Cannot despawn '{marker.Location}' in state {marker.State}.");
            }

            DespawnNow(marker, bucket);
        }

        private void DespawnNow(PooledInstanceMarker marker, PoolBucket bucket)
        {
            using ProfilerMarker.AutoScope _ = DespawnProfilerMarker.Auto();
            GameObject instance = marker.gameObject;
            if (instance == null)
            {
                throw new InvalidOperationException(
                    $"A pooled instance of '{bucket.Location}' was destroyed externally.");
            }

            InvokeDespawned(marker.Callbacks);
            ReturnInactive(marker, bucket);
            bucket.Active.Remove(marker);
        }

        private void RequireMarker(
            GameObject instance,
            out PooledInstanceMarker marker,
            out PoolBucket bucket)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            if (!instance.TryGetComponent(out marker) || marker.Owner != this)
            {
                throw new InvalidOperationException("GameObject is not an instance of this pool.");
            }

            if (!buckets.TryGetValue(marker.Location, out bucket))
            {
                throw new InvalidOperationException($"Pool '{marker.Location}' was removed.");
            }
        }

        public bool TryRemovePool(string location)
        {
            EnsureUsable();
            EnsureNoLifecycleMutation();
            ValidateLocation(location);

            if (pendingLoads.ContainsKey(location))
            {
                return false;
            }

            if (!buckets.TryGetValue(location, out PoolBucket bucket))
            {
                return false;
            }

            bucket.EnsureAvailable();
            if (bucket.Active.Count > 0 || bucket.IsPrewarming)
            {
                return false;
            }

            buckets.Remove(location);
            DropPendingDespawns(location);
            bucket.Dispose(false);
            return true;
        }

        public void Dispose()
        {
            DisposeInternal(true);
        }

        public bool TryDispose()
        {
            EnsureOwnerThread();
            EnsureNoLifecycleMutation();
            if (disposed)
            {
                return true;
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
                bucket.Dispose(force);
            }

            buckets.Clear();
            pendingDespawns.Clear();
            processingPendingDespawns = false;

            if (ownsPoolRoot)
            {
                if (poolRoot != null)
                {
                    DestroyGameObject(poolRoot.gameObject);
                }
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
            EnsureNoLifecycleMutation();
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
                pending = new PendingLoad(options ?? GameObjectPoolOptions.Default);
                pendingLoads.Add(location, pending);
                PublishLoadAsync(location, pending).Forget();
            }
            else
            {
                ValidateOptions(location, pending.Options, options);
            }

            return await pending.Completion.Task.AttachExternalCancellation(cancellationToken);
        }

        private async UniTaskVoid PublishLoadAsync(string location, PendingLoad pending)
        {
            try
            {
                pending.Completion.TrySetResult(await LoadBucketAsync(location, pending));
            }
            catch (Exception exception)
            {
                pending.Completion.TrySetException(exception);
            }
        }

        private async UniTask<PoolBucket> LoadBucketAsync(
            string location,
            PendingLoad pending)
        {
            IPrefabHandle handle = null;
            try
            {
                handle = await prefabProvider.LoadAsync(location);
            }
            finally
            {
                await UniTask.SwitchToMainThread();
                pendingLoads.Remove(location);
            }

            if (handle == null)
            {
                throw new InvalidOperationException(
                    $"Prefab provider returned null handle for '{location}'.");
            }

            if (disposed)
            {
                handle.Dispose();
                throw new ObjectDisposedException(nameof(GameObjectPoolService));
            }

            try
            {
                var bucket = new PoolBucket(this, location, handle, pending.Options);
                buckets.Add(location, bucket);
                return bucket;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private GameObject Spawn(PoolBucket bucket, Transform parent)
        {
            using ProfilerMarker.AutoScope _ = SpawnProfilerMarker.Auto();
            EnsureUsable();
            EnsureNoLifecycleMutation();
            bucket.EnsureAvailable();
            PooledInstanceMarker marker = bucket.TryPopInactive() ?? CreateInstance(bucket);
            bucket.Active.Add(marker);

            Transform instanceTransform = marker.transform;
            instanceTransform.SetParent(parent, false);
            RestoreTransform(marker);
            instanceTransform.localRotation = marker.DefaultLocalRotation;
            instanceTransform.localScale = marker.DefaultLocalScale;
            marker.gameObject.SetActive(true);
            marker.State = PooledInstanceState.Active;
            InvokeSpawned(marker.Callbacks);
            return marker.gameObject;
        }

        private void ReturnInactive(
            PooledInstanceMarker marker,
            PoolBucket bucket)
        {
            GameObject instance = marker.gameObject;
            if (instance == null)
            {
                throw new InvalidOperationException(
                    $"A pooled instance of '{bucket.Location}' was destroyed externally.");
            }

            if (bucket.StorageRoot == null)
            {
                throw new InvalidOperationException(
                    $"Pool '{bucket.Location}' storage root was destroyed externally.");
            }

            instance.SetActive(false);
            instance.transform.SetParent(bucket.StorageRoot, false);
            marker.State = PooledInstanceState.Inactive;
            if (bucket.InactiveCount >= bucket.Options.MaxSize)
            {
                DestroyPooledInstance(marker);
                return;
            }

            bucket.PushInactive(marker);
        }

        private PooledInstanceMarker CreateInstance(PoolBucket bucket)
        {
            GameObject instance = bucket.Handle.Instantiate(bucket.StorageRoot);
            PooledInstanceMarker marker =
                instance.GetComponent<PooledInstanceMarker>() ??
                instance.AddComponent<PooledInstanceMarker>();
            if (marker.Owner != null && marker.Owner != this)
            {
                DestroyGameObject(instance);
                throw new InvalidOperationException($"Prefab '{bucket.Location}' is already owned by another pool.");
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
            instance.SetActive(false);
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
            if (poolRoot == null)
            {
                throw new InvalidOperationException("GameObjectPoolService root was destroyed externally.");
            }

            if (groupRoots.TryGetValue(group, out Transform groupRoot))
            {
                if (groupRoot == null)
                {
                    throw new InvalidOperationException($"Pool group root [{group}] was destroyed externally.");
                }

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
            IPoolable[] callbacks = instance.GetComponentsInChildren<IPoolable>(true);
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

            throw new InvalidOperationException($"Pooled prefab '{location}' does not contain component {typeof(T).FullName}.");
        }

        private void InvokeSpawned(IPoolable[] callbacks)
        {
            lifecycleCallbackDepth++;
            try
            {
                for (int i = 0; i < callbacks.Length; i++)
                {
                    callbacks[i].OnSpawned();
                }
            }
            finally
            {
                lifecycleCallbackDepth--;
            }
        }

        private void InvokeDespawned(IPoolable[] callbacks)
        {
            lifecycleCallbackDepth++;
            try
            {
                int lastIndex = callbacks.Length - 1;
                for (int i = lastIndex; i >= 0; i--)
                {
                    callbacks[i].OnDespawned();
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
                throw new InvalidOperationException($"Pool '{location}' already exists with different options.");
            }
        }

        private static void ValidateLocation(string location)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                throw new ArgumentException("Prefab location cannot be empty.", nameof(location));
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
                throw new InvalidOperationException("This pool operation cannot run from an IPoolable lifecycle callback.");
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
                throw new InvalidOperationException("GameObjectPoolService can only be used from its owner Unity thread.");
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

            marker.Owner = null;
            DestroyGameObject(marker.gameObject);
        }

        internal void NotifyInstanceDestroyed(PooledInstanceMarker marker)
        {
            if (disposed || marker == null || marker.Owner != this)
            {
                return;
            }

            marker.Owner = null;
            if (buckets.TryGetValue(marker.Location, out PoolBucket bucket))
            {
                bucket.Active.Remove(marker);
                bucket.RemoveInactive(marker);
            }

            pendingDespawns.Remove(marker);
            Debug.LogError(
                $"Pooled instance '{marker.Location}' was destroyed externally. " +
                "Use DespawnImmediate or dispose the owning pool instead.");
        }

        private void DropPendingDespawns(string location)
        {
            for (int i = pendingDespawns.Count - 1; i >= 0; i--)
            {
                PooledInstanceMarker marker = pendingDespawns[i];
                if (marker == null || marker.Location == location)
                {
                    pendingDespawns.RemoveAt(i);
                }
            }
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
            public HashSet<PooledInstanceMarker> Active { get; } = new();
            public int InactiveCount => inactive.Count;
            public int CountAll => Active.Count + inactive.Count;
            public bool IsPrewarming => prewarmOperationCount > 0;

            private readonly List<PooledInstanceMarker> inactive;
            private int prewarmOperationCount;
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
                inactive = new List<PooledInstanceMarker>(options.InitialCapacity);
            }

            public PooledInstanceMarker TryPopInactive()
            {
                if (inactive.Count == 0)
                {
                    return null;
                }

                int lastIndex = inactive.Count - 1;
                PooledInstanceMarker marker = inactive[lastIndex];
                inactive.RemoveAt(lastIndex);
                if (marker == null)
                {
                    throw new InvalidOperationException(
                        $"A pooled instance of '{Location}' was destroyed externally.");
                }

                return marker;
            }

            public void PushInactive(PooledInstanceMarker marker)
            {
                inactive.Add(marker);
            }

            public void RemoveInactive(PooledInstanceMarker marker)
            {
                inactive.Remove(marker);
            }

            public void BeginPrewarm()
            {
                EnsureAvailable();
                prewarmOperationCount++;
            }

            public void EndPrewarm()
            {
                if (prewarmOperationCount <= 0)
                {
                    throw new InvalidOperationException($"Pool '{Location}' prewarm counter underflow.");
                }

                prewarmOperationCount--;
            }

            public void EnsureAvailable()
            {
                if (disposed)
                {
                    throw new ObjectDisposedException($"Pool '{Location}'");
                }

                if (StorageRoot == null)
                {
                    throw new InvalidOperationException($"Pool '{Location}' storage root was destroyed externally.");
                }
            }

            public void Dispose(bool force)
            {
                if (disposed)
                {
                    return;
                }

                if (!force && IsPrewarming)
                {
                    throw new InvalidOperationException($"Pool '{Location}' cannot be disposed while prewarming.");
                }

                disposed = true;
                if (force)
                {
                    foreach (PooledInstanceMarker marker in Active)
                    {
                        DestroyPooledInstance(marker);
                    }

                    Active.Clear();
                }

                for (int i = 0; i < inactive.Count; i++)
                {
                    DestroyPooledInstance(inactive[i]);
                }

                inactive.Clear();
                Handle.Dispose();
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
        Active,
        PendingDespawn
    }
}
