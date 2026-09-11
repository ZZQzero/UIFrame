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
                if (allowSceneActivation)
                {
                    while (!handle.IsDone)
                    {
                        report?.Invoke(handle.Progress);
                        await UniTask.Yield();
                    }

                    report?.Invoke(handle.Progress);
                    if (handle.Status != EOperationStatus.Succeeded)
                    {
                        throw new InvalidOperationException(handle.Error);
                    }

                    return new YooAssetSceneHandle(handle, mode, false);
                }

                while (!handle.IsDone && handle.Progress < SuspendReadyProgress)
                {
                    report?.Invoke(handle.Progress);
                    await UniTask.Yield();
                }

                report?.Invoke(handle.Progress);
                if (handle.Status == EOperationStatus.Failed)
                {
                    throw new InvalidOperationException(handle.Error);
                }

                return new YooAssetSceneHandle(handle, mode, true);
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

            public void ActivateScene()
            {
                UnityEngine.SceneManagement.Scene scene = handle.SceneObject;
                if (scene.IsValid() && scene.isLoaded && scene == SceneManager.GetActiveScene())
                {
                    return;
                }

                if (handle.ActivateScene())
                {
                    return;
                }

                scene = handle.SceneObject;
                if (scene.IsValid() && scene.isLoaded && scene == SceneManager.GetActiveScene())
                {
                    return;
                }

                string error = handle.Error;
                if (string.IsNullOrEmpty(error))
                {
                    error = scene.IsValid()
                        ? $"无法激活场景: {scene.name}"
                        : "无法激活场景。";
                }

                throw new InvalidOperationException(error);
            }

            public async UniTask ActivatePreloadedAsync()
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

            public async UniTask UnloadAsync()
            {
                if (!YooAssets.IsInitialized || !handle.IsValid)
                {
                    return;
                }

                UnityEngine.SceneManagement.Scene scene = handle.SceneObject;
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    handle.Release();
                    return;
                }

                try
                {
                    UnloadSceneOperation operation = handle.UnloadSceneAsync();
                    await operation;
                    if (!YooAssets.IsInitialized)
                    {
                        return;
                    }

                    if (operation.Status != EOperationStatus.Succeeded)
                    {
                        handle.Release();
                        throw new InvalidOperationException(operation.Error);
                    }
                }
                catch (Exception)
                {
                    if (!YooAssets.IsInitialized)
                    {
                        return;
                    }

                    throw;
                }
            }
        }
    }
}
