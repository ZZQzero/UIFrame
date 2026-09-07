using System;
using UnityEngine;
using YooAsset;

namespace Game.Pooling
{
    /// <summary>
    /// 进程内默认 <see cref="GameObjectPoolService"/> 入口。可选；测试和隔离场景请直接 new 服务。
    /// <see cref="Init(ResourcePackage, Transform)"/> 的 persistRoot 必须比 <c>UI.Shutdown</c> 活得更久，不要挂在 UIFrameRoot 下。
    /// LoopScroll / UIItem 仍通过 <c>SetPool</c> 注入，不要在框架内部写死 <see cref="Service"/>。
    /// 不叫 <c>Pool</c>，以免和 <c>UILoopScrollBase.Pool</c> 属性撞名。
    /// </summary>
    public static class GamePool
    {
        const string PoolRootName = "[GameObjectPool]";

        static GameObjectPoolService service;
        static Transform ownedRoot;

        public static bool IsInited => service != null && !service.IsDisposed;

        public static GameObjectPoolService Service => service;

        /// <summary>创建默认池。重复调用会先 <see cref="Shutdown"/> 再建。</summary>
        public static void Init(ResourcePackage package, Transform persistRoot)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            Init(new YooAssetPrefabProvider(package), persistRoot);
        }

        /// <summary>用自定义 Prefab 提供者创建默认池。</summary>
        public static void Init(IPrefabProvider prefabProvider, Transform persistRoot)
        {
            if (prefabProvider == null)
            {
                throw new ArgumentNullException(nameof(prefabProvider));
            }

            if (persistRoot == null)
            {
                throw new ArgumentNullException(nameof(persistRoot));
            }

            Shutdown();

            var poolRoot = new GameObject(PoolRootName).transform;
            poolRoot.SetParent(persistRoot, false);
            ownedRoot = poolRoot;
            service = new GameObjectPoolService(prefabProvider, poolRoot);
        }

        /// <summary>释放默认池。未 Init 时为空操作。退出顺序必须是先 <c>UI.Shutdown</c>，再调本方法。</summary>
        public static void Shutdown()
        {
            service?.Dispose();
            service = null;
            DestroyOwnedRoot();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnDomainReload()
        {
            service = null;
            ownedRoot = null;
        }

        static void DestroyOwnedRoot()
        {
            if (ownedRoot == null)
            {
                return;
            }

            var rootObject = ownedRoot.gameObject;
            ownedRoot = null;
            if (rootObject == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(rootObject);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(rootObject);
            }
        }
    }
}
