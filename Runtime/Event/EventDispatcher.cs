using System.Threading;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 主线程 Drain Post 队列。由 Launch 调用 EnsureDispatcher 创建。
    /// </summary>
    [AddComponentMenu("")]
    sealed class EventDispatcher : MonoBehaviour
    {
        const string ObjectName = "[EventDispatcher]";

        static EventDispatcher instance;
        static int dispatcherReady;

        internal static void Ensure()
        {
            if (instance != null)
            {
                return;
            }

            var go = new GameObject(ObjectName);
            if (Application.isPlaying)
            {
                UnityEngine.Object.DontDestroyOnLoad(go);
            }

            instance = go.AddComponent<EventDispatcher>();
            Volatile.Write(ref dispatcherReady, 1);
        }

        internal static void ThrowIfMissing()
        {
            if (Volatile.Read(ref dispatcherReady) != 0)
            {
                return;
            }

            throw new EventSystemException(
                "[EventSystem] Dispatcher 未创建。请在启动时调用 EventSystem.EnsureDispatcher，再 Post。");
        }

        internal static void Shutdown()
        {
            Volatile.Write(ref dispatcherReady, 0);
            if (instance == null)
            {
                return;
            }

            EventDispatcher current = instance;
            instance = null;
            if (current == null)
            {
                return;
            }

            GameObject go = current.gameObject;
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(go);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        void LateUpdate()
        {
            EventRegistry.DrainPosted(EventRegistry.MaxDrainPerFrame);
        }

        void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
                Volatile.Write(ref dispatcherReady, 0);
            }
        }
    }
}
