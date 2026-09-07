using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Game.Pooling
{
    /// <summary>
    /// GameObject 池的分组、收缩和延迟回收等进阶能力。
    /// </summary>
    public sealed partial class GameObjectPoolService
    {
        private readonly List<PooledInstanceMarker> pendingDespawns = new();
        private readonly List<PooledInstanceMarker> pendingDespawnBatch = new();
        private bool processingPendingDespawns;

        public void DespawnDeferred(GameObject instance)
        {
            EnsureUsable();
            EnsureNoLifecycleMutation();
            RequireMarker(instance, out PooledInstanceMarker marker, out _);
            if (marker.State == PooledInstanceState.PendingDespawn)
            {
                throw new InvalidOperationException(
                    $"'{marker.Location}' is already waiting for deferred despawn.");
            }

            if (marker.State != PooledInstanceState.Active)
            {
                throw new InvalidOperationException(
                    $"Cannot despawn '{marker.Location}' in state {marker.State}.");
            }

            QueueDespawn(marker);
        }

        public int DespawnGroup(PoolGroup group, bool deferred = false)
        {
            EnsureUsable();
            EnsureNoLifecycleMutation();
            int count = 0;
            var groupedBuckets = new List<PoolBucket>();

            foreach (PoolBucket bucket in buckets.Values)
            {
                if (bucket.Options.Group == group && bucket.Active.Count > 0)
                {
                    groupedBuckets.Add(bucket);
                }
            }

            if (deferred)
            {
                for (int bucketIndex = 0; bucketIndex < groupedBuckets.Count; bucketIndex++)
                {
                    PoolBucket bucket = groupedBuckets[bucketIndex];
                    foreach (PooledInstanceMarker marker in bucket.Active)
                    {
                        if (marker == null)
                        {
                            bucket.FailExternalDestroy();
                            throw new InvalidOperationException(
                                $"A pooled instance of '{bucket.Location}' was destroyed externally.");
                        }

                        if (marker.State == PooledInstanceState.PendingDespawn)
                        {
                            throw new InvalidOperationException(
                                $"'{marker.Location}' is already waiting for deferred despawn.");
                        }

                        if (marker.State != PooledInstanceState.Active)
                        {
                            throw new InvalidOperationException(
                                $"Cannot despawn '{bucket.Location}' in state {marker.State}.");
                        }
                    }
                }
            }

            for (int bucketIndex = 0; bucketIndex < groupedBuckets.Count; bucketIndex++)
            {
                PoolBucket bucket = groupedBuckets[bucketIndex];
                var active = new List<PooledInstanceMarker>(bucket.Active);
                for (int i = 0; i < active.Count; i++)
                {
                    PooledInstanceMarker marker = active[i];
                    if (marker == null)
                    {
                        bucket.FailExternalDestroy();
                        throw new InvalidOperationException(
                            $"A pooled instance of '{bucket.Location}' was destroyed externally.");
                    }

                    if (deferred)
                    {
                        QueueDespawn(marker);
                    }
                    else
                    {
                        DespawnNow(marker, bucket);
                    }

                    count++;
                }
            }

            return count;
        }

        public int Trim(string location, int targetInactive)
        {
            EnsureUsable();
            EnsureNoLifecycleMutation();
            ValidateLocation(location);

            if (targetInactive < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetInactive));
            }

            if (!buckets.TryGetValue(location, out PoolBucket bucket))
            {
                return 0;
            }

            bucket.EnsureAvailable();
            int before = bucket.Pool.CountInactive;
            if (before == 0)
            {
                return 0;
            }

            var inactive = new List<PooledInstanceMarker>(before);
            for (int i = 0; i < before; i++)
            {
                PooledInstanceMarker marker = bucket.Pool.Get();
                if (marker == null)
                {
                    bucket.FailExternalDestroy();
                    for (int j = 0; j < inactive.Count; j++)
                    {
                        bucket.Pool.Release(inactive[j]);
                    }

                    throw new InvalidOperationException(
                        $"An inactive pooled instance of '{location}' was destroyed externally.");
                }

                inactive.Add(marker);
            }

            int retainedCount = Math.Min(before, targetInactive);
            for (int i = retainedCount; i < inactive.Count; i++)
            {
                bucket.Pool.Release(inactive[i]);
            }

            bucket.Pool.Clear();
            for (int i = 0; i < retainedCount; i++)
            {
                bucket.Pool.Release(inactive[i]);
            }

            return before - retainedCount;
        }

        public bool TryRemoveGroup(PoolGroup group, bool force = false)
        {
            EnsureUsable();
            EnsureNoLifecycleMutation();

            foreach (PendingLoad pending in pendingLoads.Values)
            {
                if (pending.Options.Group == group)
                {
                    return false;
                }
            }

            foreach (PoolBucket bucket in buckets.Values)
            {
                if (bucket.Options.Group == group && bucket.IsPrewarming)
                {
                    return false;
                }
            }

            if (!force)
            {
                foreach (PoolBucket bucket in buckets.Values)
                {
                    if (bucket.Options.Group == group && bucket.Active.Count > 0)
                    {
                        return false;
                    }
                }
            }

            var locations = new List<string>();
            foreach (KeyValuePair<string, PoolBucket> pair in buckets)
            {
                if (pair.Value.Options.Group == group)
                {
                    pair.Value.EnsureAvailable();
                    locations.Add(pair.Key);
                }
            }

            for (int i = 0; i < locations.Count; i++)
            {
                string location = locations[i];
                PoolBucket bucket = buckets[location];
                buckets.Remove(location);
                bucket.Dispose(force);
            }

            return true;
        }

        private void QueueDespawn(PooledInstanceMarker marker)
        {
            marker.State = PooledInstanceState.PendingDespawn;
            pendingDespawns.Add(marker);
            if (processingPendingDespawns)
            {
                return;
            }

            processingPendingDespawns = true;
            ProcessPendingDespawnsAsync().Forget();
        }

        private async UniTask ProcessPendingDespawnsAsync()
        {
            try
            {
                while (pendingDespawns.Count > 0)
                {
                    await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
                    int count = pendingDespawns.Count;
                    pendingDespawnBatch.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        pendingDespawnBatch.Add(pendingDespawns[i]);
                    }

                    pendingDespawns.RemoveRange(0, count);

                    Exception firstError = null;
                    for (int i = 0; i < pendingDespawnBatch.Count; i++)
                    {
                        PooledInstanceMarker marker = pendingDespawnBatch[i];
                        if (marker == null ||
                            marker.State != PooledInstanceState.PendingDespawn ||
                            marker.Owner != this ||
                            !buckets.TryGetValue(marker.Location, out PoolBucket bucket))
                        {
                            continue;
                        }

                        try
                        {
                            DespawnNow(marker, bucket);
                        }
                        catch (Exception exception)
                        {
                            firstError ??= exception;
                        }
                    }

                    pendingDespawnBatch.Clear();
                    if (firstError != null)
                    {
                        ExceptionDispatchInfo.Capture(firstError).Throw();
                    }
                }
            }
            finally
            {
                processingPendingDespawns = false;
                if (!disposed && pendingDespawns.Count > 0)
                {
                    processingPendingDespawns = true;
                    ProcessPendingDespawnsAsync().Forget();
                }
            }
        }
    }
}
