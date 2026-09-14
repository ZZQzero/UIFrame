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

            foreach (PoolBucket bucket in buckets.Values)
            {
                if (bucket.Options.Group != group || bucket.Active.Count == 0)
                {
                    continue;
                }

                var active = new List<PooledInstanceMarker>(bucket.Active);
                for (int i = 0; i < active.Count; i++)
                {
                    PooledInstanceMarker marker = active[i];
                    if (marker == null)
                    {
                        continue;
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
            int trimmed = 0;
            while (bucket.InactiveCount > targetInactive)
            {
                DestroyPooledInstance(bucket.TryPopInactive());
                trimmed++;
            }

            return trimmed;
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

            var locations = new List<string>();
            foreach (KeyValuePair<string, PoolBucket> pair in buckets)
            {
                PoolBucket bucket = pair.Value;
                if (bucket.Options.Group != group)
                {
                    continue;
                }

                if (bucket.IsPrewarming ||
                    (!force && bucket.Active.Count > 0))
                {
                    return false;
                }

                locations.Add(pair.Key);
            }

            for (int i = 0; i < locations.Count; i++)
            {
                buckets[locations[i]].EnsureAvailable();
            }

            for (int i = 0; i < locations.Count; i++)
            {
                string location = locations[i];
                PoolBucket bucket = buckets[location];
                buckets.Remove(location);
                DropPendingDespawns(location);
                bucket.Dispose(force);
            }

            return true;
        }

        private void QueueDespawn(PooledInstanceMarker marker)
        {
            if (marker.State == PooledInstanceState.PendingDespawn)
            {
                return;
            }

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

                    for (int i = 0; i < pendingDespawnBatch.Count; i++)
                    {
                        PooledInstanceMarker marker = pendingDespawnBatch[i];
                        if (marker == null ||
                            marker.State != PooledInstanceState.PendingDespawn)
                        {
                            continue;
                        }

                        if (!buckets.TryGetValue(marker.Location, out PoolBucket bucket))
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

                    pendingDespawnBatch.Clear();
                }
            }
            finally
            {
                processingPendingDespawns = false;
            }
        }
    }
}
