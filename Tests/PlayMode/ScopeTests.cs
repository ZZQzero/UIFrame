using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Game.Timer;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Game;

namespace UIFrame.Regression
{
    struct ScopeEvent
    {
    }

    sealed class ScopePanel : UIPanel<UINone>
    {
        public int EventCount;
        public int CleanupCount;

        public CancellationToken OpenToken => OpenCancellationToken;

        public void SubscribeToScope()
        {
            OpenScope.Subscribe<ScopeEvent>(_ => EventCount++);
        }

        public void RegisterLifetimeCleanup()
        {
            LifetimeScope.Register(() => CleanupCount++);
        }

        public void RegisterOpenCleanup(Action cleanup)
        {
            OpenScope.Register(cleanup);
        }

        public void RegisterOpenDisposable(IDisposable disposable)
        {
            OpenScope.Register(disposable);
        }

        public TimerHandle ScheduleOpenTimer(TimerOptions options, Game.Timer.TimerCallback callback)
        {
            return OpenScope.Schedule(in options, callback);
        }

        protected override void OnOpen(UINone args)
        {
        }
    }

    public sealed class ScopeTests
    {
        GameObject _object;
        ScopePanel _panel;
        GameObject _timerRoot;

        [SetUp]
        public void SetUp()
        {
            _object = new GameObject("ScopePanel");
            _panel = _object.AddComponent<ScopePanel>();
            _panel.DispatchOpen();
            EventSystem.ClearAll();
            _timerRoot = new GameObject("ScopeTimerRoot");
            GameTimer.Init(_timerRoot.transform);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_panel != null && !_panel.DestroyDispatched)
                _panel.DispatchDestroy();
            if (_object != null)
                UnityEngine.Object.Destroy(_object);
            EventSystem.ClearAll();
            if (GameTimer.IsInited)
                GameTimer.Shutdown();
            if (_timerRoot != null)
                UnityEngine.Object.Destroy(_timerRoot);
            yield return null;
        }

        [Test]
        public void OpenScopeRemovesEventSubscriptionOnClose()
        {
            _panel.SubscribeToScope();
            EventSystem.Publish(new ScopeEvent());
            Assert.That(_panel.EventCount, Is.EqualTo(1));

            _panel.DispatchClose();
            EventSystem.Publish(new ScopeEvent());
            Assert.That(_panel.EventCount, Is.EqualTo(1));
        }

        [Test]
        public void OpenTokenIsCancelledAfterClose()
        {
            CancellationToken token = _panel.OpenToken;
            Assert.That(token.IsCancellationRequested, Is.False);

            _panel.DispatchClose();

            Assert.That(token.IsCancellationRequested, Is.True);
            Assert.That(_panel.OpenToken.IsCancellationRequested, Is.True);
        }

        [Test]
        public void RegisteredActionRunsWhenOpenScopeCloses()
        {
            int count = 0;
            _panel.RegisterOpenCleanup(() => count++);

            _panel.DispatchClose();

            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void RegisteredDisposableRunsWhenOpenScopeCloses()
        {
            var disposable = new ProbeDisposable();
            _panel.RegisterOpenDisposable(disposable);

            _panel.DispatchClose();

            Assert.That(disposable.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void AllCleanupsRunAndFirstCleanupExceptionIsPreserved()
        {
            var order = new List<int>();
            var first = new InvalidOperationException("first");
            var second = new InvalidOperationException("second");
            _panel.RegisterOpenCleanup(() =>
            {
                order.Add(1);
                throw first;
            });
            _panel.RegisterOpenCleanup(() =>
            {
                order.Add(2);
                throw second;
            });

            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("InvalidOperationException: first"));
            var exception = Assert.Throws<InvalidOperationException>(() => _panel.DispatchClose());

            Assert.AreSame(second, exception);
            CollectionAssert.AreEqual(new[] { 2, 1 }, order);
        }

        [Test]
        public void OpenScopeCancelsAndRecreatesAfterCachedClose()
        {
            CancellationToken oldToken = _panel.OpenToken;
            _panel.DispatchClose();

            Assert.That(oldToken.IsCancellationRequested, Is.True);
            Assert.That(_panel.CleanupCount, Is.EqualTo(0));

            _panel.DispatchOpen();

            Assert.That(_panel.OpenToken.IsCancellationRequested, Is.False);
            Assert.That(_panel.OpenToken, Is.Not.EqualTo(oldToken));
        }

        [Test]
        public void TimerOwnerIsCancelledAndReleasedWithOpenScope()
        {
            int callbackCount = 0;
            void OnTimer(in TimerContext _) => callbackCount++;

            _panel.ScheduleOpenTimer(TimerOptions.Once(100, TimerClock.Unscaled), OnTimer);
            Assert.That(GameTimer.GetStats().ActiveCount, Is.EqualTo(1));
            Assert.That(GameTimer.GetStats().OwnerCount, Is.EqualTo(1));

            _panel.DispatchClose();

            Assert.That(GameTimer.GetStats().ActiveCount, Is.EqualTo(0));
            Assert.That(GameTimer.GetStats().OwnerCount, Is.EqualTo(0));
        }

        [Test]
        public void LifetimeScopeWaitsUntilPanelDestroy()
        {
            int openCleanupCount = 0;
            _panel.RegisterOpenCleanup(() => openCleanupCount++);
            _panel.RegisterLifetimeCleanup();

            _panel.DispatchClose();

            Assert.That(openCleanupCount, Is.EqualTo(1));
            Assert.That(_panel.CleanupCount, Is.EqualTo(0));

            _panel.DispatchDestroy();

            Assert.That(_panel.CleanupCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator LifetimeScopeRunsOnDestroy()
        {
            _panel.RegisterLifetimeCleanup();
            Assert.That(_panel.CleanupCount, Is.EqualTo(0));

            _panel.DispatchDestroy();
            UnityEngine.Object.Destroy(_object);
            yield return null;

            Assert.That(_panel.CleanupCount, Is.EqualTo(1));
        }

        sealed class ProbeDisposable : IDisposable
        {
            public int DisposeCount;

            public void Dispose()
            {
                DisposeCount++;
            }
        }
    }
}
