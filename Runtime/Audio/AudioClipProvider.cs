using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YooAsset;

namespace Game.Audio
{
    internal interface IAudioClipProvider
    {
        IAudioClipHandle Load(string location);
    }

    internal interface IAudioClipHandle : IDisposable
    {
        UniTask Completion { get; }
        bool Succeeded { get; }
        string Error { get; }
        AudioClip Clip { get; }
    }

    internal sealed class YooAssetAudioClipProvider : IAudioClipProvider
    {
        private readonly ResourcePackage package;

        internal YooAssetAudioClipProvider(ResourcePackage package)
        {
            this.package = package;
        }

        public IAudioClipHandle Load(string location) =>
            new YooAssetAudioClipHandle(
                package.LoadAssetAsync<AudioClip>(location));
    }

    internal sealed class YooAssetAudioClipHandle : IAudioClipHandle
    {
        private AssetHandle handle;
        private readonly UniTask completion;

        internal YooAssetAudioClipHandle(AssetHandle handle)
        {
            this.handle = handle;
            completion = AwaitHandleAsync(handle);
        }

        public UniTask Completion => completion;
        public bool Succeeded =>
            RequireHandle().Status == EOperationStatus.Succeeded;
        public string Error => RequireHandle().Error;
        public AudioClip Clip =>
            RequireHandle().GetAssetObject<AudioClip>();

        public void Dispose()
        {
            AssetHandle current = RequireHandle();
            handle = null;
            current.Release();
        }

        private AssetHandle RequireHandle() =>
            handle ?? throw new ObjectDisposedException(
                nameof(YooAssetAudioClipHandle));

        private static async UniTask AwaitHandleAsync(AssetHandle handle)
        {
            await handle;
        }
    }
}
