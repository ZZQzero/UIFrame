using System;
using Cysharp.Threading.Tasks;
using UnityEngine.SceneManagement;
using YooAsset;

namespace Game.Scene
{
    sealed class YooAssetSceneLoader : ISceneLoader
    {
        const float SuspendReadyProgress = 0.9f;

        readonly ResourcePackage package;

        public YooAssetSceneLoader(ResourcePackage package)
        {
            this.package = package;
        }

        public async UniTask<ISceneHandle> LoadAsync(
            string location,
            LoadSceneMode mode,
            bool allowSceneActivation,
            Action<float> report)
        {
            YooAsset.SceneHandle handle = package.LoadSceneAsync(
                location,
                mode,
                LocalPhysicsMode.None,
                allowSceneActivation,
                0);

            try
            {
                while (!handle.IsDone && (allowSceneActivation || handle.Progress < SuspendReadyProgress))
                {
                    report?.Invoke(handle.Progress);
                    await UniTask.Yield();
                }

                report?.Invoke(handle.Progress);
                if (allowSceneActivation
                    ? handle.Status != EOperationStatus.Succeeded
                    : handle.Status == EOperationStatus.Failed)
                {
                    throw new InvalidOperationException(handle.Error);
                }

                return new YooAssetSceneHandle(handle, mode, !allowSceneActivation);
            }
            catch
            {
                if (handle.IsValid)
                {
                    handle.Release();
                }

                throw;
            }
        }

        sealed class YooAssetSceneHandle : ISceneHandle
        {
            readonly YooAsset.SceneHandle handle;

            public YooAssetSceneHandle(YooAsset.SceneHandle handle, LoadSceneMode mode, bool preloaded)
            {
                this.handle = handle;
                Mode = mode;
                IsPreloaded = preloaded;
            }

            public LoadSceneMode Mode { get; }

            public bool IsPreloaded { get; private set; }

            public async UniTask ActivateAsync()
            {
                if (IsPreloaded)
                {
                    if (!handle.AllowSceneActivation())
                    {
                        throw new InvalidOperationException(handle.Error);
                    }

                    while (!handle.IsDone)
                    {
                        await UniTask.Yield();
                    }

                    if (handle.Status != EOperationStatus.Succeeded)
                    {
                        throw new InvalidOperationException(handle.Error);
                    }

                    IsPreloaded = false;
                }

                if (handle.ActivateScene())
                {
                    return;
                }

                UnityEngine.SceneManagement.Scene scene = handle.SceneObject;
                if (scene.IsValid() && scene.isLoaded && scene == SceneManager.GetActiveScene())
                {
                    return;
                }

                throw new InvalidOperationException(
                    string.IsNullOrEmpty(handle.Error) ? "无法激活场景。" : handle.Error);
            }

            public async UniTask UnloadAsync()
            {
                if (IsPreloaded)
                {
                    throw new InvalidOperationException(
                        "不能卸载尚未激活的预加载场景；当前加载器不支持安全撤销。请显式 ActivateAsync 后再卸载。");
                }

                if (!handle.IsValid)
                {
                    return;
                }

                UnityEngine.SceneManagement.Scene scene = handle.SceneObject;
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    handle.Release();
                    return;
                }

                UnloadSceneOperation operation = handle.UnloadSceneAsync();
                await operation;
                if (operation.Status != EOperationStatus.Succeeded)
                {
                    handle.Release();
                    throw new InvalidOperationException(operation.Error);
                }
            }
        }
    }
}
