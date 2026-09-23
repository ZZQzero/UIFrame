using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using YooAsset;

[assembly: InternalsVisibleTo("Scene.EditMode.Tests")]
[assembly: InternalsVisibleTo("Scene.PlayMode.Tests")]

namespace Game.Scene
{
    public static class GameScene
    {
        internal static readonly Dictionary<string, ISceneHandle> Loaded = new(StringComparer.Ordinal);
        static readonly Action<float> RelayProgress = OnProgress;

        static ISceneLoader loader;
        static UniTask inflight = UniTask.CompletedTask;
        static bool busy;
        static string activeId;
        static float progress;
        static Action<float> progressCallback;

        public static bool IsInited => loader != null;

        public static bool IsBusy
        {
            get
            {
                EnsureInited();
                return busy;
            }
        }

        public static string ActiveId
        {
            get
            {
                EnsureInited();
                return activeId;
            }
        }

        public static float Progress
        {
            get
            {
                EnsureInited();
                return progress;
            }
        }

        public static void Init(ResourcePackage package)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            InitLoader(new YooAssetSceneLoader(package));
        }

        internal static void InitLoader(ISceneLoader sceneLoader)
        {
            if (loader != null)
            {
                throw new InvalidOperationException("GameScene 已初始化。");
            }

            loader = sceneLoader ?? throw new ArgumentNullException(nameof(sceneLoader));
        }

        public static async UniTask ShutdownAsync()
        {
            EnsureInited();
            if (busy)
            {
                try
                {
                    await inflight;
                }
                catch
                {
                    // ignored
                }
            }

            Loaded.Clear();
            loader = null;
            activeId = null;
            busy = false;
            progress = 0f;
            progressCallback = null;
            inflight = UniTask.CompletedTask;
        }

        public static bool IsLoaded(string location)
        {
            RequireQuery(location);
            return Loaded.ContainsKey(location);
        }

        public static bool IsPreloaded(string location)
        {
            RequireQuery(location);
            return Loaded.TryGetValue(location, out ISceneHandle handle) && handle.IsPreloaded;
        }

        public static UniTask SwitchAsync(string location, Action<float> onProgress = null)
        {
            RequireIncoming(location);
            if (activeId != null && !Loaded.ContainsKey(activeId))
            {
                throw new InvalidOperationException($"当前活动场景未登记: {activeId}");
            }

            return Run(onProgress, () => SwitchCoreAsync(location));
        }

        public static UniTask LoadAsync(
            string location,
            LoadSceneMode mode,
            Action<float> onProgress = null)
        {
            RequireIncoming(location);
            if (mode == LoadSceneMode.Single)
            {
                return Run(onProgress, () => LoadSingleCoreAsync(location));
            }

            return Run(onProgress, () => AddLoadedAsync(location, mode, true));
        }

        public static UniTask LoadBuiltinAsync(string location, Action<float> onProgress = null)
        {
            RequireIdle(location);
            RequireNoSuspendedScene();
            return Run(onProgress, () => LoadBuiltinCoreAsync(location));
        }

        public static UniTask PreloadAsync(
            string location,
            LoadSceneMode mode,
            Action<float> onProgress = null)
        {
            RequireIncoming(location);
            return Run(onProgress, () => AddLoadedAsync(location, mode, false));
        }

        public static UniTask ActivateAsync(string location)
        {
            ISceneHandle handle = RequirePresent(location);
            return Run(null, () => ActivateCoreAsync(location, handle));
        }

        public static UniTask UnloadAsync(string location)
        {
            RequirePresent(location);
            RequireNoSuspendedScene();
            return Run(null, () => UnloadLoadedAsync(location));
        }

        static UniTask Run(Action<float> onProgress, Func<UniTask> work)
        {
            busy = true;
            progressCallback = onProgress;
            inflight = ExecuteAsync(work).Preserve();
            return inflight;
        }

        static async UniTask ExecuteAsync(Func<UniTask> work)
        {
            try
            {
                await work();
            }
            finally
            {
                progressCallback = null;
                busy = false;
                progress = 0f;
            }
        }

