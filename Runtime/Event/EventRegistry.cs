using System;

namespace Game
{
    /// <summary>
    /// 事件类型分桶注册表。Post 的 Drain 扫注册桶；与 Register 通过快照避免交叉读写。
    /// </summary>
    static class EventRegistry
    {
        internal const int MaxDrainPerFrame = 1024;
        internal const int MaxDrainPerType = 256;

        static readonly object RegistryLock = new();

        static IEventBucket[] buckets = new IEventBucket[16];
        static int bucketCount;

        internal static IEventBucket GetBucket(int typeId)
        {
            lock (RegistryLock)
            {
                return (uint)typeId < (uint)bucketCount ? buckets[typeId] : null;
            }
        }

        internal static int Register(IEventBucket bucket)
        {
            lock (RegistryLock)
            {
                if (bucketCount == buckets.Length)
                {
                    Array.Resize(ref buckets, buckets.Length * 2);
                }

                int id = bucketCount;
                buckets[id] = bucket;
                bucketCount++;
                return id;
            }
        }

        internal static void ThrowIfPublishing()
        {
            Snapshot(out IEventBucket[] items, out int count);
            for (int i = 0; i < count; i++)
            {
                IEventBucket bucket = items[i];
                if (!bucket.IsPublishing)
                {
                    continue;
                }

                throw new EventSystemException(
                    $"[EventSystem] 正在派发 {bucket.TypeName}，不能 ClearAll。");
            }
        }

        internal static void ClearAllBuckets()
        {
            Snapshot(out IEventBucket[] items, out int count);
            for (int i = 0; i < count; i++)
            {
                items[i].Clear();
            }
        }

        internal static int DrainPosted(int maxCount)
        {
            if (maxCount <= 0)
            {
                return 0;
            }

            Snapshot(out IEventBucket[] items, out int count);
            int remaining = maxCount;
            for (int i = 0; i < count && remaining > 0; i++)
            {
                IEventBucket bucket = items[i];
                int perType = remaining < MaxDrainPerType ? remaining : MaxDrainPerType;
                remaining -= bucket.DrainPosted(perType);
            }

            return maxCount - remaining;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        internal static void DumpListenerCounts()
        {
            Snapshot(out IEventBucket[] items, out int count);
            for (int i = 0; i < count; i++)
            {
                IEventBucket bucket = items[i];
                UnityEngine.Debug.Log($"[EventSystem] {bucket.TypeName}: {bucket.ListenerCount}");
            }
        }
#endif

        static void Snapshot(out IEventBucket[] items, out int count)
        {
            lock (RegistryLock)
            {
                items = buckets;
                count = bucketCount;
            }
        }
    }
}
