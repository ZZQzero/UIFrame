using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YooAsset;

namespace Game.Audio
{
    public static class GameAudio
    {
        private const string RootName = "[GameAudio]";
        private const float MinimumMixerDb = -80f;

        private static readonly List<AudioClipCache.AudioClipLease>
            ResidentLeases = new();

        private static ResolvedAudioConfig config;
        private static AudioClipCache cache;
        private static AudioRuntimeDriver driver;
        private static Transform ownedRoot;
        private static RuntimeState state;
        private static CancellationTokenSource runtimeCancellation;
        private static UniTaskCompletionSource operationsIdle;
        private static BgmRequestGate bgmRequestGate = new();
        private static int pendingOperations;
        private static bool driverDestroyedUnexpectedly;

        public static bool IsInited =>
            state == RuntimeState.Running &&
            driver != null &&
            !driverDestroyedUnexpectedly;

        public static async UniTask InitAsync(
            ResourcePackage package,
            AudioRuntimeConfig runtimeConfig,
            Transform persistRoot,
            CancellationToken cancellationToken = default)
        {
            RequireMainThread(nameof(InitAsync));
            if (state != RuntimeState.None)
            {
                throw new AudioStateException(
                    "GameAudio.InitAsync 重复调用或上次未按流程 Shutdown。");
            }

            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            if (!package.PackageValid)
            {
                throw new ArgumentException(
                    "GameAudio.InitAsync 要求传入已成功初始化的 ResourcePackage。",
                    nameof(package));
            }

            if (runtimeConfig == null)
            {
                throw new ArgumentNullException(nameof(runtimeConfig));
            }

            if (persistRoot == null)
            {
                throw new ArgumentNullException(nameof(persistRoot));
            }

            if (!persistRoot.gameObject.activeInHierarchy)
            {
                throw new AudioStateException(
                    "GameAudio.InitAsync 要求 persistRoot 处于 activeInHierarchy 状态。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            state = RuntimeState.Initializing;

            var rootObject = new GameObject(RootName);
            if (Application.isPlaying)
            {
                UnityEngine.Object.DontDestroyOnLoad(rootObject);
            }

            bool driverInitialized = false;
            try
            {
                ResolvedAudioConfig resolved = runtimeConfig.Resolve();
                foreach (AudioEntry entry in resolved.Catalog.Values)
                {
                    if (!package.IsLocationValid(entry.Location))
                    {
                        throw new InvalidOperationException(
                            $"YooAsset 中不存在配置的音频 location：{entry.Location}。");
                    }
                }

                var createdCache = new AudioClipCache(
                    package,
                    resolved.CacheRetentionSeconds);
                AudioRuntimeDriver createdDriver =
                    rootObject.AddComponent<AudioRuntimeDriver>();

                config = resolved;
                cache = createdCache;
                driver = createdDriver;
                ownedRoot = rootObject.transform;
                runtimeCancellation = new CancellationTokenSource();
                createdDriver.Initialize(resolved, createdCache);
                driverInitialized = true;

                foreach (AudioEntry entry in resolved.Catalog.Values)
                {
                    if (entry.LoadMode != AudioLoadMode.Resident)
                    {
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    AudioClipCache.AudioClipLease lease =
                        await createdCache.AcquireAsync(
                            entry.Location,
                            AudioLoadMode.Resident,
                            cancellationToken);

                    ResidentLeases.Add(lease);
                }

                state = RuntimeState.Running;
            }
            catch
            {
                DisposeResidentLeases();
                if (driverInitialized)
                {
                    if (cache.LoadingCount != 0)
                    {
                        await cache.WaitForIdleAsync();
                    }

                    driver.Shutdown();
                }
                else
                {
                    cache?.Dispose();
                }

                config = null;
                cache = null;
                driver = null;
                ownedRoot = null;
                runtimeCancellation?.Dispose();
                runtimeCancellation = null;
                state = RuntimeState.None;
                DestroyObject(rootObject);
                throw;
            }
        }

        public static UniTask<AudioPlayResult> TryPlayAsync(
            AudioId id,
            CancellationToken cancellationToken = default)
        {
            return TryPlayAsync(
                id,
                AudioPlayOptions.Default,
                cancellationToken);
        }

        public static UniTask<AudioPlayResult> TryPlayAsync(
            AudioId id,
            AudioPlayOptions options,
            CancellationToken cancellationToken = default)
        {
            return TryPlayInternalAsync(
                nameof(TryPlayAsync),
                id,
                options,
                requiredBus: null,
                fadeInSeconds: 0f,
                fadeOutPreviousBgmSeconds: 0f,
                cancellationToken);
        }

        public static UniTask<AudioPlayResult> TryPlayBgmAsync(
            AudioId id,
            float crossFadeSeconds = 0.5f,
            CancellationToken cancellationToken = default)
        {
            return TryPlayBgmAsync(
                id,
                AudioPlayOptions.Default,
                crossFadeSeconds,
                cancellationToken);
        }

        public static UniTask<AudioPlayResult> TryPlayBgmAsync(
            AudioId id,
            AudioPlayOptions options,
            float crossFadeSeconds = 0.5f,
            CancellationToken cancellationToken = default)
        {
            return TryPlayInternalAsync(
                nameof(TryPlayBgmAsync),
                id,
                options,
                AudioBus.Bgm,
                crossFadeSeconds,
                crossFadeSeconds,
                cancellationToken);
        }

        public static bool TryStop(
            SoundHandle handle,
            float fadeOutSeconds = 0f)
        {
            RequireMainThread(nameof(TryStop));
            return RequireDriver(nameof(TryStop))
                .TryStop(handle, fadeOutSeconds);
        }

        public static bool IsPlaying(SoundHandle handle)
        {
            RequireMainThread(nameof(IsPlaying));
            return RequireDriver(nameof(IsPlaying)).IsPlaying(handle);
        }

        public static int StopBus(
            AudioBus bus,
            float fadeOutSeconds = 0f)
        {
            RequireMainThread(nameof(StopBus));
            ValidateBus(bus);
            return RequireDriver(nameof(StopBus))
                .StopBus(bus, fadeOutSeconds);
        }

        public static void SetMasterVolume(float linearVolume)
        {
            RequireMainThread(nameof(SetMasterVolume));
            ResolvedAudioConfig current = RequireConfig(
                nameof(SetMasterVolume));
            SetMixerVolume(
                current.MasterVolumeParameter,
                linearVolume);
        }

        public static float GetMasterVolume()
        {
            RequireMainThread(nameof(GetMasterVolume));
            ResolvedAudioConfig current = RequireConfig(
                nameof(GetMasterVolume));
            return GetMixerVolume(current.MasterVolumeParameter);
        }

        public static void SetBusVolume(
            AudioBus bus,
            float linearVolume)
        {
            RequireMainThread(nameof(SetBusVolume));
            ValidateBus(bus);
            ResolvedAudioConfig current = RequireConfig(
                nameof(SetBusVolume));
            SetMixerVolume(
                current.VolumeParameters[bus],
                linearVolume);
        }

        public static float GetBusVolume(AudioBus bus)
        {
            RequireMainThread(nameof(GetBusVolume));
            ValidateBus(bus);
            ResolvedAudioConfig current = RequireConfig(
                nameof(GetBusVolume));
            return GetMixerVolume(current.VolumeParameters[bus]);
        }

        public static int StopBgm(float fadeOutSeconds = 0f)
        {
            RequireMainThread(nameof(StopBgm));
            return RequireDriver(nameof(StopBgm))
                .StopBus(AudioBus.Bgm, fadeOutSeconds);
        }

        public static async UniTask ShutdownAsync()
        {
            RequireMainThread(nameof(ShutdownAsync));
            if (state == RuntimeState.Running)
            {
                BeginShutdown();
            }
            else if (state == RuntimeState.ShuttingDown)
            {
                throw new AudioStateException(
                    "GameAudio.ShutdownAsync 不允许并发或重复调用。");
            }
            else
            {
                throw new AudioStateException(
                    "GameAudio.ShutdownAsync 只能在成功 InitAsync 后调用一次。");
            }

            if (pendingOperations != 0)
            {
                await operationsIdle.Task;
            }

            await cache.WaitForIdleAsync();
            CompleteShutdown();
        }

        internal static void NotifyDriverDestroyed(
            AudioRuntimeDriver unavailable)
        {
            if (driver != unavailable ||
                state != RuntimeState.Running ||
                driverDestroyedUnexpectedly)
            {
                return;
            }

            driverDestroyedUnexpectedly = true;
            Debug.LogError(
                "[GameAudio] AudioRuntimeDriver 被意外销毁。" +
                "禁止直接销毁 [GameAudio]，请修复调用方生命周期并由 Launch 调用 ShutdownAsync。");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnDomainReload()
        {
            ResidentLeases.Clear();
            config = null;
            cache = null;
            driver = null;
            ownedRoot = null;
            state = RuntimeState.None;
            runtimeCancellation?.Dispose();
            runtimeCancellation = null;
            operationsIdle = null;
            bgmRequestGate = new BgmRequestGate();
            pendingOperations = 0;
            driverDestroyedUnexpectedly = false;
        }

        private static async UniTask<AudioPlayResult> TryPlayInternalAsync(
            string api,
            AudioId id,
            AudioPlayOptions options,
            AudioBus? requiredBus,
            float fadeInSeconds,
            float fadeOutPreviousBgmSeconds,
            CancellationToken cancellationToken)
        {
            RequireMainThread(api);
            AudioRuntimeDriver currentDriver = RequireDriver(api);
            if (!id.IsValid)
            {
                throw new ArgumentException(
                    "播放接口不接受未初始化的 AudioId。",
                    nameof(id));
            }

            if (!options.IsInitialized)
            {
                throw new ArgumentException(
                    "AudioPlayOptions 必须通过构造函数或 Default/At 创建。",
                    nameof(options));
            }

            if (!float.IsFinite(fadeInSeconds) || fadeInSeconds < 0f ||
                !float.IsFinite(fadeOutPreviousBgmSeconds) ||
                fadeOutPreviousBgmSeconds < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fadeInSeconds),
                    "淡入淡出时长必须是大于等于 0 的有限值。");
            }

            if (!config.Catalog.TryGetValue(id, out AudioEntry entry))
            {
                throw new KeyNotFoundException(
                    $"AudioRuntimeConfig 未配置 AudioId：{id.Value}。");
            }

            if (requiredBus.HasValue)
            {
                if (entry.Bus != requiredBus.Value)
                {
                    throw new ArgumentException(
                        $"AudioId '{id.Value}' 属于 {entry.Bus}，不能通过 BGM 接口播放。",
                        nameof(id));
                }
            }
            else if (entry.Bus == AudioBus.Bgm)
            {
                throw new ArgumentException(
                    $"BGM '{id.Value}' 必须通过 TryPlayBgmAsync 播放。",
                    nameof(id));
            }

            int sceneHandle = AudioSceneScope.Resolve(entry, in options);
            if (currentDriver.IsOnCooldown(entry))
            {
                return AudioPlayResult.Rejected(AudioPlayRejection.Cooldown);
            }

            cancellationToken.ThrowIfCancellationRequested();
            uint bgmRequestVersion = requiredBus == AudioBus.Bgm
                ? bgmRequestGate.BeginRequest()
                : 0;
            BeginOperation();
            AudioClipCache.AudioClipLease lease = null;
            bool transferred = false;
            CancellationTokenSource linkedCancellation = null;
            CancellationToken operationCancellation;
            if (cancellationToken.CanBeCanceled)
            {
                linkedCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        runtimeCancellation.Token);
                operationCancellation = linkedCancellation.Token;
            }
            else
            {
                operationCancellation = runtimeCancellation.Token;
            }

            try
            {
                lease = await cache.AcquireAsync(
                    entry.Location,
                    entry.LoadMode,
                    sceneHandle,
                    operationCancellation);
                operationCancellation.ThrowIfCancellationRequested();

                if (requiredBus == AudioBus.Bgm &&
                    !bgmRequestGate.IsCurrent(bgmRequestVersion))
                {
                    return AudioPlayResult.Rejected(
                        AudioPlayRejection.Superseded);
                }

                AudioPlayResult result = currentDriver.TryStart(
                    entry,
                    lease,
                    in options,
                    fadeInSeconds);
                if (!result.IsPlaying)
                {
                    return result;
                }

                transferred = true;
                if (requiredBus == AudioBus.Bgm)
                {
                    currentDriver.FadeOutBusExcept(
                        AudioBus.Bgm,
                        result.Handle,
                        fadeOutPreviousBgmSeconds);
                }

                return result;
            }
            finally
            {
                if (!transferred)
                {
                    lease?.Dispose();
                }

                linkedCancellation?.Dispose();
                EndOperation();
            }
        }

        private static void BeginOperation()
        {
            if (pendingOperations == 0)
            {
                operationsIdle = new UniTaskCompletionSource();
            }

            pendingOperations++;
        }

        private static void EndOperation()
        {
            if (pendingOperations <= 0)
            {
                throw new AudioStateException(
                    "GameAudio 异步操作计数发生下溢。");
            }

            pendingOperations--;
            if (pendingOperations == 0)
            {
                operationsIdle.TrySetResult();
            }
        }

        private static void BeginShutdown()
        {
            state = RuntimeState.ShuttingDown;
            bgmRequestGate.Invalidate();
            runtimeCancellation.Cancel();
        }

        private static void CompleteShutdown()
        {
            GameObject rootObject =
                ownedRoot != null ? ownedRoot.gameObject : null;
            DisposeResidentLeases();
            driver.Shutdown();

            config = null;
            cache = null;
            driver = null;
            ownedRoot = null;
            runtimeCancellation.Dispose();
            runtimeCancellation = null;
            operationsIdle = null;
            pendingOperations = 0;
            driverDestroyedUnexpectedly = false;
            state = RuntimeState.None;
            DestroyObject(rootObject);
        }

        private static AudioRuntimeDriver RequireDriver(string api)
        {
            if (state != RuntimeState.Running ||
                driver == null ||
                driverDestroyedUnexpectedly)
            {
                throw new AudioStateException(
                    $"GameAudio.{api} 要求 GameAudio 已成功 InitAsync，" +
                    "且运行节点未被外部破坏。");
            }

            return driver;
        }

        private static ResolvedAudioConfig RequireConfig(string api)
        {
            _ = RequireDriver(api);
            return config;
        }

        private static void SetMixerVolume(
            string parameter,
            float linearVolume)
        {
            if (!float.IsFinite(linearVolume) ||
                linearVolume is < 0f or > 1f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(linearVolume),
                    "音量必须位于 0..1。");
            }

            float decibels = linearVolume <= 0f
                ? MinimumMixerDb
                : Mathf.Log10(linearVolume) * 20f;
            if (!config.Mixer.SetFloat(parameter, decibels))
            {
                throw new InvalidOperationException(
                    $"AudioMixer 未暴露参数：{parameter}。");
            }
        }

        private static float GetMixerVolume(string parameter)
        {
            if (!config.Mixer.GetFloat(parameter, out float decibels))
            {
                throw new InvalidOperationException(
                    $"AudioMixer 未暴露参数：{parameter}。");
            }

            if (decibels <= MinimumMixerDb)
            {
                return 0f;
            }

            return Mathf.Clamp01(Mathf.Pow(10f, decibels / 20f));
        }

        private static void DisposeResidentLeases()
        {
            for (int i = ResidentLeases.Count - 1; i >= 0; i--)
            {
                ResidentLeases[i].Dispose();
            }

            ResidentLeases.Clear();
        }

        private static void ValidateBus(AudioBus bus)
        {
            if (!Enum.IsDefined(typeof(AudioBus), bus))
            {
                throw new ArgumentOutOfRangeException(nameof(bus));
            }
        }

        private static void RequireMainThread(string api)
        {
            if (!PlayerLoopHelper.IsMainThread)
            {
                throw new AudioStateException(
                    $"GameAudio.{api} 只能在 Unity 主线程调用。");
            }
        }

        private static void DestroyObject(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private enum RuntimeState
        {
            None = 0,
            Initializing = 1,
            Running = 2,
            ShuttingDown = 3,
        }
    }
}
