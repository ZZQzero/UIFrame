using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Cysharp.Threading.Tasks;
using UnityEngine.SceneManagement;
using YooAsset;

[assembly: InternalsVisibleTo("Scene.EditMode.Tests")]
[assembly: InternalsVisibleTo("Scene.PlayMode.Tests")]

namespace Game.Scene
{
    public static class GameScene
    {
        static readonly Dictionary<string, ISceneHandle> Loaded = new(StringComparer.Ordinal);
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
            return Watch(SwitchCoreAsync(location, onProgress));
        }

        public static UniTask LoadAsync(
            string location,
            LoadSceneMode mode,
            Action<float> onProgress = null)
        {
            RequireIncoming(location);
            if (mode == LoadSceneMode.Single)
            {
                return Watch(LoadSingleCoreAsync(location, onProgress));
            }

            return Watch(LoadIncomingCoreAsync(location, LoadSceneMode.Additive, true, onProgress));
        }

        public static UniTask PreloadAsync(
            string location,
            LoadSceneMode mode,
            Action<float> onProgress = null)
        {
            RequireIncoming(location);
            return Watch(LoadIncomingCoreAsync(location, mode, false, onProgress));
        }

        public static UniTask ActivateAsync(string location)
        {
            ISceneHandle handle = RequirePresent(location);
            return Watch(ActivateCoreAsync(location, handle));
        }

        public static UniTask UnloadAsync(string location)
        {
            RequirePresent(location);
            return Watch(UnloadCoreAsync(location));
        }

        static UniTask Watch(UniTask task)
        {
            inflight = task.Preserve();
            return inflight;
        }

        static async UniTask SwitchCoreAsync(string location, Action<float> onProgress)
        {
            busy = true;
            progressCallback = onProgress;
            try
            {
                string previous = activeId;
                ISceneHandle handle = await loader.LoadAsync(
                    location,
                    LoadSceneMode.Additive,
                    true,
                    RelayProgress);
                Loaded.Add(location, handle);
                handle.ActivateScene();
                activeId = location;
                if (previous != null)
                {
                    await UnloadLoadedAsync(previous);
                }
            }
            finally
            {
                EndOperation();
            }
        }

        static async UniTask LoadIncomingCoreAsync(
            string location,
            LoadSceneMode mode,
            bool allowSceneActivation,
            Action<float> onProgress)
        {
            busy = true;
            progressCallback = onProgress;
            try
            {
                ISceneHandle handle = await loader.LoadAsync(
                    location,
                    mode,
                    allowSceneActivation,
                    RelayProgress);
                Loaded.Add(location, handle);
            }
            finally
            {
                EndOperation();
            }
        }

        static async UniTask LoadSingleCoreAsync(string location, Action<float> onProgress)
        {
            busy = true;
            progressCallback = onProgress;
            try
            {
                ISceneHandle handle = await loader.LoadAsync(
                    location,
                    LoadSceneMode.Single,
                    true,
                    RelayProgress);
                Loaded.Add(location, handle);
                activeId = location;
                await DiscardOthersExceptAsync(location);
                handle.ActivateScene();
            }
            finally
            {
                EndOperation();
            }
        }

        static async UniTask ActivateCoreAsync(string location, ISceneHandle handle)
        {
            busy = true;
            try
            {
                if (handle.IsPreloaded)
                {
                    await handle.ActivatePreloadedAsync();
                }

                activeId = location;
                if (handle.Mode == LoadSceneMode.Single)
                {
                    await DiscardOthersExceptAsync(location);
                }

                handle.ActivateScene();
            }
            finally
            {
                EndOperation();
            }
        }

        static async UniTask UnloadCoreAsync(string location)
        {
            busy = true;
            try
            {
                await UnloadLoadedAsync(location);
            }
            finally
            {
                EndOperation();
            }
        }

        static async UniTask UnloadLoadedAsync(string location)
        {
            ISceneHandle handle = Loaded[location];
            await handle.UnloadAsync();
            Loaded.Remove(location);
            if (string.Equals(activeId, location, StringComparison.Ordinal))
            {
                activeId = null;
            }
        }

        static async UniTask DiscardOthersExceptAsync(string keep)
        {
            if (Loaded.Count <= 1)
            {
                return;
            }

            var remove = new string[Loaded.Count - 1];
            var handles = new ISceneHandle[Loaded.Count - 1];
            int index = 0;
            foreach (KeyValuePair<string, ISceneHandle> pair in Loaded)
            {
                if (!string.Equals(pair.Key, keep, StringComparison.Ordinal))
                {
                    remove[index] = pair.Key;
                    handles[index] = pair.Value;
                    index++;
                }
            }

            for (int i = 0; i < index; i++)
            {
                await handles[i].UnloadAsync();
                Loaded.Remove(remove[i]);
            }
        }

        static void OnProgress(float value)
        {
            progress = value;
            progressCallback?.Invoke(value);
        }

        static void EndOperation()
        {
            progressCallback = null;
            busy = false;
            progress = 0f;
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

        static void RequireIncoming(string location)
        {
            RequireQuery(location);
            if (busy)
            {
                throw new InvalidOperationException("场景操作进行中。");
            }

            if (Loaded.ContainsKey(location))
            {
                throw new InvalidOperationException($"场景已加载: {location}");
            }
        }

        static ISceneHandle RequirePresent(string location)
        {
            RequireQuery(location);
            if (busy)
            {
                throw new InvalidOperationException("场景操作进行中。");
            }

            if (!Loaded.TryGetValue(location, out ISceneHandle handle))
            {
                throw new InvalidOperationException($"场景未加载: {location}");
            }

            return handle;
        }
    }
}
