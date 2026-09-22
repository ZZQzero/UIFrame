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

        public static bool IsInited => state == RuntimeState.Running;

        public static async UniTask InitAsync(
            ResourcePackage package,
            AudioRuntimeConfig runtimeConfig,
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
                    await cache.WaitForIdleAsync();
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
                null,
                options,
                requiredBus: null,
                fadeInSeconds: 0f,
                fadeOutPreviousBgmSeconds: 0f,
                cancellationToken);
        }

        public static UniTask<AudioPlayResult> TryPlayLocationAsync(
            string location,
            AudioBus bus = AudioBus.Sfx,
            CancellationToken cancellationToken = default) =>
            TryPlayLocationAsync(
                location, bus, AudioPlayOptions.Default, cancellationToken);

        public static UniTask<AudioPlayResult> TryPlayLocationAsync(
            string location,
            AudioBus bus,
            AudioPlayOptions options,
            CancellationToken cancellationToken = default)
        {
            if (bus == AudioBus.Bgm)
            {
                throw new ArgumentException(
                    "BGM 地址必须通过 TryPlayBgmLocationAsync 播放。",
                    nameof(bus));
            }

            return TryPlayInternalAsync(
                nameof(TryPlayLocationAsync),
                default,
                ResolveLocationEntry(location, bus),
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
                null,
                options,
                AudioBus.Bgm,
                crossFadeSeconds,
                crossFadeSeconds,
                cancellationToken);
        }

        public static UniTask<AudioPlayResult> TryPlayBgmLocationAsync(
            string location,
            float crossFadeSeconds = 0.5f,
            CancellationToken cancellationToken = default) =>
            TryPlayBgmLocationAsync(
                location,
                AudioPlayOptions.Default,
                crossFadeSeconds,
                cancellationToken);

        public static UniTask<AudioPlayResult> TryPlayBgmLocationAsync(
            string location,
            AudioPlayOptions options,
            float crossFadeSeconds = 0.5f,
            CancellationToken cancellationToken = default) =>
            TryPlayInternalAsync(
                nameof(TryPlayBgmLocationAsync),
                default,
                ResolveLocationEntry(location, AudioBus.Bgm),
                options,
                AudioBus.Bgm,
                crossFadeSeconds,
                crossFadeSeconds,
                cancellationToken);

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
            ResolvedAudioConfig current = RequireConfig(
                nameof(SetBusVolume));
            SetMixerVolume(
                current.VolumeParameters[bus],
                linearVolume);
        }

        public static float GetBusVolume(AudioBus bus)
        {
            RequireMainThread(nameof(GetBusVolume));
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
        }

        private static async UniTask<AudioPlayResult> TryPlayInternalAsync(
            string api,
            AudioId id,
            AudioEntry locationEntry,
            AudioPlayOptions options,
            AudioBus? requiredBus,
            float fadeInSeconds,
            float fadeOutPreviousBgmSeconds,
            CancellationToken cancellationToken)
        {
            RequireMainThread(api);
            AudioRuntimeDriver currentDriver = RequireDriver(api);
            if (locationEntry == null && !id.IsValid)
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

            if (requiredBus == AudioBus.Bgm &&
                (!float.IsFinite(fadeInSeconds) || fadeInSeconds < 0f ||
                 !float.IsFinite(fadeOutPreviousBgmSeconds) ||
                 fadeOutPreviousBgmSeconds < 0f))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fadeInSeconds),
                    "淡入淡出时长必须是大于等于 0 的有限值。");
            }

            AudioEntry entry = locationEntry;
            if (entry == null && !config.Catalog.TryGetValue(id, out entry))
            {
                throw new KeyNotFoundException(
                    $"AudioRuntimeConfig 未配置 AudioId：{id.Value}。");
            }

            if (requiredBus.HasValue)
            {
                if (entry.Bus != requiredBus.Value)
                {
                    throw new ArgumentException(
                        $"音频 '{entry.Id.Value}' 属于 {entry.Bus}，不能通过 BGM 接口播放。",
                        nameof(id));
                }
            }
            else if (entry.Bus == AudioBus.Bgm)
            {
                throw new ArgumentException(
                    $"BGM '{entry.Id.Value}' 必须通过 BGM 接口播放。",
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
                currentDriver = RequireDriver(api);

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

        private static AudioEntry ResolveLocationEntry(
            string location,
            AudioBus bus)
        {
            RequireMainThread(nameof(ResolveLocationEntry));
            ResolvedAudioConfig current = RequireConfig(
                nameof(ResolveLocationEntry));
            if (string.IsNullOrWhiteSpace(location))
            {
                throw new ArgumentException(
                    "音频 YooAsset location 不能为空。", nameof(location));
            }

            if (!Enum.IsDefined(typeof(AudioBus), bus))
            {
                throw new ArgumentOutOfRangeException(nameof(bus));
            }

            AudioEntry match = null;
            AudioLoadMode loadMode = AudioLoadMode.OnDemand;
            foreach (AudioEntry candidate in current.Catalog.Values)
            {
                if (!string.Equals(candidate.Location, location, StringComparison.Ordinal))
                {
                    continue;
                }

                loadMode = candidate.LoadMode;
                if (candidate.Bus != bus)
                {
                    continue;
                }

                if (match != null)
                {
                    throw new InvalidOperationException(
                        $"location '{location}' 在 {bus} 总线配置了多个条目，请使用 AudioId。");
                }

                match = candidate;
            }

            if (match != null)
            {
                return match;
            }

            var entry = new AudioEntry(
                $"location:{bus}:{location}",
                location,
                bus,
                maxInstances: bus == AudioBus.Bgm
                    ? AudioRuntimeLimits.BgmVoiceCount
                    : Math.Min(4, current.MaxVoices),
                overflowPolicy: AudioOverflowPolicy.StopOldest,
                loop: bus == AudioBus.Bgm,
                loadMode: loadMode);
            entry.Validate();
            return entry;
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
            state = RuntimeState.None;
            DestroyObject(rootObject);
        }

        private static AudioRuntimeDriver RequireDriver(string api)
        {
            if (state != RuntimeState.Running)
            {
                throw new AudioStateException(
                    $"GameAudio.{api} 要求 GameAudio 已成功 InitAsync。");
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
