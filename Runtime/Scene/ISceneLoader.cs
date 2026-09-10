using System;
using Cysharp.Threading.Tasks;
using UnityEngine.SceneManagement;

namespace Game.Scene
{
    internal interface ISceneLoader
    {
        UniTask<ISceneHandle> LoadAsync(
            string location,
            LoadSceneMode mode,
            bool allowSceneActivation,
            Action<float> report);
    }

    internal interface ISceneHandle
    {
        LoadSceneMode Mode { get; }

        bool IsPreloaded { get; }

        void ActivateScene();

        UniTask ActivatePreloadedAsync();

        UniTask UnloadAsync();
    }
}
