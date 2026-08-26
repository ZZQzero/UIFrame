using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YooAsset;

namespace Game.Pooling
{
    public interface IPrefabHandle : IDisposable
    {
        GameObject Instantiate(Transform parent);
    }

    public interface IPrefabProvider
    {
        UniTask<IPrefabHandle> LoadAsync(string location);
    }

    /// <summary>
    /// 使用外部已初始化的 ResourcePackage 加载 Prefab。
    /// </summary>
    public sealed class YooAssetPrefabProvider : IPrefabProvider
    {
        private readonly ResourcePackage package;

        public YooAssetPrefabProvider(ResourcePackage package)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
        }

        public async UniTask<IPrefabHandle> LoadAsync(string location)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                throw new ArgumentException("Prefab location cannot be empty.", nameof(location));
            }

            AssetHandle handle = package.LoadAssetAsync<GameObject>(location);
            await handle;

            if (handle.Status != EOperationStatus.Succeeded)
            {
                string error = handle.Error;
                handle.Release();
                throw new InvalidOperationException(
                    $"Failed to load pooled prefab '{location}': {error}");
            }

            return new YooAssetPrefabHandle(handle);
        }

        private sealed class YooAssetPrefabHandle : IPrefabHandle
        {
            private AssetHandle handle;

            public YooAssetPrefabHandle(AssetHandle handle)
            {
                this.handle = handle;
            }

            public GameObject Instantiate(Transform parent)
            {
                if (handle == null)
                {
                    throw new ObjectDisposedException(nameof(YooAssetPrefabHandle));
                }

                var options = new InstantiateOptions(false, parent, false);
                GameObject instance = handle.InstantiateSync(options);
                if (instance == null)
                {
                    throw new InvalidOperationException(
                        "YooAsset returned a null prefab instance.");
                }

                return instance;
            }

            public void Dispose()
            {
                if (handle == null)
                {
                    return;
                }

                handle.Release();
                handle = null;
            }
        }
    }
}
