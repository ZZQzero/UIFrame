using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Pooling;
using Game.Timer;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UIFrame.Regression
{
    public class StrictErrorContractTests
    {
        [Test] public void RedDotFailedBindDoesNotRetainSubscription()
        {
            const string path = "Contract/Bind";
            int calls = 0;
            var primary = new InvalidOperationException("bind-failed");
            Action<int> callback = _ => { calls++; throw primary; };
            try
            {
                Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(() => RedDot.Bind(path, callback)));
                RedDot.Set(path, 1);
                RedDot.Flush();
                Assert.AreEqual(1, calls);
            }
            finally { RedDot.Unbind(path, callback); RedDot.Remove(path); }
        }

        [Test] public void RedDotFlushRemovesFailedListenerAndDispatchesOthers()
        {
            const string path = "Contract/Flush";
            var primary = new InvalidOperationException("flush-failed");
            int firstCalls = 0, laterCalls = 0;
            Action<int> first = value => { firstCalls++; if (value > 0) throw primary; };
            Action<int> later = _ => laterCalls++;
            RedDot.Bind(path, first);
            RedDot.Bind(path, later);
            try
            {
                RedDot.Set(path, 1);
                Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(RedDot.Flush));
                Assert.AreEqual(2, laterCalls);
                RedDot.Flush();
                Assert.AreEqual(2, laterCalls);
                RedDot.Set(path, 2);
                RedDot.Flush();
                Assert.AreEqual(2, firstCalls);
                Assert.AreEqual(3, laterCalls);
            }
            finally { RedDot.Unbind(path, first); RedDot.Unbind(path, later); RedDot.Remove(path); }
        }

        [TestCase(TimerCatchUpPolicy.Coalesce)]
        [TestCase(TimerCatchUpPolicy.Skip)]
        [TestCase(TimerCatchUpPolicy.FireAll)]
        public void TimerFailureStopsTickReleasesOwnerAndNeverRepeats(TimerCatchUpPolicy catchUp)
        {
            var clock = new SimulationClock();
            using var scheduler = TimerScheduler.CreateSimulation(clock);
            var owner = scheduler.CreateOwner();
            var primary = new InvalidOperationException("timer-failed");
            int failedCalls = 0, laterCalls = 0, pendingCalls = 0;
            void Pending(in TimerContext _) => pendingCalls++;
            void Failing(in TimerContext _)
            {
                failedCalls++;
                scheduler.Schedule(TimerOptions.Once(0, TimerClock.Simulation), Pending);
                throw primary;
            }
            void Later(in TimerContext _) => laterCalls++;
            scheduler.Schedule(TimerOptions.Repeat(1, 1, clock: TimerClock.Simulation, owner: owner)
                .WithCatchUp(catchUp, 3), Failing);
            scheduler.Schedule(TimerOptions.Once(1, TimerClock.Simulation), Later);
            clock.AdvanceBy(10);
            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(() => scheduler.Tick()));
            Assert.AreEqual(0, laterCalls);
            Assert.AreEqual(0, pendingCalls);
            scheduler.ReleaseOwner(owner);
            scheduler.Tick();
            Assert.AreEqual(1, failedCalls);
            Assert.AreEqual(1, laterCalls);
            Assert.AreEqual(1, pendingCalls);
            Assert.AreEqual(0, scheduler.GetStats().ActiveCount);
        }

        [Test] public void TimerClassifiesFailureByOriginRatherThanExceptionType()
        {
            var primary = new TimerClockException("shared-clock-failed");
            var clock = new FailingTimeSource();
            using var scheduler = new TimerScheduler(clock, TimerClock.Simulation);
            void Fail(in TimerContext _) => throw primary;
            scheduler.Schedule(TimerOptions.Once(0, TimerClock.Simulation), Fail);
            Exception isolated = null;
            Assert.AreSame(primary, Assert.Throws<TimerClockException>(() => scheduler.Tick(out isolated)));
            Assert.AreSame(primary, isolated);
            clock.Failure = primary;
            Assert.AreSame(primary, Assert.Throws<TimerClockException>(() => scheduler.Tick(out isolated)));
            Assert.IsNull(isolated);
        }

        sealed class FailingTimeSource : ITimeSource
        {
            public Exception Failure;
            public long NowMs => Failure == null ? 0 : throw Failure;
        }

        [UnityTest] public IEnumerator RedDotFailureKeepsOtherPathsAndFutureFramesUpdating()
        {
            const string badPath = "Contract/Isolation/A", otherPath = "Contract/Isolation/B";
            int failures = 0, sameValue = -1, otherValue = -1;
            Action<int> bad = value => { if (value > 0) { failures++; throw new InvalidOperationException("red-dot-isolated"); } };
            Action<int> same = value => sameValue = value;
            Action<int> other = value => otherValue = value;
            RedDot.Bind(badPath, bad);
            RedDot.Bind(badPath, same);
            RedDot.Bind(otherPath, other);
            try
            {
                LogAssert.Expect(LogType.Exception, new Regex("red-dot-isolated"));
                RedDot.Set(badPath, 1);
                RedDot.Set(otherPath, 2);
                yield return null;
                yield return null;
                Assert.AreEqual(1, failures);
                Assert.AreEqual(1, sameValue);
                Assert.AreEqual(2, otherValue);
                RedDot.Set(badPath, 3);
                RedDot.Set(otherPath, 4);
                yield return null;
                yield return null;
                Assert.AreEqual(1, failures);
                Assert.AreEqual(3, sameValue);
                Assert.AreEqual(4, otherValue);
            }
            finally
            {
                RedDot.Unbind(badPath, bad);
                RedDot.Unbind(badPath, same);
                RedDot.Unbind(otherPath, other);
                RedDot.Remove("Contract/Isolation");
            }
        }

        [Test] public void RedDotMultipleFailuresPreserveFirstErrorAndDoNotSkipHealthyListener()
        {
            const string path = "Contract/MultipleFailures";
            var primary = new InvalidOperationException("red-dot-first");
            int failures = 0, last = -1;
            Action<int> first = value => { if (value > 0) { failures++; throw primary; } };
            Action<int> second = value => { if (value > 0) { failures++; throw new InvalidOperationException("red-dot-second"); } };
            Action<int> healthy = value => last = value;
            RedDot.Bind(path, first);
            RedDot.Bind(path, second);
            RedDot.Bind(path, healthy);
            try
            {
                RedDot.Set(path, 1);
                LogAssert.Expect(LogType.Exception, new Regex("red-dot-second"));
                Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(RedDot.Flush));
                Assert.AreEqual(1, last);
                RedDot.Set(path, 2);
                RedDot.Flush();
                Assert.AreEqual(2, failures);
                Assert.AreEqual(2, last);
            }
            finally
            {
                RedDot.Unbind(path, first);
                RedDot.Unbind(path, second);
                RedDot.Unbind(path, healthy);
                RedDot.Remove(path);
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void RedDotFailureDoesNotRemoveAnExplicitReplacementSubscription(bool duringBind)
        {
            const string path = "Contract/Replacement";
            var primary = new InvalidOperationException("old-subscription-failed");
            bool replaced = false;
            int last = -1, failures = 0;
            Action<int> callback = null;
            callback = value =>
            {
                if (!replaced && (duringBind || value > 0))
                {
                    RedDot.Unbind(path, callback);
                    replaced = true;
                    RedDot.Bind(path, callback);
                    failures++;
                    throw primary;
                }
                last = value;
            };
            try
            {
                if (duringBind)
                    Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(() => RedDot.Bind(path, callback)));
                else
                {
                    RedDot.Bind(path, callback);
                    RedDot.Set(path, 1);
                    Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(RedDot.Flush));
                }
                RedDot.Set(path, 2);
                RedDot.Flush();
                Assert.AreEqual(1, failures);
                Assert.AreEqual(2, last);
            }
            finally { RedDot.Unbind(path, callback); RedDot.Remove(path); }
        }

        [UnityTest] public IEnumerator RuntimeTimerFailureStopsOnlyTheFailedTask()
        {
            var root = new GameObject("contract-timer-root");
            int calls = 0, laterCalls = 0;
            var primary = new InvalidOperationException("runtime-timer-primary");
            void Failing(in TimerContext _) { calls++; throw primary; }
            void Later(in TimerContext _) => laterCalls++;
            GameTimer.Init(root.transform);
            using var cancellation = new CancellationTokenSource();
            using var scope = new UIFrameScope(default);
            try
            {
                GameTimer.Schedule(TimerOptions.Repeat(0, 1, clock: TimerClock.Unscaled), Failing);
                var later = GameTimer.Schedule(TimerOptions.Once(0, TimerClock.Unscaled), Later);
                var owned = scope.Schedule(TimerOptions.Once(60000, TimerClock.Unscaled), Later);
                var canceled = GameTimer.DelayAsync(60000, TimerClock.Unscaled, cancellation.Token);
                var pending = GameTimer.DelayAsync(60000, TimerClock.Unscaled);
                LogAssert.Expect(LogType.Exception, new Regex("runtime-timer-primary"));
                TickRunner(root);
                Assert.AreEqual(1, calls);
                Assert.AreEqual(0, laterCalls);
                Assert.AreEqual(UniTaskStatus.Pending, pending.Status);
                Assert.IsTrue(root.GetComponentInChildren<UnityTimerRunner>().enabled);
                var due = GameTimer.DelayAsync(0, TimerClock.Unscaled);
                var owner = GameTimer.CreateOwner();
                GameTimer.ReleaseOwner(owner);
                cancellation.Cancel();
                yield return null;
                yield return null;
                Assert.AreEqual(1, calls);
                Assert.AreEqual(1, laterCalls);
                Assert.AreEqual(UniTaskStatus.Succeeded, due.Status);
                due.GetAwaiter().GetResult();
                Assert.Throws<OperationCanceledException>(() => canceled.GetAwaiter().GetResult());
                Assert.AreEqual(UniTaskStatus.Pending, pending.Status);
                Assert.IsTrue(GameTimer.IsActive(owned));
                Assert.IsFalse(GameTimer.TryCancel(later));
                scope.Dispose();
                Assert.AreEqual(0, GameTimer.GetStats().OwnerCount);
                Assert.AreEqual(1, GameTimer.GetStats().ActiveCount);
                GameTimer.Shutdown();
                Assert.Throws<OperationCanceledException>(() => pending.GetAwaiter().GetResult());
            }
            finally
            {
                if (GameTimer.IsInited) GameTimer.Shutdown();
                UnityEngine.Object.Destroy(root);
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void LostRunnerFailsWaitersAndKeepsOwnerCleanup(bool destroy)
        {
            var root = new GameObject("lost-timer-root");
            GameTimer.Init(root.transform);
            try
            {
                var owner = GameTimer.CreateOwner();
                var first = GameTimer.DelayAsync(60000);
                var second = GameTimer.DelayAsync(60000);
                var runner = root.GetComponentInChildren<UnityTimerRunner>();
                LogAssert.Expect(LogType.Exception, new Regex("UnityTimerRunner 被外部禁用"));
                if (destroy) UnityEngine.Object.DestroyImmediate(runner);
                else runner.enabled = false;
                var error = Assert.Throws<TimerStateException>(() => first.GetAwaiter().GetResult());
                Assert.AreSame(error, Assert.Throws<TimerStateException>(() => second.GetAwaiter().GetResult()));
                GameTimer.ReleaseOwner(owner);
                Assert.AreEqual(0, GameTimer.GetStats().ActiveCount);
                Assert.AreEqual(0, GameTimer.GetStats().OwnerCount);
            }
            finally
            {
                GameTimer.Shutdown();
                UnityEngine.Object.Destroy(root);
            }
        }

        [Test] public void RunnerLostInsideCallbackDefersAllCompletionsUntilCallbackReturns()
        {
            var root = new GameObject("callback-stop-timer-root");
            GameTimer.Init(root.transform, new TimerSchedulerOptions { RuntimeBudget = new TimerBudget(1, 0, 8) });
            var waits = new[] { GameTimer.DelayAsync(60000), GameTimer.DelayAsync(60000) };
            bool insideCallback = false, completedInsideCallback = false;
            int completed = 0;
            foreach (var wait in waits)
                wait.GetAwaiter().OnCompleted(() =>
                {
                    completedInsideCallback |= insideCallback;
                    Assert.Throws<TimerStateException>(() => wait.GetAwaiter().GetResult());
                    completed++;
                });
            void Stop(in TimerContext _)
            {
                insideCallback = true;
                root.SetActive(false);
                insideCallback = false;
            }
            try
            {
                GameTimer.Schedule(0, Stop, TimerClock.Unscaled);
                LogAssert.Expect(LogType.Exception, new Regex("UnityTimerRunner 被外部禁用"));
                TickRunner(root);
                Assert.IsFalse(completedInsideCallback);
                Assert.AreEqual(2, completed);
                Assert.AreEqual(0, GameTimer.GetStats().ActiveCount);
            }
            finally
            {
                GameTimer.Shutdown();
                UnityEngine.Object.Destroy(root);
            }
        }

        [Test] public void LostRunnerDelayContinuationCanExplicitlyShutdown()
        {
            var root = new GameObject("continuation-stop-timer-root");
            GameTimer.Init(root.transform);
            var delay = GameTimer.DelayAsync(60000);
            Exception received = null;
            delay.GetAwaiter().OnCompleted(() =>
            {
                try { delay.GetAwaiter().GetResult(); }
                catch (Exception exception) { received = exception; }
                GameTimer.Shutdown();
            });
            try
            {
                LogAssert.Expect(LogType.Exception, new Regex("UnityTimerRunner 被外部禁用"));
                root.GetComponentInChildren<UnityTimerRunner>().enabled = false;
                Assert.IsInstanceOf<TimerStateException>(received);
                Assert.IsFalse(GameTimer.IsInited);
            }
            finally
            {
                if (GameTimer.IsInited) GameTimer.Shutdown();
                UnityEngine.Object.Destroy(root);
            }
        }

        [Test] public void CancellationClaimedBeforeFailureKeepsItsResult()
        {
            var root = new GameObject("cancel-before-failure-root");
            GameTimer.Init(root.transform);
            using var cancellation = new CancellationTokenSource();
            var delay = GameTimer.DelayAsync(60000, cancellationToken: cancellation.Token);
            var primary = new InvalidOperationException("cancel-before-timer-primary");
            void Fail(in TimerContext _) { cancellation.Cancel(); throw primary; }
            try
            {
                GameTimer.Schedule(0, Fail, TimerClock.Unscaled);
                LogAssert.Expect(LogType.Exception, new Regex("cancel-before-timer-primary"));
                TickRunner(root);
                Assert.AreEqual(UniTaskStatus.Canceled, delay.Status);
                Assert.AreEqual(cancellation.Token,
                    Assert.Throws<OperationCanceledException>(() => delay.GetAwaiter().GetResult()).CancellationToken);
                Assert.AreEqual(0, GameTimer.GetStats().ActiveCount);
            }
            finally
            {
                GameTimer.Shutdown();
                UnityEngine.Object.Destroy(root);
            }
        }

        [UnityTest] public IEnumerator HealthyDelayStillCompletesCancelsAndShutsDownNormally()
        {
            var root = new GameObject("healthy-delay-root");
            GameTimer.Init(root.transform);
            using var cancellation = new CancellationTokenSource();
            try
            {
                var due = GameTimer.DelayAsync(0, TimerClock.Unscaled);
                var canceled = GameTimer.DelayAsync(60000, cancellationToken: cancellation.Token);
                var shutdown = GameTimer.DelayAsync(60000);
                cancellation.Cancel();
                yield return null;
                yield return null;
                Assert.AreEqual(UniTaskStatus.Succeeded, due.Status);
                due.GetAwaiter().GetResult();
                Assert.Throws<OperationCanceledException>(() => canceled.GetAwaiter().GetResult());
                Assert.AreEqual(UniTaskStatus.Pending, shutdown.Status);
                GameTimer.Shutdown();
                Assert.Throws<OperationCanceledException>(() => shutdown.GetAwaiter().GetResult());
            }
            finally
            {
                if (GameTimer.IsInited) GameTimer.Shutdown();
                UnityEngine.Object.Destroy(root);
            }
        }

        static void TickRunner(GameObject root)
        {
            typeof(UnityTimerRunner).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(root.GetComponentInChildren<UnityTimerRunner>(true), null);
        }

        [Test] public void TimerDeadlineOverflowIsFailureAndReleasesTimer()
        {
            var clock = new SimulationClock(long.MaxValue - 10);
            using var scheduler = TimerScheduler.CreateSimulation(clock);
            int calls = 0;
            void Callback(in TimerContext _) => calls++;
            scheduler.Schedule(TimerOptions.Repeat(0, 20, clock: TimerClock.Simulation), Callback);
            Assert.Throws<TimerClockException>(() => scheduler.Tick());
            Assert.AreEqual(1, calls);
            Assert.AreEqual(0, scheduler.GetStats().ActiveCount);
            scheduler.Tick();
            Assert.AreEqual(1, calls);
        }

        [Test] public void NegativeUiLayerIsRejectedBeforeCameraMutation()
        {
            var go = new GameObject("contract-camera");
            var root = new GameObject("contract-root").AddComponent<UIFrameRoot>();
            var camera = go.AddComponent<Camera>();
            try
            {
                var components = go.GetComponents<Component>();
                Assert.Throws<InvalidOperationException>(() => root.ConfigureURPCameraStack(camera, null, -2));
                CollectionAssert.AreEqual(components, go.GetComponents<Component>());
            }
            finally
            {
                UnityEngine.Object.Destroy(root.gameObject);
                UnityEngine.Object.Destroy(go);
            }
        }

        [Test] public void PrewarmOverCapacityFailsBeforeLoading()
        {
            var provider = new Provider();
            using var pool = new GameObjectPoolService(provider);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                pool.PrewarmAsync("test", 3, new GameObjectPoolOptions(maxSize: 2)).GetAwaiter().GetResult());
            Assert.AreEqual(0, provider.Handles.Count);
            Assert.AreEqual(0, pool.PoolCount);
        }

        [Test] public void PoolDisposalAttemptsEveryHandleAndReportsFirstFailure()
        {
            var provider = new Provider();
            var pool = new GameObjectPoolService(provider);
            pool.PrepareAsync("first").GetAwaiter().GetResult();
            pool.PrepareAsync("second").GetAwaiter().GetResult();
            var primary = new InvalidOperationException("pool-first");
            provider.Handles[0].Failure = primary;
            provider.Handles[1].Failure = new InvalidOperationException("pool-second");
            LogAssert.Expect(LogType.Exception, new Regex("pool-second"));
            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(pool.Dispose));
            Assert.AreEqual(1, provider.Handles[0].Disposals);
            Assert.AreEqual(1, provider.Handles[1].Disposals);
            Assert.AreEqual(0, pool.PoolCount);
            Assert.IsTrue(pool.IsDisposed);
            pool.Dispose();
            Assert.AreEqual(1, provider.Handles[0].Disposals);
        }

        [Test] public void ManagedPoolDisposalAttemptsAllItems()
        {
            int disposals = 0;
            var primary = new InvalidOperationException("managed-first");
            var pool = new ManagedObjectPool<object>(() => new object(), onDestroy: _ =>
            {
                if (++disposals == 1) throw primary;
            });
            var first = pool.Get();
            var second = pool.Get();
            pool.Release(first);
            pool.Release(second);
            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(pool.Dispose));
            Assert.AreEqual(2, disposals);
            Assert.AreEqual(0, pool.CountInactive);
            pool.Dispose();
            Assert.AreEqual(2, disposals);
        }

        [Test] public void InvalidSafeAreaDoesNotReplaceExistingOverride()
        {
            var valid = new Rect(0, 0, Screen.width, Screen.height);
            ScreenSafeArea.SetOverride(valid);
            try
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => ScreenSafeArea.SetOverride(new Rect(-1, 0, 10, 10)));
                Assert.Throws<ArgumentOutOfRangeException>(() => ScreenSafeArea.SetOverride(new Rect(float.NaN, 0, 10, 10)));
                Assert.Throws<ArgumentOutOfRangeException>(() => ScreenSafeArea.SetOverride(new Rect(0, 0, Screen.width + 1, 10)));
                Assert.AreEqual(valid, ScreenSafeArea.Current.SafeRect);
            }
            finally { ScreenSafeArea.SetOverride(null); }
        }

        sealed class Provider : IPrefabProvider
        {
            public readonly List<Handle> Handles = new();
            public UniTask<IPrefabHandle> LoadAsync(string location)
            {
                var handle = new Handle();
                Handles.Add(handle);
                return UniTask.FromResult<IPrefabHandle>(handle);
            }
        }
        sealed class Handle : IPrefabHandle
        {
            public Exception Failure;
            public int Disposals;
            public GameObject Instantiate(Transform parent) => throw new NotSupportedException();
            public void Dispose() { Disposals++; if (Failure != null) throw Failure; }
        }
    }

    public class ContractPopupPanel : FailurePanel { }

    public class PopupCompletionContractTests
    {
        ContractPopupPanel panel;
        UIManager manager;
        UniTask<int> result;

        [SetUp] public void Setup()
        {
            UI.Init();
            manager = (UIManager)typeof(UI).GetField("_manager", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            panel = new GameObject("popup-contract", typeof(RectTransform)).AddComponent<ContractPopupPanel>();
            panel.Location = "contract/popup";
            UIPanelCatalog.Register<ContractPopupPanel>(panel.Location);
            var cache = (Dictionary<Type, UIPanel>)typeof(UIManager).GetField("_cached", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            cache[typeof(ContractPopupPanel)] = panel;
            result = UI.Popup<ContractPopupPanel, int>();
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (panel != null) panel.OpenAction = panel.CloseAction = panel.DestroyAction = panel.CompleteAction = null;
            UI.Shutdown();
            yield return null;
        }

        [TestCase(false)] [TestCase(true)]
        public void OpeningStillAllowsExplicitClose(bool closeSelf)
        {
            var previous = result;
            panel.OpenAction = () =>
            {
                if (closeSelf) panel.CloseSelf();
                else UI.Close<ContractPopupPanel>();
            };
            result = UI.Popup<ContractPopupPanel, int>();
            Assert.Throws<OperationCanceledException>(() => previous.GetAwaiter().GetResult());
            Assert.Throws<OperationCanceledException>(() => result.GetAwaiter().GetResult());
            Assert.IsFalse(panel.gameObject.activeSelf);
        }

        [Test] public void CancellationContinuationCannotReenterPopupOpening()
        {
            var previous = result;
            Exception nestedError = null;
            bool continued = false;
            previous.GetAwaiter().OnCompleted(() =>
            {
                try { previous.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                continued = true;
                try { UI.Popup<ContractPopupPanel, int>().GetAwaiter().GetResult(); }
                catch (Exception exception) { nestedError = exception; }
            });
            result = UI.Popup<ContractPopupPanel, int>();
            Assert.IsTrue(continued);
            Assert.IsInstanceOf<InvalidOperationException>(nestedError);
            StringAssert.Contains("正在打开", nestedError.Message);
            panel.Submit(17);
            Assert.AreEqual(17, result.GetAwaiter().GetResult());
            // 保护随本次操作结束解除，普通的后续打开不受影响。
            result = UI.Popup<ContractPopupPanel, int>();
            panel.Submit(18);
            Assert.AreEqual(18, result.GetAwaiter().GetResult());
        }

        [TestCase(false)] [TestCase(true)]
        public void SubmittedResultWaitsForCloseAndDestroy(bool destroy)
        {
            panel.CloseAction = () => Assert.AreEqual(UniTaskStatus.Pending, result.Status);
            panel.DestroyAction = () => Assert.AreEqual(UniTaskStatus.Pending, result.Status);
            panel.Submit(42, destroy);
            Assert.AreEqual(42, result.GetAwaiter().GetResult());
        }

        [TestCase(false)] [TestCase(true)]
        public void SubmittedResultReportsCloseOrDestroyFailure(bool destroy)
        {
            var primary = new InvalidOperationException("popup-close-failed");
            if (destroy) panel.DestroyAction = () => throw primary;
            else panel.CloseAction = () => throw primary;
            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(() => panel.Submit(42, destroy)));
            Assert.AreEqual(UniTaskStatus.Faulted, result.Status);
            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(() => result.GetAwaiter().GetResult()));
        }

        [Test] public void CloseWithoutResultCancelsOnlyAfterSuccessfulClose()
        {
            panel.CloseAction = () => Assert.AreEqual(UniTaskStatus.Pending, result.Status);
            panel.CloseSelf();
            Assert.Throws<OperationCanceledException>(() => result.GetAwaiter().GetResult());
        }

        [Test] public void CloseWithoutResultReportsFailureInsteadOfCancellation()
        {
            var primary = new InvalidOperationException("popup-no-result-failed");
            panel.CloseAction = () => throw primary;
            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(() => panel.CloseSelf()));
            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(() => result.GetAwaiter().GetResult()));
        }
    }
}
