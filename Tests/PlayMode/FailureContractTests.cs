using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using Game.Input;
using Game.Scene;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace UIFrame.Regression
{
    public class FailurePanel : UIPanel<UINone, int>
    {
        public Action OpenAction, CloseAction, DestroyAction, CompleteAction, ResumeAction;
        public int CloseCount, DestroyCount, CompleteCount, ResumeCount;
        public bool ThrowOnCancel;
        protected override void OnOpen(UINone args)
        {
            if (ThrowOnCancel)
                OpenCancellationToken.Register(() => throw new InvalidOperationException("cancel-primary"));
            OpenAction?.Invoke();
        }
        protected override void OnClose() { CloseCount++; CloseAction?.Invoke(); }
        protected override void OnDestroyPanel() { DestroyCount++; DestroyAction?.Invoke(); }
        protected override void OnResume() { ResumeCount++; ResumeAction?.Invoke(); }
        protected override void CompleteOpen() { CompleteCount++; base.CompleteOpen(); CompleteAction?.Invoke(); }
    }
    public class OtherPanel : FailurePanel { }

    public class FailureContractTests
    {
        UIManager manager;
        readonly List<GameObject> objects = new();
        static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
        T Panel<T>(bool open = true, UIOpenMode mode = UIOpenMode.Hud) where T : FailurePanel
        {
            var go = new GameObject(typeof(T).Name, typeof(RectTransform));
            objects.Add(go);
            var panel = go.AddComponent<T>();
            panel.OpenMode = mode;
            panel.Location = "test/" + typeof(T).Name;
            if (open)
            {
                if (mode == UIOpenMode.Toast)
                    Field<Dictionary<Type, List<UIPanel>>>(manager, "_toasts")[typeof(T)] = new List<UIPanel> { panel };
                else
                    Field<Dictionary<Type, UIPanel>>(manager, "_opened")[typeof(T)] = panel;
                panel.DispatchOpen();
            }
            return panel;
        }
        [SetUp] public void SetUp()
        {
            manager = new UIManager();
            typeof(UIManager).GetField("_inited", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, true);
        }
        [UnityTearDown] public IEnumerator TearDown()
        {
            // 所有测试对象通过面板销毁入口结束，避免外部 Destroy 告警干扰断言。
            foreach (var go in objects)
            {
                if (go == null) continue;
                var panel = go.GetComponent<FailurePanel>();
                panel.CloseAction = panel.DestroyAction = panel.CompleteAction = panel.OpenAction = null;
                panel.DispatchDestroy();
                UnityEngine.Object.Destroy(go);
            }
            objects.Clear();
            yield return null;
        }

        [Test] public void OrdinaryDestroyPropagatesCallbackFailure()
        {
            var panel = Panel<FailurePanel>();
            var original = new InvalidOperationException("destroy-primary");
            panel.DestroyAction = () => throw original;
            Assert.AreSame(original, Assert.Throws<InvalidOperationException>(() => manager.Close(typeof(FailurePanel), true)));
            Assert.IsTrue(panel.DestroyDispatched);
            Assert.IsFalse(manager.IsOpen<FailurePanel>());
        }

        [TestCase(UIOpenMode.Hud)] [TestCase(UIOpenMode.Toast)]
        public void FailedCloseIsRetainedAndCannotBeReused(UIOpenMode mode)
        {
            var panel = Panel<FailurePanel>(mode: mode);
            var original = new InvalidOperationException("close-primary");
            panel.CloseAction = () => throw original;
            Assert.AreSame(original, Assert.Throws<InvalidOperationException>(() => manager.CloseInstance(panel, false)));
            Assert.IsFalse(Field<Dictionary<Type, UIPanel>>(manager, "_cached").ContainsKey(typeof(FailurePanel)));
            Assert.IsFalse(Field<Dictionary<Type, List<UIPanel>>>(manager, "_toastIdle").ContainsKey(typeof(FailurePanel)));
            Assert.IsTrue(Field<Dictionary<UIPanel, Exception>>(manager, "_failedPanels").ContainsKey(panel));
            Assert.Throws<InvalidOperationException>(() => manager.Open<FailurePanel, UINone>(UIOpenMode.Hud, UINone.Value));
            manager.CloseInstance(panel, true);
            Assert.AreEqual(1, panel.CloseCount, "显式销毁不能重试失败的 OnClose");
            Assert.AreEqual(1, panel.DestroyCount);
        }

        [Test] public void FailedCloseDoesNotResumePreviousWindow()
        {
            var previous = Panel<OtherPanel>();
            var current = Panel<FailurePanel>();
            var stack = Field<List<UIPanel>>(manager, "_windowStack");
            stack.Add(previous); stack.Add(current);
            current.CloseAction = () => throw new InvalidOperationException("close-primary");
            Assert.Throws<InvalidOperationException>(() => manager.CloseInstance(current, false));
            Assert.AreEqual(0, previous.ResumeCount);
        }

        [Test] public void PrimaryCloseExceptionSurvivesCompletionFailure()
        {
            var panel = Panel<FailurePanel>();
            var original = new InvalidOperationException("close-primary");
            panel.CloseAction = () => throw original;
            panel.CompleteAction = () => throw new InvalidOperationException("complete-secondary");
            LogAssert.Expect(LogType.Exception, new Regex("complete-secondary"));
            Assert.AreSame(original, Assert.Throws<InvalidOperationException>(panel.DispatchClose));
        }

        [Test] public void CancellationFailureDisposesScopeAndSettlesResult()
        {
            var panel = Panel<FailurePanel>(false);
            panel.ThrowOnCancel = true;
            panel.DispatchOpen();
            var result = panel.WaitResultAsync();
            Assert.Throws<AggregateException>(panel.DispatchClose);
            Assert.AreEqual(0, panel.CloseCount);
            Assert.AreEqual(1, panel.CompleteCount);
            Assert.AreEqual(UniTaskStatus.Canceled, result.Status);
            Assert.Throws<OperationCanceledException>(() => result.GetAwaiter().GetResult());
            Assert.IsNull(typeof(UIPanel).GetField("_openCts", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel));
        }

        [Test] public void ClearCacheKeepsUntouchedInstancesAfterFirstFailure()
        {
            var first = Panel<FailurePanel>(false);
            var second = Panel<OtherPanel>(false);
            var cache = Field<Dictionary<Type, UIPanel>>(manager, "_cached");
            cache.Add(typeof(FailurePanel), first); cache.Add(typeof(OtherPanel), second);
            first.DestroyAction = () => throw new InvalidOperationException("destroy-primary");
            Assert.Throws<InvalidOperationException>(manager.ClearCache);
            Assert.AreSame(second, cache[typeof(OtherPanel)]);
            Assert.AreEqual(0, second.DestroyCount);
            manager.ClearCache();
            Assert.AreEqual(1, second.DestroyCount);
        }

        [Test] public void ClearToastCachePropagatesAndKeepsUntouchedInstances()
        {
            var first = Panel<FailurePanel>(false);
            var second = Panel<FailurePanel>(false);
            var cache = Field<Dictionary<Type, List<UIPanel>>>(manager, "_toastIdle");
            cache.Add(typeof(FailurePanel), new List<UIPanel> { first, second });
            first.DestroyAction = () => throw new InvalidOperationException("destroy-primary");
            Assert.Throws<InvalidOperationException>(manager.ClearCache);
            CollectionAssert.AreEqual(new[] { second }, cache[typeof(FailurePanel)]);
            Assert.AreEqual(0, second.DestroyCount);
            manager.ClearCache();
        }

        [Test] public void FailedOpenCleanupDetachesPreviouslyCachedInstance()
        {
            var panel = Panel<FailurePanel>();
            manager.CloseInstance(panel, false);
            typeof(UIManager).GetMethod("CleanupFailedOpen", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, new object[] { panel });
            Assert.IsFalse(Field<Dictionary<Type, UIPanel>>(manager, "_cached").ContainsKey(typeof(FailurePanel)));
            Assert.IsTrue(panel.DestroyDispatched);
        }

        [TestCase(UIOpenMode.Hud)] [TestCase(UIOpenMode.Toast)]
        public void SelfCloseThenOpenFailureDoesNotLeaveDestroyedCache(UIOpenMode mode)
        {
            manager = new UIManager();
            manager.Init();
            try
            {
                var panel = Panel<FailurePanel>(false, mode);
                UIPanelCatalog.Register<FailurePanel>(panel.Location);
                if (mode == UIOpenMode.Toast)
                    Field<Dictionary<Type, List<UIPanel>>>(manager, "_toastIdle")[typeof(FailurePanel)] = new List<UIPanel> { panel };
                else
                    Field<Dictionary<Type, UIPanel>>(manager, "_cached")[typeof(FailurePanel)] = panel;
                var original = new InvalidOperationException("open-primary");
                panel.OpenAction = () => { manager.CloseInstance(panel, false); throw original; };
                Assert.AreSame(original, Assert.Throws<InvalidOperationException>(() =>
                    manager.Open<FailurePanel, UINone>(mode, UINone.Value).GetAwaiter().GetResult()));
                Assert.IsFalse(Field<Dictionary<Type, UIPanel>>(manager, "_cached").ContainsKey(typeof(FailurePanel)));
                Assert.IsFalse(Field<Dictionary<Type, List<UIPanel>>>(manager, "_toastIdle").ContainsKey(typeof(FailurePanel)));
                Assert.IsTrue(panel.DestroyDispatched);
            }
            finally { manager.Shutdown(); }
        }

        [Test] public void ReentrantDestroyDuringCloseFailsWithoutRetryingCallback()
        {
            var panel = Panel<FailurePanel>();
            panel.CloseAction = () => manager.Close(typeof(FailurePanel), true);
            Assert.Throws<InvalidOperationException>(() => manager.CloseInstance(panel, false));
            Assert.AreEqual(1, panel.CloseCount);
            Assert.AreEqual(0, panel.DestroyCount);
            Assert.IsTrue(Field<Dictionary<UIPanel, Exception>>(manager, "_failedPanels").ContainsKey(panel));
        }

        [Test] public void SuccessfulCloseCachesAndResumesWindow()
        {
            var previous = Panel<OtherPanel>();
            var current = Panel<FailurePanel>();
            var stack = Field<List<UIPanel>>(manager, "_windowStack");
            stack.Add(previous); stack.Add(current);
            manager.CloseInstance(current, false);
            Assert.AreSame(current, Field<Dictionary<Type, UIPanel>>(manager, "_cached")[typeof(FailurePanel)]);
            Assert.AreEqual(1, previous.ResumeCount);
            Assert.IsFalse(current.gameObject.activeSelf);
            manager.ClearCache();
        }

        [Test] public void DisabledActionsDoNotExposeEarlierPressInSameFrame()
        {
            var previousBackground = InputSystem.settings.backgroundBehavior;
            var previousMode = InputSystem.settings.updateMode;
#if UNITY_EDITOR
            var previousEditorBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsManually;
            var keyboard = InputSystem.AddDevice<Keyboard>();
            using var attack = new InputAction("Attack", binding: "<Keyboard>/space");
            using var move = new InputAction("Move", InputActionType.Value);
            try
            {
                var controls = new PlayerControls(move, null, attack, attack, null, 1f);
                var ui = new UiControls(null, attack);
                attack.Enable();
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Space));
                InputSystem.Update();
                Assert.IsTrue(keyboard.spaceKey.isPressed, "测试输入事件必须被实际处理");
                Assert.IsTrue(controls.AttackPressed);
                attack.Disable();
                Assert.IsTrue(attack.WasPressedThisFrame(), "验证实际 Input System 的同帧历史行为");
                Assert.IsFalse(controls.AttackPressed);
                Assert.IsFalse(controls.JumpPressed);
                Assert.IsFalse(ui.CancelPressed);
            }
            finally
            {
                InputSystem.RemoveDevice(keyboard);
                InputSystem.settings.updateMode = previousMode;
                InputSystem.settings.backgroundBehavior = previousBackground;
#if UNITY_EDITOR
                InputSystem.settings.editorInputBehaviorInPlayMode = previousEditorBehavior;
#endif
            }
        }

        sealed class FakeScene : ISceneHandle
        {
            public LoadSceneMode Mode => LoadSceneMode.Additive;
            public bool IsPreloaded { get; private set; } = true;
            public int Unloads;
            public UniTask ActivateAsync() { IsPreloaded = false; return UniTask.CompletedTask; }
            public UniTask UnloadAsync() { Unloads++; return UniTask.CompletedTask; }
        }
        sealed class FakeLoader : ISceneLoader
        {
            public readonly FakeScene Scene = new();
            public int Loads;
            public UniTask<ISceneHandle> LoadAsync(string location, LoadSceneMode mode, bool allowSceneActivation, Action<float> report)
            { Loads++; return UniTask.FromResult<ISceneHandle>(Scene); }
        }
        [Test] public void SuspendedSceneRejectsUnloadAndFollowingLoadButCanActivate()
        {
            var loader = new FakeLoader();
            GameScene.InitLoader(loader);
            try
            {
                GameScene.PreloadAsync("A", LoadSceneMode.Additive).GetAwaiter().GetResult();
                Assert.Throws<InvalidOperationException>(() => GameScene.UnloadAsync("A"));
                Assert.Throws<InvalidOperationException>(() => GameScene.LoadAsync("B", LoadSceneMode.Additive));
                Assert.Throws<InvalidOperationException>(() => GameScene.LoadBuiltinAsync("B"));
                Assert.AreEqual(1, loader.Loads);
                Assert.AreEqual(0, loader.Scene.Unloads);
                Assert.IsTrue(GameScene.IsPreloaded("A"));
                Assert.IsFalse(GameScene.IsBusy);
                GameScene.ActivateAsync("A").GetAwaiter().GetResult();
                GameScene.UnloadAsync("A").GetAwaiter().GetResult();
                Assert.AreEqual(1, loader.Scene.Unloads);
                Assert.IsFalse(GameScene.IsLoaded("A"));
            }
            finally { GameScene.ShutdownAsync().GetAwaiter().GetResult(); }
        }
    }
}
