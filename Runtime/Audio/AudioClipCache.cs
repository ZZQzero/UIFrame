using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YooAsset;

namespace Game.Audio
{
    internal sealed class AudioClipCache : IDisposable
    {
        private readonly IAudioClipProvider provider;
        private readonly float retentionSeconds;
        private readonly Dictionary<string, CacheEntry> entries =
            new(StringComparer.Ordinal);
        private readonly List<string> removalBuffer = new();
        private UniTaskCompletionSource idleCompletion;
        private int loadingCount;
        private bool disposed;

        internal AudioClipCache(
            ResourcePackage package,
            float retentionSeconds)
            : this(
                new YooAssetAudioClipProvider(package),
                retentionSeconds)
        {
        }

        internal AudioClipCache(
            IAudioClipProvider provider,
            float retentionSeconds)
        {
            this.provider = provider ??
                throw new ArgumentNullException(nameof(provider));
            if (!float.IsFinite(retentionSeconds) || retentionSeconds < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(retentionSeconds));
            }

            this.retentionSeconds = retentionSeconds;
        }

        internal async UniTask<AudioClipLease> AcquireAsync(
            string location,
            AudioLoadMode loadMode,
            CancellationToken cancellationToken)
        {
            int sceneHandle = loadMode == AudioLoadMode.Scene
                ? AudioSceneScope.RequireActiveHandle()
                : 0;
            return await AcquireAsync(
                location,
                loadMode,
                sceneHandle,
                cancellationToken);
        }

        internal async UniTask<AudioClipLease> AcquireAsync(
            string location,
            AudioLoadMode loadMode,
            int sceneHandle,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(location))
            {
                throw new ArgumentException(
                    "AudioClip location 不能为空。",
                    nameof(location));
            }

            if (!provider.Contains(location))
            {
                throw new InvalidOperationException(
                    $"YooAsset 中不存在音频 location：{location}。");
            }

            AudioSceneScope.Validate(loadMode, sceneHandle);
            if (!entries.TryGetValue(location, out CacheEntry entry))
            {
                entry = new CacheEntry(location, loadMode);
                entries.Add(location, entry);
                BeginLoad(entry);
            }
            else if (entry.LoadMode != loadMode)
            {
                throw new InvalidOperationException(
                    $"同一音频 location '{location}' 不能配置不同的 loadMode：" +
                    $"{entry.LoadMode} 与 {loadMode}。");
            }

            if (loadMode == AudioLoadMode.Scene)
            {
                entry.SceneOwners.Add(sceneHandle);
                entry.PendingSceneUnload = false;
            }

