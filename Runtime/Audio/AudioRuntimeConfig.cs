using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace Game.Audio
{
    [Serializable]
    public sealed class AudioEntry
    {
        [SerializeField] private string id;
        [SerializeField] private string location;
        [SerializeField] private AudioBus bus = AudioBus.Sfx;
        [SerializeField, Min(1)] private int maxInstances = 4;
        [SerializeField, Min(0f)] private float cooldownSeconds;
        [SerializeField] private AudioOverflowPolicy overflowPolicy =
            AudioOverflowPolicy.Reject;
        [SerializeField, Range(0, 256)] private int priority = 128;
        [SerializeField, Range(0f, 1f)] private float volume = 1f;
        [SerializeField, Range(0.01f, 3f)] private float pitch = 1f;
        [SerializeField] private bool loop;
        [SerializeField, Range(0f, 1f)] private float spatialBlend;
        [SerializeField, Min(0f)] private float minDistance = 1f;
        [SerializeField, Min(0.01f)] private float maxDistance = 50f;
        [SerializeField] private AudioRolloffMode rolloffMode =
            AudioRolloffMode.Logarithmic;
        [SerializeField] private AudioLoadMode loadMode = AudioLoadMode.OnDemand;

        public AudioEntry(
            string id,
            string location,
            AudioBus bus = AudioBus.Sfx,
            int maxInstances = 4,
            float cooldownSeconds = 0f,
            AudioOverflowPolicy overflowPolicy = AudioOverflowPolicy.Reject,
            int priority = 128,
            float volume = 1f,
            float pitch = 1f,
            bool loop = false,
            float spatialBlend = 0f,
            float minDistance = 1f,
            float maxDistance = 50f,
            AudioRolloffMode rolloffMode = AudioRolloffMode.Logarithmic,
            AudioLoadMode loadMode = AudioLoadMode.OnDemand)
        {
            this.id = id;
            this.location = location;
            this.bus = bus;
            this.maxInstances = maxInstances;
            this.cooldownSeconds = cooldownSeconds;
            this.overflowPolicy = overflowPolicy;
            this.priority = priority;
            this.volume = volume;
            this.pitch = pitch;
            this.loop = loop;
            this.spatialBlend = spatialBlend;
            this.minDistance = minDistance;
            this.maxDistance = maxDistance;
            this.rolloffMode = rolloffMode;
            this.loadMode = loadMode;
        }

        public AudioId Id => new(id);
        public string Location => location;
        public AudioBus Bus => bus;
        public int MaxInstances => maxInstances;
        public float CooldownSeconds => cooldownSeconds;
        public AudioOverflowPolicy OverflowPolicy => overflowPolicy;
        public int Priority => priority;
        public float Volume => volume;
        public float Pitch => pitch;
        public bool Loop => loop;
        public float SpatialBlend => spatialBlend;
        public float MinDistance => minDistance;
        public float MaxDistance => maxDistance;
        public AudioRolloffMode RolloffMode => rolloffMode;
        public AudioLoadMode LoadMode => loadMode;

        public void Validate()
        {
            _ = new AudioId(id);

            if (string.IsNullOrWhiteSpace(location))
            {
                throw new InvalidOperationException(
                    $"AudioEntry '{id}' 的 YooAsset location 不能为空。");
            }

            if (!Enum.IsDefined(typeof(AudioBus), bus))
            {
                throw new InvalidOperationException(
                    $"AudioEntry '{id}' 的 AudioBus 无效：{bus}。");
            }

            if (maxInstances <= 0)
            {
                throw new InvalidOperationException(
                    $"AudioEntry '{id}' 的 maxInstances 必须大于 0。");
            }

            if (!float.IsFinite(cooldownSeconds) || cooldownSeconds < 0f)
            {
                throw new InvalidOperationException(
                    $"AudioEntry '{id}' 的 cooldownSeconds 必须是大于等于 0 的有限值。");
            }

            if (!Enum.IsDefined(typeof(AudioOverflowPolicy), overflowPolicy))
            {
                throw new InvalidOperationException(
                    $"AudioEntry '{id}' 的溢出策略无效：{overflowPolicy}。");
            }

            if (priority is < 0 or > 256)
            {
                throw new InvalidOperationException(
                    $"AudioEntry '{id}' 的 priority 必须位于 0..256。");
            }

            if (!float.IsFinite(volume) || volume is < 0f or > 1f)
            {
                throw new InvalidOperationException(
                    $"AudioEntry '{id}' 的 volume 必须位于 0..1。");
            }

            if (!float.IsFinite(pitch) || pitch is < 0.01f or > 3f)
            {
                throw new InvalidOperationException(
                    $"AudioEntry '{id}' 的 pitch 必须位于 0.01..3。");
            }

            if (!float.IsFinite(spatialBlend) ||
                spatialBlend is < 0f or > 1f)
            {
                throw new InvalidOperationException(
                    $"AudioEntry '{id}' 的 spatialBlend 必须位于 0..1。");
            }

            if (!float.IsFinite(minDistance) || minDistance < 0f ||
                !float.IsFinite(maxDistance) || maxDistance <= minDistance)
            {
                throw new InvalidOperationException(
                    $"AudioEntry '{id}' 要求 0 <= minDistance < maxDistance。");
            }

            if (!Enum.IsDefined(typeof(AudioLoadMode), loadMode))
            {
                throw new InvalidOperationException(
                    $"AudioEntry '{id}' 的 loadMode 无效：{loadMode}。");
            }

            if (!Enum.IsDefined(typeof(AudioRolloffMode), rolloffMode))
            {
                throw new InvalidOperationException(
                    $"AudioEntry '{id}' 的 rolloffMode 无效：{rolloffMode}。");
            }

            if (bus == AudioBus.Bgm && spatialBlend > 0f)
            {
                throw new InvalidOperationException(
                    $"BGM '{id}' 必须配置为 2D 音频。");
            }
        }
    }

    [CreateAssetMenu(
        fileName = "AudioRuntimeConfig",
        menuName = "Game/Audio/Runtime Config")]
    public sealed class AudioRuntimeConfig : ScriptableObject
    {
        [Header("Capacity")]
        [SerializeField, Min(1)] private int maxVoices = 32;
        [SerializeField, Min(0f)] private float cacheRetentionSeconds = 30f;

        [Header("Mixer")]
        [SerializeField] private AudioMixer mixer;
        [SerializeField] private string bgmGroupPath = "Master/BGM";
        [SerializeField] private string sfxGroupPath = "Master/SFX";
        [SerializeField] private string uiGroupPath = "Master/UI";
        [SerializeField] private string voiceGroupPath = "Master/Voice";

        [Header("Exposed Volume Parameters")]
        [SerializeField] private string masterVolumeParameter = "MasterVolume";
        [SerializeField] private string bgmVolumeParameter = "BgmVolume";
        [SerializeField] private string sfxVolumeParameter = "SfxVolume";
        [SerializeField] private string uiVolumeParameter = "UiVolume";
        [SerializeField] private string voiceVolumeParameter = "VoiceVolume";

        [Header("Catalog")]
        [SerializeField] private List<AudioEntry> entries = new();

        internal ResolvedAudioConfig Resolve()
        {
            if (maxVoices is <= 0 or > AudioRuntimeLimits.MaximumVoiceCapacity)
            {
                throw new InvalidOperationException(
                    $"AudioRuntimeConfig.maxVoices 必须位于 1..{AudioRuntimeLimits.MaximumVoiceCapacity}。");
            }

            if (!float.IsFinite(cacheRetentionSeconds) ||
                cacheRetentionSeconds < 0f)
            {
                throw new InvalidOperationException(
                    "AudioRuntimeConfig.cacheRetentionSeconds 必须是大于等于 0 的有限值。");
            }

            if (mixer == null)
            {
                throw new InvalidOperationException(
                    "AudioRuntimeConfig 必须显式配置 AudioMixer。");
            }

            if (entries.Count == 0)
            {
                throw new InvalidOperationException(
                    "AudioRuntimeConfig.entries 不能为空。");
            }

            var catalog = new Dictionary<AudioId, AudioEntry>(entries.Count);
            var loadModesByLocation =
                new Dictionary<string, AudioLoadMode>(StringComparer.Ordinal);
            for (int i = 0; i < entries.Count; i++)
            {
                AudioEntry entry = entries[i] ??
                    throw new InvalidOperationException(
                        $"AudioRuntimeConfig.entries[{i}] 不能为 null。");
                entry.Validate();
                if (!catalog.TryAdd(entry.Id, entry))
                {
                    throw new InvalidOperationException(
                        $"AudioRuntimeConfig 存在重复 AudioId：{entry.Id}。");
                }

                if (entry.Bus == AudioBus.Bgm)
                {
                    if (entry.MaxInstances != AudioRuntimeLimits.BgmVoiceCount)
                    {
                        throw new InvalidOperationException(
                            $"BGM '{entry.Id}' 的 maxInstances 必须为 " +
                            $"{AudioRuntimeLimits.BgmVoiceCount}，以支持交叉淡化。");
                    }
                }
                else if (entry.MaxInstances > maxVoices)
                {
                    throw new InvalidOperationException(
                        $"AudioEntry '{entry.Id}' 的 maxInstances " +
                        $"不能超过常规声道容量 {maxVoices}。");
                }

                if (loadModesByLocation.TryGetValue(
                        entry.Location,
                        out AudioLoadMode configuredMode) &&
                    configuredMode != entry.LoadMode)
                {
                    throw new InvalidOperationException(
                        $"同一 location '{entry.Location}' 不能配置不同的 loadMode。");
                }

                loadModesByLocation[entry.Location] = entry.LoadMode;
            }

            var groups = new Dictionary<AudioBus, AudioMixerGroup>(4)
            {
                [AudioBus.Bgm] = ResolveGroup(bgmGroupPath, AudioBus.Bgm),
                [AudioBus.Sfx] = ResolveGroup(sfxGroupPath, AudioBus.Sfx),
                [AudioBus.Ui] = ResolveGroup(uiGroupPath, AudioBus.Ui),
                [AudioBus.Voice] = ResolveGroup(voiceGroupPath, AudioBus.Voice),
            };

            string masterParameter = RequireParameter(
                masterVolumeParameter,
                nameof(masterVolumeParameter));
            var volumeParameters = new Dictionary<AudioBus, string>(4)
            {
                [AudioBus.Bgm] = RequireParameter(
                    bgmVolumeParameter,
                    nameof(bgmVolumeParameter)),
                [AudioBus.Sfx] = RequireParameter(
                    sfxVolumeParameter,
                    nameof(sfxVolumeParameter)),
                [AudioBus.Ui] = RequireParameter(
                    uiVolumeParameter,
                    nameof(uiVolumeParameter)),
                [AudioBus.Voice] = RequireParameter(
                    voiceVolumeParameter,
                    nameof(voiceVolumeParameter)),
            };
            ValidateMixerParameters(masterParameter, volumeParameters);

            return new ResolvedAudioConfig(
                maxVoices,
                cacheRetentionSeconds,
                mixer,
                masterParameter,
                catalog,
                groups,
                volumeParameters);
        }

        private AudioMixerGroup ResolveGroup(string path, AudioBus bus)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException(
                    $"{bus} 的 Mixer Group path 不能为空。");
            }

            AudioMixerGroup[] matches = mixer.FindMatchingGroups(path);
            if (matches.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Mixer Group path '{path}' 必须精确匹配一个分组，实际为 {matches.Length}。");
            }

            return matches[0];
        }

        private static string RequireParameter(string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"AudioRuntimeConfig.{field} 不能为空。");
            }

            return value;
        }

        private void ValidateMixerParameters(
            string masterParameter,
            IReadOnlyDictionary<AudioBus, string> volumeParameters)
        {
            var names = new HashSet<string>(StringComparer.Ordinal)
            {
                masterParameter,
            };

            ValidateMixerParameterExists(masterParameter);
            foreach (KeyValuePair<AudioBus, string> pair in volumeParameters)
            {
                if (!names.Add(pair.Value))
                {
                    throw new InvalidOperationException(
                        $"AudioMixer 音量参数重复：{pair.Value}。");
                }

                ValidateMixerParameterExists(pair.Value);
            }
        }

        private void ValidateMixerParameterExists(string parameter)
        {
            if (!mixer.GetFloat(parameter, out _))
            {
                throw new InvalidOperationException(
                    $"AudioMixer 未暴露参数：{parameter}。");
            }
        }
    }

    internal sealed class ResolvedAudioConfig
    {
        internal ResolvedAudioConfig(
            int maxVoices,
            float cacheRetentionSeconds,
            AudioMixer mixer,
            string masterVolumeParameter,
            IReadOnlyDictionary<AudioId, AudioEntry> catalog,
            IReadOnlyDictionary<AudioBus, AudioMixerGroup> groups,
            IReadOnlyDictionary<AudioBus, string> volumeParameters)
        {
            MaxVoices = maxVoices;
            CacheRetentionSeconds = cacheRetentionSeconds;
            Mixer = mixer;
            MasterVolumeParameter = masterVolumeParameter;
            Catalog = catalog;
            Groups = groups;
            VolumeParameters = volumeParameters;
        }

        internal int MaxVoices { get; }
        internal float CacheRetentionSeconds { get; }
        internal AudioMixer Mixer { get; }
        internal string MasterVolumeParameter { get; }
        internal IReadOnlyDictionary<AudioId, AudioEntry> Catalog { get; }
        internal IReadOnlyDictionary<AudioBus, AudioMixerGroup> Groups { get; }
        internal IReadOnlyDictionary<AudioBus, string> VolumeParameters { get; }
    }
}
