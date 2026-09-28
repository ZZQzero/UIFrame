using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Game.Scene;
using NUnit.Framework;
using UnityEngine.SceneManagement;

namespace UIFrame.Regression
{
    public class SceneOperationTests
    {
        sealed class Handle : ISceneHandle
        {
            public LoadSceneMode Mode { get; set; }
            public bool IsPreloaded { get; set; }
            public int Unloads;
            public Exception ActivationFailure;
            public UniTask ActivateAsync()
            {
                if (ActivationFailure != null) return UniTask.FromException(ActivationFailure);
                IsPreloaded = false;
                return UniTask.CompletedTask;
            }
            public UniTask UnloadAsync() { Unloads++; return UniTask.CompletedTask; }
        }

        sealed class Loader : ISceneLoader
        {
            public readonly List<Handle> Handles = new();
            public UniTaskCompletionSource<ISceneHandle> Pending;
            public Exception ActivationFailure;
            public UniTask<ISceneHandle> LoadAsync(string location, LoadSceneMode mode,
                bool allowSceneActivation, Action<float> report)
            {
                var handle = new Handle { Mode = mode, IsPreloaded = !allowSceneActivation,
                    ActivationFailure = ActivationFailure };
                Handles.Add(handle);
                return Pending != null ? Pending.Task : UniTask.FromResult<ISceneHandle>(handle);
            }
        }

        Loader loader;
        [SetUp] public void Setup()
        {
            loader = new Loader();
            GameScene.InitLoader(loader);
        }
        [TearDown] public void TearDown()
        {
            if (GameScene.IsInited) GameScene.ShutdownAsync().GetAwaiter().GetResult();
        }

        [Test] public void ReloadReplacesActiveHandleAndUnloadsAdditiveScenes()
        {
            GameScene.LoadAsync("game", LoadSceneMode.Single).GetAwaiter().GetResult();
            GameScene.LoadAsync("chunk", LoadSceneMode.Additive).GetAwaiter().GetResult();
            GameScene.ReloadAsync().GetAwaiter().GetResult();
            Assert.AreEqual("game", GameScene.ActiveId);
            Assert.IsTrue(GameScene.IsLoaded("game"));
            Assert.IsFalse(GameScene.IsLoaded("chunk"));
            Assert.AreEqual(LoadSceneMode.Single, loader.Handles[2].Mode);
            Assert.AreEqual(1, loader.Handles[0].Unloads);
            Assert.AreEqual(1, loader.Handles[1].Unloads);
            Assert.AreSame(loader.Handles[2], GameScene.Loaded["game"]);
        }

        [Test] public void ReloadCannotBypassSuspendedSceneGuard()
        {
            GameScene.LoadAsync("game", LoadSceneMode.Single).GetAwaiter().GetResult();
            GameScene.PreloadAsync("next", LoadSceneMode.Additive).GetAwaiter().GetResult();
            Assert.Throws<InvalidOperationException>(() => GameScene.ReloadAsync());
            Assert.Throws<InvalidOperationException>(() => GameScene.SwitchAsync("other"));
            Assert.Throws<InvalidOperationException>(() => GameScene.LoadBuiltinAsync("Launch"));
            Assert.AreEqual(2, loader.Handles.Count);
            Assert.AreEqual(0, loader.Handles[1].Unloads);
            Assert.AreEqual("game", GameScene.ActiveId);
            GameScene.ActivateAsync("next").GetAwaiter().GetResult();
            Assert.DoesNotThrow(() => GameScene.ReloadAsync().GetAwaiter().GetResult());
        }

        [Test] public void SwitchFromBuiltinShellRequiresExplicitSingleLoad()
        {
            // 内置场景只有 ActiveId，没有 YooAsset handle；保持严格的 Switch 契约。
            typeof(GameScene).GetField("activeId", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, "Launch");
            Assert.Throws<InvalidOperationException>(() => GameScene.SwitchAsync("game"));
            Assert.AreEqual(0, loader.Handles.Count);
            Assert.AreEqual("Launch", GameScene.ActiveId);

            GameScene.LoadAsync("game", LoadSceneMode.Single).GetAwaiter().GetResult();
            Assert.AreEqual(LoadSceneMode.Single, loader.Handles[0].Mode);
            Assert.AreEqual("game", GameScene.ActiveId);
            Assert.IsFalse(GameScene.IsLoaded("Launch"));
        }

        [Test] public void ReloadActivationFailureReleasesNewHandleAndPropagatesOriginalError()
        {
            GameScene.LoadAsync("game", LoadSceneMode.Single).GetAwaiter().GetResult();
            var failure = new InvalidOperationException("reload-activation");
            loader.ActivationFailure = failure;
            Assert.AreSame(failure, Assert.Throws<InvalidOperationException>(
                () => GameScene.ReloadAsync().GetAwaiter().GetResult()));
            Assert.AreEqual(1, loader.Handles[1].Unloads);
            Assert.IsFalse(GameScene.IsLoaded("game"));
            Assert.IsFalse(GameScene.IsBusy);
        }

        [TestCase(false)] [TestCase(true)]
        public async Task ExitWaitersSharePendingLoadWithoutConsumingItsError(bool fail)
        {
            loader.Pending = new UniTaskCompletionSource<ISceneHandle>();
            var load = GameScene.LoadAsync("game", LoadSceneMode.Single).AsTask();
            var exitWait = GameScene.WaitForIdleAsync().AsTask();
            var secondWait = GameScene.WaitForIdleAsync().AsTask();
            Assert.IsFalse(exitWait.IsCompleted);
            Assert.IsFalse(secondWait.IsCompleted);
            var failure = new InvalidOperationException("load-primary");
            if (fail) loader.Pending.TrySetException(failure);
            else loader.Pending.TrySetResult(loader.Handles[0]);
            Exception observed = null;
            try { await load; } catch (Exception error) { observed = error; }
            await Task.WhenAll(exitWait, secondWait);
            Assert.AreSame(fail ? failure : null, observed);
            Assert.IsFalse(GameScene.IsBusy);
            loader.Pending = null;
            await GameScene.LoadAsync("after-exit-wait", LoadSceneMode.Single);
            Assert.AreEqual("after-exit-wait", GameScene.ActiveId);
        }

        [Test] public async Task ShutdownCanWaitAlongsideLoadAndExitWaiter()
        {
            loader.Pending = new UniTaskCompletionSource<ISceneHandle>();
            var load = GameScene.LoadAsync("game", LoadSceneMode.Single).AsTask();
            var exitWait = GameScene.WaitForIdleAsync().AsTask();
            var shutdown = GameScene.ShutdownAsync().AsTask();
            Assert.IsFalse(shutdown.IsCompleted);
            loader.Pending.TrySetResult(loader.Handles[0]);
            await Task.WhenAll(load, exitWait, shutdown);
            Assert.IsFalse(GameScene.IsInited);
        }
    }
}
