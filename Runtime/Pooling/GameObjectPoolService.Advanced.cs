using System;
using System.Collections.Generic;
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
        private bool processingPendingDespawns;

        public bool DespawnDeferred(GameObject instance)
        {
            EnsureUsable();
            if (!TryGetActiveMarker(
                    instance,
                    out PooledInstanceMarker marker,
                    out _) ||
                marker.State == PooledInstanceState.PendingDespawn)
            {
                return false;
            }

            QueueDespawn(marker);
            return true;
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

            for (int bucketIndex = 0; bucketIndex < groupedBuckets.Count; bucketIndex++)
            {
                PoolBucket bucket = groupedBuckets[bucketIndex];
                var active = new List<PooledInstanceMarker>(bucket.Active);
                for (int i = 0; i < active.Count; i++)
                {
                    PooledInstanceMarker marker = active[i];
                    if (marker == null ||
                        (marker.State != PooledInstanceState.Active &&
                         marker.State != PooledInstanceState.PendingDespawn))
                    {
                        continue;
                    }

                    if (!deferred)
                    {
                        DespawnNow(marker, bucket);
                    }
                    else if (marker.State != PooledInstanceState.PendingDespawn)
                    {
                        QueueDespawn(marker);
                    }
                    else
                    {
                        continue;
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
            if (before <= targetInactive)
            {
                return 0;
            }

            if (targetInactive == 0)
            {
                bucket.Pool.Clear();
                return before;
            }

            var retained = new List<PooledInstanceMarker>(targetInactive);
            for (int i = 0; i < targetInactive; i++)
            {
                retained.Add(bucket.Pool.Get());
            }

            bucket.Pool.Clear();
            for (int i = 0; i < retained.Count; i++)
            {
                bucket.Pool.Release(retained[i]);
            }

            return before - targetInactive;
        }

        public bool TryRemoveGroup(PoolGroup group, bool force = false)
        {
            EnsureUsable();
            if (lifecycleCallbackDepth > 0)
            {
                return false;
            }

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
                    for (int i = 0; i < count; i++)
                    {
                        PooledInstanceMarker marker = pendingDespawns[i];
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
                            Debug.LogException(exception);
                        }
                    }

                    pendingDespawns.RemoveRange(0, count);
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
