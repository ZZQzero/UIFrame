using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Audio;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace UIFrame.Regression
{
    public class AudioFailureContractTests
    {
        sealed class Handle : IAudioClipHandle
        {
            public AudioClip Clip { get; set; }
            public readonly UniTaskCompletionSource Ready = new();
            public UniTask Completion => Ready.Task;
            public bool Succeeded => true;
            public string Error => null;
            public Exception DisposeFailure;
            public int Disposals;
            public Action OnDispose;
            public void Dispose() { Disposals++; OnDispose?.Invoke(); if (DisposeFailure != null) throw DisposeFailure; }
        }
        sealed class Provider : IAudioClipProvider
        {
            public readonly Dictionary<string, Handle> Handles = new();
            public IAudioClipHandle Load(string location) => Handles[location];
        }

        Provider provider;
        AudioClipCache cache;
        AudioClip clip;
        GameObject root;
        AudioRuntimeDriver driver;
        bool cacheDisposed;
        static readonly BindingFlags StaticFields = BindingFlags.Static | BindingFlags.NonPublic;
        static void Set(string name, object value) => typeof(GameAudio).GetField(name, StaticFields).SetValue(null, value);
        static object Get(string name) => typeof(GameAudio).GetField(name, StaticFields).GetValue(null);

        [SetUp] public void Setup()
        {
            Assert.AreEqual("None", Get("state").ToString());
            provider = new Provider();
            cache = new AudioClipCache(provider, 0f);
            clip = AudioClip.Create("audio-failure-contract", 44100, 1, 44100, false);
            cacheDisposed = false;
        }

        Handle Add(string location, bool ready = true)
        {
            var handle = new Handle { Clip = clip };
            provider.Handles.Add(location, handle);
            if (ready) handle.Ready.TrySetResult();
            return handle;
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            foreach (var handle in provider.Handles.Values) handle.DisposeFailure = null;
            if (!cacheDisposed) cache.Dispose();
            if (root != null) UnityEngine.Object.Destroy(root);
            UnityEngine.Object.Destroy(clip);
            yield return null;
        }

        [Test] public void LoadAndCleanupFailureSettleEveryWaiterWithPrimary()
        {
            var handle = Add("broken", false);
            var primary = new InvalidOperationException("audio-load-primary");
            handle.DisposeFailure = new InvalidOperationException("audio-cleanup-secondary");
            var first = cache.AcquireAsync("broken", AudioLoadMode.OnDemand, default);
            var second = cache.AcquireAsync("broken", AudioLoadMode.OnDemand, default);
            LogAssert.Expect(LogType.Exception, new Regex("audio-cleanup-secondary"));
            handle.Ready.TrySetException(primary);
            Assert.AreEqual(UniTaskStatus.Faulted, first.Status);
            Assert.AreEqual(UniTaskStatus.Faulted, second.Status);
            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(() => first.GetAwaiter().GetResult()));
            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(() => second.GetAwaiter().GetResult()));
            Assert.AreEqual(1, handle.Disposals);
            Assert.AreEqual(0, cache.EntryCount);
            Assert.AreEqual(0, cache.LoadingCount);
            Assert.AreEqual(UniTaskStatus.Succeeded, cache.WaitForIdleAsync().Status);
            cache.Dispose(); cacheDisposed = true;
            Assert.AreEqual(1, handle.Disposals);
        }

        [Test] public void CacheDisposalAttemptsAllHandlesAndDoesNotRepeatRelease()
        {
            var first = Add("first"); var second = Add("second");
            cache.AcquireAsync("first", AudioLoadMode.OnDemand, default).GetAwaiter().GetResult().Dispose();
            cache.AcquireAsync("second", AudioLoadMode.OnDemand, default).GetAwaiter().GetResult().Dispose();
            var primary = new InvalidOperationException("audio-dispose-primary");
            first.DisposeFailure = primary;
            second.DisposeFailure = new InvalidOperationException("audio-dispose-secondary");
            LogAssert.Expect(LogType.Exception, new Regex("audio-dispose-secondary"));
            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(cache.Dispose));
            cacheDisposed = true;
            Assert.AreEqual(1, first.Disposals); Assert.AreEqual(1, second.Disposals);
            Assert.AreEqual(0, cache.EntryCount);
            Assert.Throws<ObjectDisposedException>(cache.Dispose);
            Assert.AreEqual(1, first.Disposals);
        }

        [Test] public void DisposalWithLiveLeaseIsRejectedBeforeAnyRelease()
        {
            var handle = Add("live");
            var lease = cache.AcquireAsync("live", AudioLoadMode.OnDemand, default).GetAwaiter().GetResult();
            Assert.Throws<AudioStateException>(cache.Dispose);
            Assert.AreEqual(0, handle.Disposals);
            lease.Dispose();
            cache.Dispose(); cacheDisposed = true;
            Assert.AreEqual(1, handle.Disposals);
        }

        [Test] public void ExpirationFailureDetachesFailedEntryAndKeepsUntouchedEntries()
        {
            var first = Add("first"); var second = Add("second");
            cache.AcquireAsync("first", AudioLoadMode.OnDemand, default).GetAwaiter().GetResult().Dispose();
            cache.AcquireAsync("second", AudioLoadMode.OnDemand, default).GetAwaiter().GetResult().Dispose();
            var primary = new InvalidOperationException("expire-primary"); first.DisposeFailure = primary;
            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(() => cache.CollectExpired(double.MaxValue)));
            Assert.AreEqual(1, cache.EntryCount);
            Assert.AreEqual(1, first.Disposals); Assert.AreEqual(0, second.Disposals);
            cache.CollectExpired(double.MaxValue);
            Assert.AreEqual(1, first.Disposals); Assert.AreEqual(1, second.Disposals);
        }

        [Test] public void SceneReleaseFailureDoesNotRetryHandleOrLease()
        {
            var handle = Add("scene");
            int scene = SceneManager.GetActiveScene().handle;
            var lease = cache.AcquireAsync("scene", AudioLoadMode.Scene, scene, default).GetAwaiter().GetResult();
            cache.UnloadSceneAssets(scene);
            var primary = new InvalidOperationException("scene-release"); handle.DisposeFailure = primary;
            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(lease.Dispose));
            Assert.AreEqual(0, cache.EntryCount);
            Assert.Throws<ObjectDisposedException>(lease.Dispose);
            cache.Dispose(); cacheDisposed = true;
            Assert.AreEqual(1, handle.Disposals);
        }

        [Test] public void PendingSceneUnloadReleaseFailureIsReportedOnce()
        {
            var handle = Add("pending", false);
            using var cancellation = new CancellationTokenSource();
            int scene = SceneManager.GetActiveScene().handle;
            var load = cache.AcquireAsync("pending", AudioLoadMode.Scene, scene, cancellation.Token);
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => load.GetAwaiter().GetResult());
            cache.UnloadSceneAssets(scene);
            handle.DisposeFailure = new InvalidOperationException("pending-scene-release");
            LogAssert.Expect(LogType.Exception, new Regex("pending-scene-release"));
            handle.Ready.TrySetResult();
            Assert.AreEqual(1, handle.Disposals);
            Assert.AreEqual(0, cache.EntryCount);
            Assert.AreEqual(0, cache.LoadingCount);
        }

        ResolvedAudioConfig StartDriver()
        {
            root = new GameObject("audio-shutdown-contract");
            driver = root.AddComponent<AudioRuntimeDriver>();
            var config = new ResolvedAudioConfig(2, 0f, null, null,
                new Dictionary<AudioId, AudioEntry>(),
                new Dictionary<AudioBus, AudioMixerGroup> { [AudioBus.Sfx] = null },
                new Dictionary<AudioBus, string>());
            driver.Initialize(config, cache);
            return config;
        }

        [Test] public void DriverShutdownReleasesAllVoicesBeforeReportingFailure()
        {
            StartDriver();
            var first = Add("first"); var second = Add("second");
            var options = AudioPlayOptions.Default;
            foreach (var name in new[] { "first", "second" })
            {
                var lease = cache.AcquireAsync(name, AudioLoadMode.OnDemand, default).GetAwaiter().GetResult();
                Assert.IsTrue(driver.TryStart(new AudioEntry(name, name, loop: true), lease, in options, 0f).IsPlaying);
            }
            var primary = new InvalidOperationException("driver-dispose"); first.DisposeFailure = primary;
            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(driver.Shutdown));
            cacheDisposed = true;
            Assert.AreEqual(1, first.Disposals); Assert.AreEqual(1, second.Disposals);
            Assert.AreEqual(0, cache.EntryCount);
            foreach (var source in root.GetComponentsInChildren<AudioSource>()) Assert.IsNull(source.clip);
            Assert.Throws<AudioStateException>(driver.Shutdown);
        }

        [Test] public void PlayFailurePreservesPrimaryAndFinishesOperationWhenLeaseCleanupFails()
        {
            var config = StartDriver();
            var first = Add("old");
            var second = Add("new");
            int scene = SceneManager.GetActiveScene().handle;
            var options = AudioPlayOptions.Default;
            var entry = new AudioEntry("sound", "new", maxInstances: 1,
                overflowPolicy: AudioOverflowPolicy.StopOldest, loop: true,
                loadMode: AudioLoadMode.Scene);
            ((Dictionary<AudioId, AudioEntry>)config.Catalog).Add(entry.Id, entry);
            var oldLease = cache.AcquireAsync("old", AudioLoadMode.Scene, scene, default).GetAwaiter().GetResult();
            var playing = driver.TryStart(entry, oldLease, in options, 0f);
            Assert.IsTrue(playing.IsPlaying);
            cache.UnloadSceneAssets(scene);
            var primary = new InvalidOperationException("play-primary");
            first.DisposeFailure = primary;
            // Simulate scene teardown during the displaced voice's release.
            first.OnDispose = () => cache.UnloadSceneAssets(scene);
            second.DisposeFailure = new InvalidOperationException("play-lease-secondary");
            var cancellation = new CancellationTokenSource();
            Set("config", config); Set("cache", cache); Set("driver", driver);
            Set("ownedRoot", root.transform); Set("runtimeCancellation", cancellation);
            var stateField = typeof(GameAudio).GetField("state", StaticFields);
            stateField.SetValue(null, Enum.Parse(stateField.FieldType, "Running"));

            LogAssert.Expect(LogType.Exception, new Regex("play-lease-secondary"));
            var request = GameAudio.TryPlayAsync(entry.Id);
            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(() => request.GetAwaiter().GetResult()));
            Assert.AreEqual(0, Get("pendingOperations"));
            Assert.AreEqual(0, cache.EntryCount);
            Assert.AreEqual(1, first.Disposals); Assert.AreEqual(1, second.Disposals);
            Assert.IsFalse(driver.IsPlaying(playing.Handle));
            var shutdown = GameAudio.ShutdownAsync();
            Assert.AreEqual(UniTaskStatus.Succeeded, shutdown.Status);
            shutdown.GetAwaiter().GetResult();
            cacheDisposed = true;
            Assert.AreEqual("None", Get("state").ToString());
        }

        [TestCase(false)] [TestCase(true)]
        public void GlobalShutdownClearsStateEvenWhenCancellationOrReleaseFails(bool cancelFails)
        {
            var config = StartDriver();
            var first = Add("first"); var second = Add("second");
            cache.AcquireAsync("first", AudioLoadMode.OnDemand, default).GetAwaiter().GetResult().Dispose();
            cache.AcquireAsync("second", AudioLoadMode.OnDemand, default).GetAwaiter().GetResult().Dispose();
            var primary = new InvalidOperationException("global-dispose"); first.DisposeFailure = primary;
            var cancellation = new CancellationTokenSource();
            var cancelError = new InvalidOperationException("global-cancel");
            if (cancelFails) cancellation.Token.Register(() => throw cancelError);
            Set("config", config); Set("cache", cache); Set("driver", driver);
            Set("ownedRoot", root.transform); Set("runtimeCancellation", cancellation);
            var stateField = typeof(GameAudio).GetField("state", StaticFields);
            stateField.SetValue(null, Enum.Parse(stateField.FieldType, "Running"));
            if (cancelFails)
            {
                LogAssert.Expect(LogType.Exception, new Regex("global-dispose"));
                var error = Assert.Throws<AggregateException>(() => GameAudio.ShutdownAsync().GetAwaiter().GetResult());
                Assert.AreSame(cancelError, error.InnerException);
            }
            else Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(() => GameAudio.ShutdownAsync().GetAwaiter().GetResult()));
            cacheDisposed = true;
            Assert.AreEqual("None", Get("state").ToString());
            foreach (var name in new[] { "cache", "driver", "config", "ownedRoot", "runtimeCancellation" }) Assert.IsNull(Get(name), name);
            Assert.Throws<ObjectDisposedException>(() => { _ = cancellation.Token; });
            Assert.AreEqual(1, first.Disposals); Assert.AreEqual(1, second.Disposals);
        }
    }
}
