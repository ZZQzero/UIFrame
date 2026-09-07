using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 单一事件类型的槽位表。Publish 按 iterate 快照遍历；发布中退订只标无效，结束后 compact。
    /// </summary>
    sealed class EventBucket<T> : IEventBucket
    {
        internal static readonly EventBucket<T> Instance = new();

        struct Slot
        {
            public Action<T> Handler;
            public object Owner;
            public int Generation;
            public bool Active;
        }

        readonly object initLock = new();
        readonly ConcurrentQueue<T> postQueue = new();

        Slot[] slots;
        int[] iterate;
        int[] free;
        int[] pendingFree;
        int slotCount;
        int iterateCount;
        int freeCount;
        int pendingFreeCount;
        int activeCount;
        int typeId = -1;
        bool publishing;

        public string TypeName => typeof(T).FullName ?? typeof(T).Name;

        public int ListenerCount => activeCount;

        public bool IsPublishing => publishing;

        public EventHandle Subscribe(Action<T> handler, object owner)
        {
            EnsureRegistered();
            int slot = AllocSlot();
            ref Slot s = ref slots[slot];
            if (s.Generation == 0)
            {
                s.Generation = 1;
            }

            s.Handler = handler;
            s.Owner = owner;
            s.Active = true;
            activeCount++;

            if (iterateCount == iterate.Length)
            {
                Array.Resize(ref iterate, iterate.Length * 2);
            }

            iterate[iterateCount++] = slot;
            return new EventHandle(typeId, slot, s.Generation);
        }

        public bool Unsubscribe(int slot, int generation, bool updateOwner = true)
        {
            if (slots == null || (uint)slot >= (uint)slotCount)
            {
                return false;
            }

            ref Slot s = ref slots[slot];
            if (!s.Active || s.Generation != generation)
            {
                return false;
            }

            object owner = s.Owner;
            EventHandle handle = new EventHandle(typeId, slot, generation);
            Deactivate(slot);
            if (updateOwner)
            {
                EventOwnerMap.Remove(owner, handle);
            }

            return true;
        }

        public void Publish(in T evt)
        {
            if (publishing)
            {
                throw new EventSystemException(
                    $"[EventSystem] 正在派发 {TypeName}，不能再次 Publish 同类型。需要再发请使用 Post。");
            }

            if (iterateCount == 0)
            {
                return;
            }

            publishing = true;
            try
            {
                int n = iterateCount;
                for (int i = 0; i < n; i++)
                {
                    ref Slot s = ref slots[iterate[i]];
                    if (!s.Active)
                    {
                        continue;
                    }

                    Invoke(s.Handler, evt);
                }
            }
            finally
            {
                publishing = false;
                if (pendingFreeCount > 0)
                {
                    FlushPendingFree();
                    CompactIterate();
                }
            }
        }

        public void Post(in T evt)
        {
            EnsureRegistered();
            postQueue.Enqueue(evt);
        }

        public void Clear()
        {
            if (publishing)
            {
                throw new EventSystemException(
                    $"[EventSystem] 正在派发 {TypeName}，不能 Clear。");
            }

            if (slots == null)
            {
                DrainPostQueue();
                return;
            }

            for (int i = 0; i < slotCount; i++)
            {
                ref Slot s = ref slots[i];
                if (s.Active)
                {
                    EventOwnerMap.Remove(s.Owner, new EventHandle(typeId, i, s.Generation));
                }

                s.Handler = null;
                s.Owner = null;
                s.Active = false;
                s.Generation = s.Generation == 0 ? 1 : s.Generation + 1;
            }

            if (free.Length < slotCount)
            {
                Array.Resize(ref free, slotCount);
            }

            for (int i = 0; i < slotCount; i++)
            {
                free[i] = i;
            }

            freeCount = slotCount;
            pendingFreeCount = 0;
            iterateCount = 0;
            activeCount = 0;
            DrainPostQueue();
        }

        public int DrainPosted(int max)
        {
            int n = 0;
            while (n < max && postQueue.TryDequeue(out T evt))
            {
                Publish(in evt);
                n++;
            }

            return n;
        }

        void DrainPostQueue()
        {
            while (postQueue.TryDequeue(out _))
            {
            }
        }

        void EnsureRegistered()
        {
            if (typeId >= 0)
            {
                return;
            }

            lock (initLock)
            {
                if (typeId >= 0)
                {
                    return;
                }

                slots = new Slot[8];
                iterate = new int[8];
                free = new int[8];
                pendingFree = new int[8];
                typeId = EventRegistry.Register(this);
            }
        }

        int AllocSlot()
        {
            if (freeCount > 0)
            {
                return free[--freeCount];
            }

            if (slotCount == slots.Length)
            {
                int next = slots.Length * 2;
                Array.Resize(ref slots, next);
                Array.Resize(ref free, next);
                Array.Resize(ref pendingFree, next);
            }

            return slotCount++;
        }

        void Deactivate(int slot)
        {
            ref Slot s = ref slots[slot];
            s.Handler = null;
            s.Owner = null;
            s.Active = false;
            unchecked
            {
                s.Generation++;
            }

            activeCount--;
            if (publishing)
            {
                if (pendingFreeCount == pendingFree.Length)
                {
                    Array.Resize(ref pendingFree, pendingFree.Length * 2);
                }

                pendingFree[pendingFreeCount++] = slot;
                return;
            }

            if (freeCount == free.Length)
            {
                Array.Resize(ref free, free.Length * 2);
            }

            free[freeCount++] = slot;
            CompactIterate();
        }

        void FlushPendingFree()
        {
            for (int i = 0; i < pendingFreeCount; i++)
            {
                if (freeCount == free.Length)
                {
                    Array.Resize(ref free, free.Length * 2);
                }

                free[freeCount++] = pendingFree[i];
            }

            pendingFreeCount = 0;
        }

        void CompactIterate()
        {
            int write = 0;
            for (int i = 0; i < iterateCount; i++)
            {
                int slot = iterate[i];
                if (!slots[slot].Active)
                {
                    continue;
                }

                iterate[write++] = slot;
            }

            iterateCount = write;
        }

        static void Invoke(Action<T> handler, T evt)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            try
            {
                handler(evt);
            }
            catch (EventSystemException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
#else
            handler(evt);
#endif
        }
    }
}
