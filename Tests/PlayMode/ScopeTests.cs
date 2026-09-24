using System;
using System.Collections;
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

        public void SubscribeToScope()
        {
            OpenScope.Subscribe<ScopeEvent>(_ => EventCount++);
        }

        public void RegisterLifetimeCleanup()
        {
            LifetimeScope.Register(() => CleanupCount++);
        }

        protected override void OnOpen(UINone args)
        {
        }
    }

    public sealed class ScopeTests
    {
        GameObject _object;
        ScopePanel _panel;

        [SetUp]
        public void SetUp()
        {
            _object = new GameObject("ScopePanel");
            _panel = _object.AddComponent<ScopePanel>();
            _panel.DispatchOpen();
            EventSystem.ClearAll();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_panel != null && !_panel.DestroyDispatched)
                _panel.DispatchDestroy();
            if (_object != null)
                UnityEngine.Object.Destroy(_object);
            EventSystem.ClearAll();
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
    }
}
