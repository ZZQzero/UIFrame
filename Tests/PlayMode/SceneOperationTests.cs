using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Game.Scene;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace UIFrame.Regression
{
    public class SceneOperationTests
    {
        sealed class Handle : ISceneHandle
        {
            public LoadSceneMode Mode { get; set; }
            public bool IsPreloaded { get; set; }
            public int Unloads;
            public int Activations;
            public Exception ActivationFailure;
            public Exception UnloadFailure;
            public UniTaskCompletionSource UnloadCompletion;
            public UniTask ActivateAsync()
            {
                Activations++;
                if (ActivationFailure != null) return UniTask.FromException(ActivationFailure);
                IsPreloaded = false;
                return UniTask.CompletedTask;
            }
            public UniTask UnloadAsync()
            {
                Unloads++;
                if (UnloadFailure != null) return UniTask.FromException(UnloadFailure);
                return UnloadCompletion != null ? UnloadCompletion.Task : UniTask.CompletedTask;
            }
        }

        sealed class Loader : ISceneLoader
        {
            public readonly List<Handle> Handles = new();
            public UniTaskCompletionSource<ISceneHandle> Pending;
            public Exception ActivationFailure;
            public Exception UnloadFailure;
            public UniTaskCompletionSource UnloadCompletion;
            public UniTask<ISceneHandle> LoadAsync(string location, LoadSceneMode mode,
                bool allowSceneActivation, Action<float> report)
            {
                var handle = new Handle { Mode = mode, IsPreloaded = !allowSceneActivation,
                    ActivationFailure = ActivationFailure, UnloadFailure = UnloadFailure,
                    UnloadCompletion = UnloadCompletion };
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

        [TestCase(false)] [TestCase(true)]
        public void SingleActivationFailureReleasesNewHandleAndPropagatesOriginalError(bool reload)
        {
            GameScene.LoadAsync("game", LoadSceneMode.Single).GetAwaiter().GetResult();
            var failure = new InvalidOperationException("reload-activation");
            loader.ActivationFailure = failure;
            Assert.AreSame(failure, Assert.Throws<InvalidOperationException>(
                () => ReplaceScene(reload).GetAwaiter().GetResult()));
            Assert.AreEqual(1, loader.Handles[1].Unloads);
            Assert.AreEqual(1, loader.Handles[1].Activations);
            Assert.IsFalse(GameScene.IsLoaded("game"));
            Assert.IsFalse(GameScene.IsLoaded("next"));
            Assert.IsNull(GameScene.ActiveId);
            Assert.IsFalse(GameScene.IsBusy);
        }

        UniTask ReplaceScene(bool reload) => reload
            ? GameScene.ReloadAsync()
            : GameScene.LoadAsync("next", LoadSceneMode.Single);

        [TestCase(false, false)] [TestCase(false, true)]
        [TestCase(true, false)] [TestCase(true, true)]
        public void OldUnloadFailureReleasesIncomingHandleAndPreservesError(bool reload, bool cleanupFailure)
        {
            GameScene.LoadAsync("game", LoadSceneMode.Single).GetAwaiter().GetResult();
            var old = loader.Handles[0];
            var primary = new InvalidOperationException("old-unload-primary");
            old.UnloadFailure = primary;
            if (cleanupFailure)
            {
                loader.UnloadFailure = new InvalidOperationException("incoming-unload-secondary");
                LogAssert.Expect(LogType.Exception, new Regex("incoming-unload-secondary"));
            }

            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(() =>
                ReplaceScene(reload).GetAwaiter().GetResult()));
            var incoming = loader.Handles[1];
            Assert.AreEqual(1, old.Unloads, "失败的旧场卸载不自动重试");
            Assert.AreEqual(1, incoming.Unloads, "未登记句柄只释放一次");
            Assert.AreEqual(0, incoming.Activations, "旧场清理失败后停止后续激活");
            Assert.AreSame(old, GameScene.Loaded["game"]);
            Assert.AreEqual(1, GameScene.Loaded.Count);
            Assert.AreEqual("game", GameScene.ActiveId);
            Assert.IsFalse(GameScene.IsBusy);
        }

        [Test] public async Task IncomingHandleCleanupKeepsOperationBusyUntilItFinishes()
        {
            await GameScene.LoadAsync("game", LoadSceneMode.Single);
            var primary = new InvalidOperationException("old-unload-primary");
            loader.Handles[0].UnloadFailure = primary;
            var cleanup = new UniTaskCompletionSource();
            loader.UnloadCompletion = cleanup;

            var replace = GameScene.LoadAsync("next", LoadSceneMode.Single).AsTask();
            var idle = GameScene.WaitForIdleAsync().AsTask();
            try
            {
                Assert.IsTrue(GameScene.IsBusy);
                Assert.IsFalse(replace.IsCompleted);
                Assert.IsFalse(idle.IsCompleted);
                Assert.AreEqual(1, loader.Handles[1].Unloads);
                Assert.Throws<InvalidOperationException>(() => GameScene.LoadAsync("third", LoadSceneMode.Single));
            }
            finally
            {
                cleanup.TrySetResult();
            }
            Exception observed = null;
            try { await replace; } catch (Exception error) { observed = error; }
            await idle;
            Assert.AreSame(primary, observed);
            Assert.IsFalse(GameScene.IsBusy);
        }

        [Test] public void SwitchOldUnloadFailureKeepsSuccessfullyRegisteredIncomingScene()
        {
            GameScene.LoadAsync("game", LoadSceneMode.Single).GetAwaiter().GetResult();
            var primary = new InvalidOperationException("switch-old-unload");
            loader.Handles[0].UnloadFailure = primary;
            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(() =>
                GameScene.SwitchAsync("next").GetAwaiter().GetResult()));
            var incoming = loader.Handles[1];
            Assert.AreEqual(0, incoming.Unloads, "已经登记的新场不能被当成未登记句柄释放");
            Assert.AreEqual(1, incoming.Activations);
            Assert.AreSame(incoming, GameScene.Loaded["next"]);
            Assert.AreEqual(2, GameScene.Loaded.Count);
            Assert.AreEqual("next", GameScene.ActiveId);
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

        [Test] public async Task ShutdownReportsInflightFailureAfterClearingState()
        {
            loader.Pending = new UniTaskCompletionSource<ISceneHandle>();
            var load = GameScene.LoadAsync("game", LoadSceneMode.Single).AsTask();
            var shutdown = GameScene.ShutdownAsync().AsTask();
            var primary = new InvalidOperationException("shutdown-inflight");
            loader.Pending.TrySetException(primary);
            Exception loadFailure = null, shutdownFailure = null;
            try { await load; } catch (Exception ex) { loadFailure = ex; }
            try { await shutdown; } catch (Exception ex) { shutdownFailure = ex; }
            Assert.AreSame(primary, loadFailure);
            Assert.AreSame(primary, shutdownFailure);
            Assert.IsFalse(GameScene.IsInited);
            Assert.Throws<InvalidOperationException>(() => { _ = GameScene.IsBusy; });
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
