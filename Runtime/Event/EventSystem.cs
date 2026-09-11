using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Game
{
    public sealed class EventSystemException : InvalidOperationException
    {
        public EventSystemException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// 宿主事件总线。与 UIFrame 无关。热路径 Publish 零分配；跨线程用 Post。
    /// 同类型派发中禁止再次 Publish / Clear；需要再发用 Post。Dispatcher 由启动代码 EnsureDispatcher。
    /// </summary>
    public static class EventSystem
    {
        static int mainThreadId;

        public static bool ThreadChecks { get; set; }

        static EventSystem()
        {
            ResetToBuildDefaults();
        }

        public static void ResetToBuildDefaults()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            ThreadChecks = true;
#else
            ThreadChecks = false;
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnDomainReload()
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            ResetToBuildDefaults();
            ClearAll();
            ShutdownDispatcher();
        }

        public static EventHandle Subscribe<T>(Action<T> handler)
        {
            return Subscribe(null, handler);
        }

        public static EventHandle Subscribe<T>(object owner, Action<T> handler)
        {
            EnsureMainThread();
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            EventHandle handle = EventBucket<T>.Instance.Subscribe(handler, owner);
            if (owner != null)
            {
                EventOwnerMap.Add(owner, handle);
            }

            return handle;
        }

        public static bool Unsubscribe(EventHandle handle)
        {
            EnsureMainThread();
            if (!handle.IsValid)
            {
                return false;
            }

            IEventBucket bucket = EventRegistry.GetBucket(handle.TypeId);
            return bucket != null && bucket.Unsubscribe(handle.Slot, handle.Generation);
        }

        public static void UnsubscribeAll(object owner)
        {
            EnsureMainThread();
            if (!EventOwnerMap.TryTake(owner, out List<EventHandle> list))
            {
                return;
            }

            for (int i = 0; i < list.Count; i++)
            {
                EventHandle handle = list[i];
                IEventBucket bucket = EventRegistry.GetBucket(handle.TypeId);
                bucket?.Unsubscribe(handle.Slot, handle.Generation, updateOwner: false);
            }

            list.Clear();
        }

        public static void Publish<T>(in T evt)
        {
            EnsureMainThread();
            EventBucket<T>.Instance.Publish(in evt);
        }

        public static void Post<T>(in T evt) where T : struct
        {
            EventDispatcher.ThrowIfMissing();
            EventBucket<T>.Instance.Post(in evt);
        }

        public static void Clear<T>()
        {
            EnsureMainThread();
            EventBucket<T>.Instance.Clear();
        }

        public static void ClearAll()
        {
            EnsureMainThread();
            EventRegistry.ThrowIfPublishing();
            EventRegistry.ClearAllBuckets();
            EventOwnerMap.Clear();
        }

        public static int ListenerCount<T>()
        {
            EnsureMainThread();
            return EventBucket<T>.Instance.ListenerCount;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public static void DumpListenerCounts()
        {
            EnsureMainThread();
            EventRegistry.DumpListenerCounts();
        }
#endif

        public static void EnsureDispatcher()
        {
            EnsureMainThread();
            EventDispatcher.Ensure();
        }

        public static void ShutdownDispatcher()
        {
            EventDispatcher.Shutdown();
        }

        public static int DrainPosted(int maxCount = EventRegistry.MaxDrainPerFrame)
        {
            EnsureMainThread();
            return EventRegistry.DrainPosted(maxCount);
        }

        static void EnsureMainThread()
        {
            int current = Thread.CurrentThread.ManagedThreadId;
            if (mainThreadId == 0)
            {
                mainThreadId = current;
                return;
            }

            if (!ThreadChecks || current == mainThreadId)
            {
                return;
            }

            throw new EventSystemException(
                "[EventSystem] 必须在主线程调用 Publish / Subscribe / Clear。跨线程请用 Post。");
        }
    }
}
