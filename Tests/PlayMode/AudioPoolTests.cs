using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Audio;
using Game.Pooling;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Audio;

namespace UIFrame.Regression
{
    public class AudioPoolTests
    {
        sealed class ClipProvider : IAudioClipProvider, IAudioClipHandle
        {
            public AudioClip Clip { get; } = AudioClip.Create("regression", 44100, 1, 44100, false);
            public UniTask Completion => UniTask.CompletedTask;
            public bool Succeeded => true;
            public string Error => null;
            public IAudioClipHandle Load(string location) => this;
            public void Dispose() { }
        }

        [Test] public void OldSoundHandleCannotStopVoiceAfterReinitialize()
        {
            var provider = new ClipProvider();
            var cache = new AudioClipCache(provider, 0f);
            bool running = false;
            var root = new GameObject("audio-session-test");
            var driver = root.AddComponent<AudioRuntimeDriver>();
            var config = new ResolvedAudioConfig(1, 0f, null, null,
                new Dictionary<AudioId, AudioEntry>(),
                new Dictionary<AudioBus, AudioMixerGroup> { [AudioBus.Sfx] = null },
                new Dictionary<AudioBus, string>());
            try
            {
                driver.Initialize(config, cache);
                running = true;
                var entry = new AudioEntry("test", "test", loop: true);
                var options = AudioPlayOptions.Default;
                var firstLease = cache.AcquireAsync("test", AudioLoadMode.OnDemand, default).GetAwaiter().GetResult();
                var first = driver.TryStart(entry, firstLease, in options, 0f);
                Assert.IsTrue(first.IsPlaying);
                driver.Shutdown();
                running = false;
                cache = new AudioClipCache(provider, 0f);
                driver.Initialize(config, cache);
                running = true;
                var secondLease = cache.AcquireAsync("test", AudioLoadMode.OnDemand, default).GetAwaiter().GetResult();
                var second = driver.TryStart(entry, secondLease, in options, 0f);
                Assert.IsTrue(second.IsPlaying);
                Assert.AreEqual(first.Handle.Slot, second.Handle.Slot);
                Assert.AreEqual(first.Handle.Generation, second.Handle.Generation, "仅槽位和代次会碰撞");
                Assert.AreNotEqual(first.Handle, second.Handle);
                Assert.IsFalse(driver.TryStop(first.Handle, 0f));
                Assert.AreEqual(1, driver.GetActiveVoiceCount());
                Assert.Throws<ArgumentOutOfRangeException>(() => driver.StopBus((AudioBus)99, 0f));
                Assert.AreEqual(1, driver.GetActiveVoiceCount());
                Assert.IsTrue(driver.TryStop(second.Handle, 0f));
                driver.Shutdown();
                running = false;
            }
            finally
            {
                if (running) driver.Shutdown();
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(provider.Clip);
            }
        }

        [Test] public void ReusedDefaultOptionsReadSafetyWhenPoolIsCreated()
        {
            var previous = UIFrameSafety.CollectionChecks;
            try
            {
                UIFrameSafety.CollectionChecks = false;
                var options = ManagedPoolOptions.Default;
                UIFrameSafety.CollectionChecks = true;
                using var pool = new ManagedObjectPool<object>(() => new object(), options: options);
                var value = pool.Get();
                pool.Release(value);
                Assert.Throws<InvalidOperationException>(() => pool.Release(value));
            }
            finally { UIFrameSafety.CollectionChecks = previous; }
        }
    }
}