            entry.ReferenceCount++;
            try
            {
                AudioClip clip = entry.IsLoaded
                    ? entry.Clip
                    : await entry.Completion.Task.AttachExternalCancellation(
                        cancellationToken);
                await UniTask.SwitchToMainThread();
                if (loadMode == AudioLoadMode.Scene &&
                    !entry.SceneOwners.Contains(sceneHandle))
                {
                    throw new AudioStateException(
                        $"音频 '{location}' 加载完成前所属 Scene 已卸载。");
                }

                return new AudioClipLease(this, entry, clip);
            }
            catch
            {
                await UniTask.SwitchToMainThread();
                Release(entry);
                throw;
            }
        }

        internal void CollectExpired(double now)
        {
            ThrowIfDisposed();
            if (!double.IsFinite(now) || now < 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(now));
            }

            removalBuffer.Clear();
            foreach (KeyValuePair<string, CacheEntry> pair in entries)
            {
                CacheEntry entry = pair.Value;
                if (entry.LoadMode != AudioLoadMode.OnDemand ||
                    entry.ReferenceCount != 0 ||
                    !entry.IsLoaded ||
                    now - entry.LastReleaseTime < retentionSeconds)
                {
                    continue;
                }

                removalBuffer.Add(pair.Key);
            }

            ReleaseEntries(removalBuffer);
            removalBuffer.Clear();
        }

        internal void UnloadSceneAssets()
        {
            ThrowIfDisposed();
            removalBuffer.Clear();
            foreach (KeyValuePair<string, CacheEntry> pair in entries)
            {
                CacheEntry entry = pair.Value;
                if (entry.LoadMode != AudioLoadMode.Scene)
                {
                    continue;
                }

                entry.SceneOwners.Clear();
                entry.PendingSceneUnload = true;
                if (entry.ReferenceCount == 0 && entry.IsLoaded)
                {
                    removalBuffer.Add(pair.Key);
                }
            }

            ReleaseEntries(removalBuffer);
            removalBuffer.Clear();
        }

        internal void UnloadSceneAssets(int sceneHandle)
        {
            ThrowIfDisposed();
            if (sceneHandle == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sceneHandle));
            }

            removalBuffer.Clear();
            foreach (KeyValuePair<string, CacheEntry> pair in entries)
            {
                CacheEntry entry = pair.Value;
                if (entry.LoadMode != AudioLoadMode.Scene ||
                    !entry.SceneOwners.Remove(sceneHandle) ||
                    entry.SceneOwners.Count != 0)
                {
                    continue;
                }

                entry.PendingSceneUnload = true;
                if (entry.ReferenceCount == 0 && entry.IsLoaded)
                {
                    removalBuffer.Add(pair.Key);
                }
            }

            ReleaseEntries(removalBuffer);
            removalBuffer.Clear();
        }

        internal UniTask WaitForIdleAsync()
        {
            ThrowIfDisposed();
            return loadingCount == 0
                ? UniTask.CompletedTask
                : idleCompletion.Task;
        }

        internal int LoadingCount => loadingCount;
        internal int EntryCount => entries.Count;

        public void Dispose()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(
                    nameof(AudioClipCache),
                    "AudioClipCache 不允许重复 Dispose。");
            }

            foreach (CacheEntry entry in entries.Values)
            {
                if (entry.ReferenceCount != 0)
                {
                    throw new AudioStateException(
                        $"关闭 AudioClipCache 时音频 '{entry.Location}' " +
                        $"仍有 {entry.ReferenceCount} 个播放引用。");
                }

            }

            if (loadingCount != 0)
            {
                throw new AudioStateException(
                    $"关闭 AudioClipCache 时仍有 {loadingCount} 个音频资源正在加载。");
            }

            disposed = true;
            foreach (CacheEntry entry in entries.Values)
            {
                entry.Handle?.Dispose();
                entry.Handle = null;
            }

            entries.Clear();
        }

        private void BeginLoad(CacheEntry entry)
        {
            if (loadingCount == 0)
            {
                idleCompletion = new UniTaskCompletionSource();
            }

            loadingCount++;
            entry.Completion = new UniTaskCompletionSource<AudioClip>();
            LoadCoreAsync(entry).Forget();
        }

        private async UniTask LoadCoreAsync(CacheEntry entry)
        {
            IAudioClipHandle handle = null;
            try
            {
                handle = provider.Load(entry.Location);
                entry.Handle = handle;
                await handle.Completion;
                await UniTask.SwitchToMainThread();
                if (!handle.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"加载 AudioClip 失败：{entry.Location}，{handle.Error}");
                }

                AudioClip clip = handle.Clip;
                if (clip == null)
                {
                    throw new InvalidOperationException(
                        $"资源 '{entry.Location}' 不是有效的 AudioClip。");
                }

                entry.IsLoaded = true;
                entry.Clip = clip;
                entry.Completion.TrySetResult(clip);

                if (entry.PendingSceneUnload &&
                    entry.ReferenceCount == 0 &&
                    entries.TryGetValue(entry.Location, out CacheEntry current) &&
                    ReferenceEquals(current, entry))
                {
                    ReleaseEntry(entry);
                }
            }
            catch (Exception exception)
            {
                await UniTask.SwitchToMainThread();
                handle?.Dispose();
                entry.Handle = null;
                if (entries.TryGetValue(entry.Location, out CacheEntry current) &&
                    ReferenceEquals(current, entry))
                {
                    entries.Remove(entry.Location);
                }

                entry.Completion.TrySetException(exception);
            }
            finally
            {
                loadingCount--;
                if (loadingCount == 0)
                {
                    idleCompletion.TrySetResult();
                }
            }
        }

        private void Release(CacheEntry entry)
        {
            if (entry.ReferenceCount <= 0)
            {
                throw new AudioStateException(
                    $"AudioClip '{entry.Location}' 的引用计数发生下溢。");
            }

            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0)
            {
                entry.LastReleaseTime = Time.realtimeSinceStartupAsDouble;
                if (entry.PendingSceneUnload &&
                    entry.IsLoaded &&
                    entries.TryGetValue(entry.Location, out CacheEntry current) &&
                    ReferenceEquals(current, entry))
                {
                    ReleaseEntry(entry);
                }
            }
        }

        private void ReleaseEntries(List<string> removals)
        {
            if (removals.Count == 0)
            {
                return;
            }

            for (int i = 0; i < removals.Count; i++)
            {
                string location = removals[i];
                CacheEntry entry = entries[location];
                ReleaseEntry(entry);
            }
        }

        private void ReleaseEntry(CacheEntry entry)
        {
            if (entry.ReferenceCount != 0 || !entry.IsLoaded)
            {
                throw new AudioStateException(
                    $"音频 '{entry.Location}' 尚不满足释放条件。");
            }

            entry.Handle.Dispose();
            entry.Handle = null;
            entry.Clip = null;
            entries.Remove(entry.Location);
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(AudioClipCache));
            }
        }

        internal sealed class AudioClipLease : IDisposable
        {
            private AudioClipCache owner;
            private CacheEntry entry;

            internal AudioClipLease(
                AudioClipCache owner,
                CacheEntry entry,
                AudioClip clip)
            {
                this.owner = owner;
                this.entry = entry;
                Clip = clip;
            }

            internal AudioClip Clip { get; }

            public void Dispose()
            {
                if (owner == null)
                {
                    throw new ObjectDisposedException(
                        nameof(AudioClipLease),
                        "AudioClipLease 不允许重复 Dispose。");
                }

                owner.Release(entry);
                owner = null;
                entry = null;
            }
        }

        internal sealed class CacheEntry
        {
            internal CacheEntry(string location, AudioLoadMode loadMode)
            {
                Location = location;
                LoadMode = loadMode;
                SceneOwners = loadMode == AudioLoadMode.Scene
                    ? new HashSet<int>()
                    : null;
            }

            internal string Location { get; }
            internal AudioLoadMode LoadMode { get; }
            internal IAudioClipHandle Handle { get; set; }
            internal AudioClip Clip { get; set; }
            internal UniTaskCompletionSource<AudioClip> Completion { get; set; }
            internal int ReferenceCount { get; set; }
            internal double LastReleaseTime { get; set; }
            internal bool IsLoaded { get; set; }
            internal bool PendingSceneUnload { get; set; }
            internal HashSet<int> SceneOwners { get; }
        }
    }
}