        static async UniTask SwitchCoreAsync(string location)
        {
            string previous = activeId;
            ISceneHandle handle = await loader.LoadAsync(
                location,
                LoadSceneMode.Additive,
                true,
                RelayProgress);
            await BindActiveAsync(location, handle);
            if (previous != null)
            {
                await UnloadLoadedAsync(previous);
            }
        }

        static async UniTask AddLoadedAsync(
            string location,
            LoadSceneMode mode,
            bool allowSceneActivation)
        {
            Loaded.Add(
                location,
                await loader.LoadAsync(location, mode, allowSceneActivation, RelayProgress));
        }

        static async UniTask LoadSingleCoreAsync(string location)
        {
            ISceneHandle handle = await loader.LoadAsync(
                location,
                LoadSceneMode.Single,
                true,
                RelayProgress);
            await DiscardOthersExceptAsync(location);
            await BindActiveAsync(location, handle);
        }

        static async UniTask LoadBuiltinCoreAsync(string location)
        {
            AsyncOperation operation = SceneManager.LoadSceneAsync(location, LoadSceneMode.Single);
            if (operation == null)
            {
                throw new InvalidOperationException($"无法加载内置场景: {location}");
            }

            while (!operation.isDone)
            {
                RelayProgress(operation.progress);
                await UniTask.Yield();
            }

            RelayProgress(operation.progress);
            await DiscardOthersExceptAsync(null);
            activeId = location;
        }

        static async UniTask ActivateCoreAsync(string location, ISceneHandle handle)
        {
            await handle.ActivateAsync();
            activeId = location;
            if (handle.Mode == LoadSceneMode.Single)
            {
                await DiscardOthersExceptAsync(location);
            }
        }

        static async UniTask BindActiveAsync(string location, ISceneHandle handle)
        {
            try
            {
                await handle.ActivateAsync();
                Loaded.Add(location, handle);
                activeId = location;
            }
            catch
            {
                await AbandonUnregisteredAsync(handle);
                throw;
            }
        }

        static async UniTask UnloadLoadedAsync(string location)
        {
            ISceneHandle handle = Loaded[location];
            await handle.UnloadAsync();
            Loaded.Remove(location);
            if (activeId == location)
            {
                activeId = null;
            }
        }

        static async UniTask DiscardOthersExceptAsync(string keep)
        {
            var remove = new string[Loaded.Count];
            int index = 0;
            foreach (string location in Loaded.Keys)
            {
                if (location != keep)
                {
                    remove[index++] = location;
                }
            }

            for (int i = 0; i < index; i++)
            {
                await UnloadLoadedAsync(remove[i]);
            }
        }

        static async UniTask AbandonUnregisteredAsync(ISceneHandle handle)
        {
            try
            {
                await handle.UnloadAsync();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        static void OnProgress(float value)
        {
            progress = value;
            progressCallback?.Invoke(value);
        }

        static void EnsureInited()
        {
            if (loader == null)
            {
                throw new InvalidOperationException("GameScene 未初始化。");
            }
        }

        static void RequireQuery(string location)
        {
            EnsureInited();
            if (string.IsNullOrWhiteSpace(location))
            {
                throw new ArgumentException("场景地址不能为空。", nameof(location));
            }
        }

        static void RequireIdle(string location)
        {
            RequireQuery(location);
            if (busy)
            {
                throw new InvalidOperationException("场景操作进行中。");
            }
        }

        static void RequireNoSuspendedScene()
        {
            foreach (var entry in Loaded)
            {
                if (entry.Value.IsPreloaded)
                {
                    throw new InvalidOperationException(
                        $"场景 {entry.Key} 尚未激活，会阻塞后续加载／卸载；请先显式 ActivateAsync。");
                }
            }
        }

        static void RequireIncoming(string location)
        {
            RequireIdle(location);
            RequireNoSuspendedScene();
            if (Loaded.ContainsKey(location))
            {
                throw new InvalidOperationException($"场景已加载: {location}");
            }
        }

        static ISceneHandle RequirePresent(string location)
        {
            RequireIdle(location);
            if (!Loaded.TryGetValue(location, out ISceneHandle handle))
            {
                throw new InvalidOperationException($"场景未加载: {location}");
            }

            return handle;
        }
    }
}
