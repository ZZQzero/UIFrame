using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Audio
{
    public enum AudioBus
    {
        Bgm = 0,
        Sfx = 1,
        Ui = 2,
        Voice = 3,
    }

    public enum AudioOverflowPolicy
    {
        Reject = 0,
        StopOldest = 1,
        StopLowestPriority = 2,
    }

    public enum AudioLoadMode
    {
        OnDemand = 0,
        Scene = 1,
        Resident = 2,
    }

    public enum AudioPlayRejection
    {
        None = 0,
        Cooldown = 1,
        InstanceLimit = 2,
        VoiceLimit = 3,
        Superseded = 4,
    }

    [Serializable]
    public struct AudioId : IEquatable<AudioId>
    {
        [SerializeField] private string value;

        public AudioId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "AudioId 不能为空、空白或未初始化。",
                    nameof(value));
            }

            this.value = value;
        }

        public bool IsValid => !string.IsNullOrWhiteSpace(value);
        public string Value => IsValid
            ? value
            : throw new InvalidOperationException("当前 AudioId 未初始化。");

        public bool Equals(AudioId other) =>
            string.Equals(value, other.value, StringComparison.Ordinal);

        public override bool Equals(object obj) =>
            obj is AudioId other && Equals(other);

        public override int GetHashCode() =>
            value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);

        public override string ToString() => value ?? "<invalid>";

        public static bool operator ==(AudioId left, AudioId right) =>
            left.Equals(right);

        public static bool operator !=(AudioId left, AudioId right) =>
            !left.Equals(right);
    }

    public readonly struct SoundHandle : IEquatable<SoundHandle>
    {
        private readonly int slot;
        private readonly uint generation;

        internal SoundHandle(int slot, uint generation)
        {
            this.slot = slot;
            this.generation = generation;
        }

        internal int Slot => slot;
        internal uint Generation => generation;
        public bool IsValid => slot >= 0 && generation != 0;

        public bool Equals(SoundHandle other) =>
            slot == other.slot && generation == other.generation;

        public override bool Equals(object obj) =>
            obj is SoundHandle other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(slot, generation);

        public override string ToString() =>
            IsValid ? $"SoundHandle({slot}:{generation})" : "SoundHandle(invalid)";

        public static bool operator ==(SoundHandle left, SoundHandle right) =>
            left.Equals(right);

        public static bool operator !=(SoundHandle left, SoundHandle right) =>
            !left.Equals(right);

        public static SoundHandle Invalid => new(-1, 0);
    }

    public readonly struct AudioPlayOptions
    {
        public AudioPlayOptions(
            float volumeScale,
            float pitchScale,
            bool useWorldPosition = false,
            Vector3 worldPosition = default)
        {
            if (!float.IsFinite(volumeScale) || volumeScale < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(volumeScale),
                    "音量倍率必须是大于等于 0 的有限值。");
            }

            if (!float.IsFinite(pitchScale) || pitchScale <= 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pitchScale),
                    "音高倍率必须是大于 0 的有限值。");
            }

            if (useWorldPosition &&
                (!float.IsFinite(worldPosition.x) ||
                 !float.IsFinite(worldPosition.y) ||
                 !float.IsFinite(worldPosition.z)))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(worldPosition),
                    "世界坐标的每个分量都必须是有限值。");
            }

            VolumeScale = volumeScale;
            PitchScale = pitchScale;
            UseWorldPosition = useWorldPosition;
            WorldPosition = worldPosition;
            HasSceneScope = false;
            SceneHandle = 0;
            IsInitialized = true;
        }

        private AudioPlayOptions(
            float volumeScale,
            float pitchScale,
            bool useWorldPosition,
            Vector3 worldPosition,
            bool hasSceneScope,
            int sceneHandle)
            : this(volumeScale, pitchScale, useWorldPosition, worldPosition)
        {
            HasSceneScope = hasSceneScope;
            SceneHandle = sceneHandle;
        }

        public float VolumeScale { get; }
        public float PitchScale { get; }
        public bool UseWorldPosition { get; }
        public Vector3 WorldPosition { get; }
        internal bool HasSceneScope { get; }
        internal int SceneHandle { get; }
        internal bool IsInitialized { get; }

        public static AudioPlayOptions Default =>
            new(1f, 1f);

        public static AudioPlayOptions At(Vector3 worldPosition) =>
            new(1f, 1f, true, worldPosition);

        public AudioPlayOptions WithVolume(float volumeScale)
        {
            RequireInitialized();
            return new AudioPlayOptions(
                volumeScale,
                PitchScale,
                UseWorldPosition,
                WorldPosition,
                HasSceneScope,
                SceneHandle);
        }

        public AudioPlayOptions WithPitch(float pitchScale)
        {
            RequireInitialized();
            return new AudioPlayOptions(
                VolumeScale,
                pitchScale,
                UseWorldPosition,
                WorldPosition,
                HasSceneScope,
                SceneHandle);
        }

        /// <summary>
        /// 将 Scene 加载模式的音频缓存归属于指定场景。
        /// 未显式指定时，播放接口使用调用时的 Active Scene。
        /// </summary>
        public AudioPlayOptions InScene(Scene scene)
        {
            RequireInitialized();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                throw new ArgumentException(
                    "音频场景作用域必须是已加载的有效 Scene。",
                    nameof(scene));
            }

            return new AudioPlayOptions(
                VolumeScale,
                PitchScale,
                UseWorldPosition,
                WorldPosition,
                true,
                scene.handle);
        }

        private void RequireInitialized()
        {
            if (!IsInitialized)
            {
                throw new InvalidOperationException(
                    "AudioPlayOptions 必须先通过构造函数或 Default/At 创建。");
            }
        }
    }

    public readonly struct AudioPlayResult
    {
        private AudioPlayResult(
            SoundHandle handle,
            AudioPlayRejection rejection)
        {
            Handle = handle;
            Rejection = rejection;
        }

        public SoundHandle Handle { get; }
        public AudioPlayRejection Rejection { get; }
        public bool IsPlaying => Rejection == AudioPlayRejection.None;

        internal static AudioPlayResult Played(SoundHandle handle) =>
            new(handle, AudioPlayRejection.None);

        internal static AudioPlayResult Rejected(AudioPlayRejection rejection) =>
            new(SoundHandle.Invalid, rejection);
    }

    public sealed class AudioStateException : InvalidOperationException
    {
        public AudioStateException(string message) : base(message)
        {
        }
    }

    public sealed class AudioPlaybackRejectedException : InvalidOperationException
    {
        public AudioPlaybackRejectedException(
            AudioId id,
            AudioPlayRejection rejection)
            : base($"音效 {id} 的播放请求被策略拒绝：{rejection}。")
        {
            AudioId = id;
            Rejection = rejection;
        }

        public AudioId AudioId { get; }
        public AudioPlayRejection Rejection { get; }
    }

    internal static class AudioRuntimeLimits
    {
        internal const int BgmVoiceCount = 2;
        internal const int MaximumVoiceCapacity = 128;
    }

    internal static class AudioSceneScope
    {
        internal static int RequireActiveHandle()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                throw new AudioStateException(
                    "Scene 加载模式要求当前 Active Scene 已加载且有效。");
            }

            return scene.handle;
        }

        internal static bool IsLoaded(int sceneHandle)
        {
            if (sceneHandle == 0)
            {
                return false;
            }

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene loadedScene = SceneManager.GetSceneAt(i);
                if (loadedScene.handle == sceneHandle &&
                    loadedScene.isLoaded)
                {
                    return true;
                }
            }

            return false;
        }

        internal static int Resolve(
            AudioEntry entry,
            in AudioPlayOptions options)
        {
            if (entry.LoadMode != AudioLoadMode.Scene)
            {
                if (options.HasSceneScope)
                {
                    throw new ArgumentException(
                        $"音效 '{entry.Id}' 不是 Scene 加载模式，不能指定 Scene 作用域。",
                        nameof(options));
                }

                return 0;
            }

            if (!options.HasSceneScope)
            {
                return RequireActiveHandle();
            }

            if (!IsLoaded(options.SceneHandle))
            {
                throw new AudioStateException(
                    $"音效 '{entry.Id}' 指定的 Scene 已不再处于加载状态。");
            }

            return options.SceneHandle;
        }

        internal static void Validate(
            AudioLoadMode loadMode,
            int sceneHandle)
        {
            if (loadMode == AudioLoadMode.Scene)
            {
                if (sceneHandle == 0)
                {
                    throw new ArgumentException(
                        "Scene 加载模式必须提供有效的 Scene handle。",
                        nameof(sceneHandle));
                }

                return;
            }

            if (sceneHandle != 0)
            {
                throw new ArgumentException(
                    "只有 Scene 加载模式可以提供 Scene handle。",
                    nameof(sceneHandle));
            }
        }
    }
}
