using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Audio
{
    [DisallowMultipleComponent]
    internal sealed class AudioRuntimeDriver : MonoBehaviour
    {
        private const double CacheCollectionInterval = 1d;

        private readonly Dictionary<AudioId, double> lastStartedAt = new();
        private ResolvedAudioConfig config;
        private AudioClipCache cache;
        private VoiceSlot[] voices;
        private double nextCacheCollectionAt;
        private bool initialized;
        private bool shuttingDown;

        internal void Initialize(
            ResolvedAudioConfig resolvedConfig,
            AudioClipCache clipCache)
        {
            if (initialized)
            {
                throw new AudioStateException(
                    "AudioRuntimeDriver.Initialize 不允许重复调用。");
            }

            config = resolvedConfig ??
                throw new ArgumentNullException(nameof(resolvedConfig));
            cache = clipCache ??
                throw new ArgumentNullException(nameof(clipCache));

            voices = new VoiceSlot[
                config.MaxVoices + AudioRuntimeLimits.BgmVoiceCount];
            for (int i = 0; i < voices.Length; i++)
            {
                var voiceObject = new GameObject($"Voice-{i:00}");
                voiceObject.transform.SetParent(transform, false);
                AudioSource source = voiceObject.AddComponent<AudioSource>();
                source.playOnAwake = false;
                voices[i] = new VoiceSlot(
                    i,
                    source,
                    reservedForBgm: i < AudioRuntimeLimits.BgmVoiceCount);
            }

            nextCacheCollectionAt =
                Time.realtimeSinceStartupAsDouble + CacheCollectionInterval;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            initialized = true;
        }

        internal AudioPlayResult TryStart(
            AudioEntry entry,
            AudioClipCache.AudioClipLease lease,
            in AudioPlayOptions options,
            float fadeInSeconds)
        {
            RequireRunning();
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (lease == null)
            {
                throw new ArgumentNullException(nameof(lease));
            }

            if (!options.IsInitialized)
            {
                throw new ArgumentException(
                    "AudioPlayOptions 必须通过构造函数或 Default/At 创建。",
                    nameof(options));
            }

            ValidateFadeSeconds(fadeInSeconds, nameof(fadeInSeconds));
            ValidateSpatialRequest(entry, in options);
            ValidateCombinedPlaybackValues(entry, in options);

            double now = Time.realtimeSinceStartupAsDouble;
            if (IsOnCooldown(entry, now))
            {
                return AudioPlayResult.Rejected(AudioPlayRejection.Cooldown);
            }

            int activeInstances = 0;
            for (int i = 0; i < voices.Length; i++)
            {
                if (voices[i].Active && voices[i].Entry.Id.Equals(entry.Id))
                {
                    activeInstances++;
                }
            }

            VoiceSlot slot;
            if (activeInstances >= entry.MaxInstances)
            {
                slot = SelectVictim(entry, sameAudioOnly: true);
                if (slot == null)
                {
                    return AudioPlayResult.Rejected(
                        AudioPlayRejection.InstanceLimit);
                }

                ReleaseVoice(slot);
            }
            else
            {
                slot = FindFreeVoice(entry.Bus);
                if (slot == null)
                {
                    slot = SelectVictim(entry, sameAudioOnly: false);
                    if (slot == null)
                    {
                        return AudioPlayResult.Rejected(
                            AudioPlayRejection.VoiceLimit);
                    }

                    ReleaseVoice(slot);
                }
            }

            StartVoice(slot, entry, lease, in options, fadeInSeconds, now);
            lastStartedAt[entry.Id] = now;
            return AudioPlayResult.Played(
                new SoundHandle(slot.Index, slot.Generation));
        }

        internal bool TryStop(SoundHandle handle, float fadeOutSeconds)
        {
            RequireRunning();
            ValidateFadeSeconds(fadeOutSeconds, nameof(fadeOutSeconds));
            if (!TryGetActiveVoice(handle, out VoiceSlot slot))
            {
                return false;
            }

            StopVoice(slot, fadeOutSeconds);
            return true;
        }

        internal bool IsOnCooldown(AudioEntry entry)
        {
            RequireRunning();
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            return IsOnCooldown(entry, Time.realtimeSinceStartupAsDouble);
        }

        internal bool IsPlaying(SoundHandle handle)
        {
            RequireRunning();
            return TryGetActiveVoice(handle, out VoiceSlot slot) &&
                IsVoiceAudible(slot.Source);
        }

        internal void FadeOutBusExcept(
            AudioBus bus,
            SoundHandle excluded,
            float fadeOutSeconds)
        {
            RequireRunning();
            ValidateFadeSeconds(fadeOutSeconds, nameof(fadeOutSeconds));
            for (int i = 0; i < voices.Length; i++)
            {
                VoiceSlot slot = voices[i];
                if (!slot.Active ||
                    slot.Entry.Bus != bus ||
                    (slot.Index == excluded.Slot &&
                     slot.Generation == excluded.Generation))
                {
                    continue;
                }

                StopVoice(slot, fadeOutSeconds);
            }
        }

        internal int StopBus(AudioBus bus, float fadeOutSeconds)
        {
            RequireRunning();
            ValidateFadeSeconds(fadeOutSeconds, nameof(fadeOutSeconds));
            int stopped = 0;
            for (int i = 0; i < voices.Length; i++)
            {
                VoiceSlot slot = voices[i];
                if (!slot.Active || slot.Entry.Bus != bus)
                {
                    continue;
                }

                StopVoice(slot, fadeOutSeconds);
                stopped++;
            }

            return stopped;
        }

        internal int GetActiveVoiceCount()
        {
            RequireRunning();
            int count = 0;
            for (int i = 0; i < voices.Length; i++)
            {
                if (voices[i].Active)
                {
                    count++;
                }
            }

            return count;
        }

        internal int GetActiveVoiceCount(AudioBus bus)
        {
            RequireRunning();
            int count = 0;
            for (int i = 0; i < voices.Length; i++)
            {
                if (voices[i].Active && voices[i].Entry.Bus == bus)
                {
                    count++;
                }
            }

            return count;
        }

        internal void Shutdown()
        {
            RequireRunning();
            shuttingDown = true;
            try
            {
                for (int i = 0; i < voices.Length; i++)
                {
                    if (voices[i].Active)
                    {
                        ReleaseVoice(voices[i]);
                    }
                }

                cache.Dispose();
                SceneManager.sceneUnloaded -= OnSceneUnloaded;
                initialized = false;
            }
            finally
            {
                shuttingDown = false;
            }
        }

        private void Update()
        {
            if (!initialized || shuttingDown)
            {
                return;
            }

            float deltaTime = Time.unscaledDeltaTime;
            for (int i = 0; i < voices.Length; i++)
            {
                VoiceSlot slot = voices[i];
                if (!slot.Active)
                {
                    continue;
                }

                if (!slot.Source.loop && !IsVoiceAudible(slot.Source))
                {
                    ReleaseVoice(slot);
                    continue;
                }

                if (slot.Fading)
                {
                    slot.FadeElapsed += deltaTime;
                    float progress = Mathf.Clamp01(
                        slot.FadeElapsed / slot.FadeDuration);
                    slot.Source.volume = Mathf.Lerp(
                        slot.FadeFrom,
                        slot.FadeTarget,
                        progress);
                    if (progress >= 1f)
                    {
                        slot.Fading = false;
                        if (slot.StopAfterFade)
                        {
                            ReleaseVoice(slot);
                            continue;
                        }
                    }
                }

            }

            double now = Time.realtimeSinceStartupAsDouble;
            if (now >= nextCacheCollectionAt)
            {
                cache.CollectExpired(now);
                nextCacheCollectionAt = now + CacheCollectionInterval;
            }
        }

        private void OnDestroy()
        {
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            if (initialized && !shuttingDown)
            {
                GameAudio.NotifyDriverDestroyed(this);
            }
        }

        private void OnSceneUnloaded(Scene scene)
        {
            if (initialized && !shuttingDown)
            {
                cache.UnloadSceneAssets(scene.handle);
            }
        }

        private void StartVoice(
            VoiceSlot slot,
            AudioEntry entry,
            AudioClipCache.AudioClipLease lease,
            in AudioPlayOptions options,
            float fadeInSeconds,
            double now)
        {
            uint generation = slot.Generation + 1;
            if (generation == 0)
            {
                generation = 1;
            }

            slot.Generation = generation;
            slot.Entry = entry;
            slot.Lease = lease;
            slot.StartedAt = now;
            slot.Active = true;

            AudioSource source = slot.Source;
            source.Stop();
            source.clip = lease.Clip;
            source.outputAudioMixerGroup = config.Groups[entry.Bus];
            source.loop = entry.Loop;
            source.priority = entry.Priority;
            source.pitch = entry.Pitch * options.PitchScale;
            source.spatialBlend = entry.SpatialBlend;
            source.minDistance = entry.MinDistance;
            source.maxDistance = entry.MaxDistance;
            source.rolloffMode = entry.RolloffMode;
            source.ignoreListenerPause = entry.Bus == AudioBus.Ui;
            source.transform.position = options.UseWorldPosition
                ? options.WorldPosition
                : transform.position;

            float targetVolume = entry.Volume * options.VolumeScale;
            if (fadeInSeconds > 0f)
            {
                source.volume = 0f;
                BeginFade(slot, targetVolume, fadeInSeconds, false);
            }
            else
            {
                source.volume = targetVolume;
                ClearFade(slot);
            }

            source.Play();
        }

        private VoiceSlot FindFreeVoice(AudioBus bus)
        {
            for (int i = 0; i < voices.Length; i++)
            {
                VoiceSlot slot = voices[i];
                if (!slot.Active && IsCompatibleVoice(slot, bus))
                {
                    return slot;
                }
            }

            return null;
        }

        private VoiceSlot SelectVictim(
            AudioEntry incoming,
            bool sameAudioOnly)
        {
            if (incoming.OverflowPolicy == AudioOverflowPolicy.Reject)
            {
                return null;
            }

            VoiceSlot candidate = null;
            for (int i = 0; i < voices.Length; i++)
            {
                VoiceSlot current = voices[i];
                if (!current.Active ||
                    !IsCompatibleVoice(current, incoming.Bus) ||
                    (!sameAudioOnly &&
                     GetBusProtection(current.Entry.Bus) >
                     GetBusProtection(incoming.Bus)) ||
                    (sameAudioOnly && !current.Entry.Id.Equals(incoming.Id)))
                {
                    continue;
                }

                if (incoming.OverflowPolicy ==
                    AudioOverflowPolicy.StopOldest)
                {
                    if (candidate == null ||
                        current.StartedAt < candidate.StartedAt)
                    {
                        candidate = current;
                    }

                    continue;
                }

                if (candidate == null ||
                    current.Entry.Priority > candidate.Entry.Priority ||
                    (current.Entry.Priority == candidate.Entry.Priority &&
                     current.StartedAt < candidate.StartedAt))
                {
                    candidate = current;
                }
            }

            if (incoming.OverflowPolicy ==
                    AudioOverflowPolicy.StopLowestPriority &&
                candidate != null &&
                candidate.Entry.Priority < incoming.Priority)
            {
                return null;
            }

            return candidate;
        }

        private bool IsOnCooldown(AudioEntry entry, double now) =>
            entry.CooldownSeconds > 0f &&
            lastStartedAt.TryGetValue(entry.Id, out double lastStarted) &&
            now - lastStarted < entry.CooldownSeconds;

        private static bool IsVoiceAudible(AudioSource source) =>
            source.isPlaying ||
            (AudioListener.pause && !source.ignoreListenerPause);

        private static bool IsCompatibleVoice(
            VoiceSlot slot,
            AudioBus bus) =>
            slot.ReservedForBgm == (bus == AudioBus.Bgm);

        private static int GetBusProtection(AudioBus bus) =>
            bus switch
            {
                AudioBus.Bgm => 3,
                AudioBus.Voice => 2,
                AudioBus.Ui => 1,
                AudioBus.Sfx => 0,
                _ => throw new ArgumentOutOfRangeException(nameof(bus)),
            };

        private void StopVoice(VoiceSlot slot, float fadeOutSeconds)
        {
            if (fadeOutSeconds <= 0f)
            {
                ReleaseVoice(slot);
                return;
            }

            BeginFade(slot, 0f, fadeOutSeconds, true);
        }

        private static void BeginFade(
            VoiceSlot slot,
            float target,
            float duration,
            bool stopAfterFade)
        {
            slot.Fading = true;
            slot.FadeFrom = slot.Source.volume;
            slot.FadeTarget = target;
            slot.FadeDuration = duration;
            slot.FadeElapsed = 0f;
            slot.StopAfterFade = stopAfterFade;
        }

        private static void ClearFade(VoiceSlot slot)
        {
            slot.Fading = false;
            slot.FadeFrom = 0f;
            slot.FadeTarget = 0f;
            slot.FadeDuration = 0f;
            slot.FadeElapsed = 0f;
            slot.StopAfterFade = false;
        }

        private static void ReleaseVoice(VoiceSlot slot)
        {
            slot.Source.Stop();
            slot.Source.clip = null;
            slot.Source.outputAudioMixerGroup = null;
            slot.Lease.Dispose();
            slot.Lease = null;
            slot.Entry = null;
            slot.Active = false;
            ClearFade(slot);
        }

        private bool TryGetActiveVoice(
            SoundHandle handle,
            out VoiceSlot slot)
        {
            slot = null;
            if (!handle.IsValid ||
                handle.Slot >= voices.Length)
            {
                return false;
            }

            VoiceSlot candidate = voices[handle.Slot];
            if (!candidate.Active ||
                candidate.Generation != handle.Generation)
            {
                return false;
            }

            slot = candidate;
            return true;
        }

        private void RequireRunning()
        {
            if (!initialized || shuttingDown)
            {
                throw new AudioStateException(
                    "AudioRuntimeDriver 当前不可用。");
            }
        }

        private static void ValidateFadeSeconds(float value, string parameter)
        {
            if (!float.IsFinite(value) || value < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    parameter,
                    "淡入淡出时长必须是大于等于 0 的有限值。");
            }
        }

        private static void ValidateSpatialRequest(
            AudioEntry entry,
            in AudioPlayOptions options)
        {
            bool entryIsSpatial = entry.SpatialBlend > 0f;
            if (entryIsSpatial != options.UseWorldPosition)
            {
                throw new ArgumentException(
                    entryIsSpatial
                        ? $"3D 音效 '{entry.Id.Value}' 必须提供世界坐标。"
                        : $"2D 音效 '{entry.Id.Value}' 不接受世界坐标。",
                    nameof(options));
            }
        }

        private static void ValidateCombinedPlaybackValues(
            AudioEntry entry,
            in AudioPlayOptions options)
        {
            float volume = entry.Volume * options.VolumeScale;
            if (!float.IsFinite(volume) || volume is < 0f or > 1f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"音效 '{entry.Id.Value}' 的最终音量必须位于 0..1。");
            }

            float pitch = entry.Pitch * options.PitchScale;
            if (!float.IsFinite(pitch) || pitch is < 0.01f or > 3f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"音效 '{entry.Id.Value}' 的最终音高必须位于 0.01..3。");
            }
        }

        private sealed class VoiceSlot
        {
            internal VoiceSlot(
                int index,
                AudioSource source,
                bool reservedForBgm)
            {
                Index = index;
                Source = source;
                ReservedForBgm = reservedForBgm;
            }

            internal int Index { get; }
            internal AudioSource Source { get; }
            internal bool ReservedForBgm { get; }
            internal uint Generation { get; set; }
            internal AudioEntry Entry { get; set; }
            internal AudioClipCache.AudioClipLease Lease { get; set; }
            internal double StartedAt { get; set; }
            internal bool Active { get; set; }
            internal bool Fading { get; set; }
            internal float FadeFrom { get; set; }
            internal float FadeTarget { get; set; }
            internal float FadeDuration { get; set; }
            internal float FadeElapsed { get; set; }
            internal bool StopAfterFade { get; set; }
        }
    }
}
