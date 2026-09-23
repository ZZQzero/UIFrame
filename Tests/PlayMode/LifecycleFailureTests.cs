using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UIFrame.Regression
{
    public class LifecycleFirstPanel : FailurePanel { }
    public class LifecycleSecondPanel : FailurePanel { }
    public class LifecycleThirdPanel : FailurePanel { }

    public class LifecycleFailureTests
    {
        UIManager manager;
        UIFrameRoot root;
        readonly List<FailurePanel> panels = new();
        static T Field<T>(object obj, string name) => (T)obj.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);

        [SetUp] public void Setup()
        {
            manager = new UIManager();
            manager.Init();
            root = Field<UIFrameRoot>(manager, "_root");
        }

        T Prepare<T>(UIOpenMode mode) where T : FailurePanel
        {
            var panel = new GameObject(typeof(T).Name, typeof(RectTransform)).AddComponent<T>();
            panels.Add(panel);
            panel.Location = "review/" + typeof(T).Name;
            UIPanelCatalog.Register<T>(panel.Location, UIGroup.Scene);
            if (mode == UIOpenMode.Toast)
                Field<Dictionary<Type, List<UIPanel>>>(manager, "_toastIdle")[typeof(T)] = new List<UIPanel> { panel };
            else
                Field<Dictionary<Type, UIPanel>>(manager, "_cached")[typeof(T)] = panel;
            return panel;
        }

        T Open<T>(UIOpenMode mode) where T : FailurePanel
        {
            Prepare<T>(mode);
            return manager.Open<T, UINone>(mode, UINone.Value).GetAwaiter().GetResult();
        }

        Exception FailClose(FailurePanel panel)
        {
            var original = new InvalidOperationException("review-close-primary");
            panel.CloseAction = () => throw original;
            Assert.AreSame(original, Assert.Throws<InvalidOperationException>(() => manager.CloseInstance(panel, false)));
            return original;
        }

        [TestCase(false)] [TestCase(true)]
        public void ExplicitDestroyOfFailedWindowRestoresPreviousWindow(bool byType)
        {
            var previous = Open<LifecycleFirstPanel>(UIOpenMode.Push);
            var current = Open<LifecycleSecondPanel>(UIOpenMode.Push);
            Assert.IsFalse(previous.gameObject.activeSelf);
            FailClose(current);
            Assert.AreEqual(0, previous.ResumeCount);
            if (byType) manager.Close(typeof(LifecycleSecondPanel), true);
            else manager.CloseInstance(current, true);
            Assert.IsTrue(current.DestroyDispatched);
            Assert.AreEqual(1, current.CloseCount, "显式销毁不重跑失败的关闭回调");
            Assert.IsTrue(previous.gameObject.activeSelf, "显式销毁故障窗口后底层窗口仍不可见");
            Assert.AreEqual(1, previous.ResumeCount);
        }

        [Test] public void ExplicitDestroyOfFailedPopupClearsMask()
        {
            var popup = Open<LifecycleFirstPanel>(UIOpenMode.Popup);
            var mask = Field<Component>(root, "_maskImage").gameObject;
            Assert.IsTrue(mask.activeSelf);
            FailClose(popup);
            manager.Close(typeof(LifecycleFirstPanel), true);
            Assert.IsFalse(mask.activeSelf, "最后一个弹窗已被显式销毁，但遮罩仍阻挡输入");
        }

        [Test] public void BackMustNotCloseWindowBehindFailedPopup()
        {
            var window = Open<LifecycleFirstPanel>(UIOpenMode.Push);
            var popup = Open<LifecycleSecondPanel>(UIOpenMode.Popup);
            var original = FailClose(popup);
            var error = Assert.Throws<InvalidOperationException>(() => manager.Back());
            Assert.AreSame(original, error.InnerException);
            Assert.AreEqual(0, window.CloseCount);
            Assert.AreEqual(1, popup.CloseCount);
        }

        [Test] public void CloseGroupMustExposePreviouslyFailedMember()
        {
            var panel = Open<LifecycleFirstPanel>(UIOpenMode.Hud);
            var healthy = Open<LifecycleSecondPanel>(UIOpenMode.Hud);
            var original = FailClose(panel);
            Assert.AreSame(original, Assert.Throws<InvalidOperationException>(() => manager.CloseGroup(UIGroup.Scene, false)).InnerException);
            Assert.AreEqual(0, healthy.CloseCount, "必须先检查整个组，不能关闭其它成员后才拒绝");
            Assert.IsTrue(manager.IsOpen<LifecycleSecondPanel>());
        }

        [Test] public void ShutdownInsideFailingCloseMustNotLeaveOwnedPanelAfterShutdown()
        {
            var panel = Open<LifecycleFirstPanel>(UIOpenMode.Hud);
            panel.CloseAction = () => { manager.Shutdown(); throw new InvalidOperationException("review-shutdown-primary"); };
            Assert.Throws<InvalidOperationException>(() => manager.CloseInstance(panel, false));
            Assert.IsFalse(manager.IsInited);
            Assert.IsTrue(panel.DestroyDispatched, "Shutdown 漏掉正在关闭的实例，根节点将外部销毁它");
            Assert.IsEmpty(Field<Dictionary<UIPanel, Exception>>(manager, "_failedPanels"));
        }

        [Test] public void GroupDestroyUsesSameNavigationCompletion()
        {
            var previous = Open<LifecycleFirstPanel>(UIOpenMode.Push);
            previous.Group = UIGroup.Persistent;
            var current = Open<LifecycleSecondPanel>(UIOpenMode.Push);
            FailClose(current);
            manager.CloseGroup(UIGroup.Scene, true);
            Assert.IsTrue(previous.gameObject.activeSelf);
            Assert.AreEqual(1, previous.ResumeCount);
            Assert.AreEqual(1, current.CloseCount);
            Assert.AreEqual(1, current.DestroyCount);
        }

        [Test] public void GroupDestroyDoesNotResumeAnotherMemberBeingDestroyed()
        {
            var previous = Open<LifecycleFirstPanel>(UIOpenMode.Push);
            previous.Group = UIGroup.Persistent;
            var lower = Open<LifecycleSecondPanel>(UIOpenMode.Push);
            var upper = Open<LifecycleThirdPanel>(UIOpenMode.Push);
            // 故障发生顺序与栈顺序相反，分组收尾不能依赖 Dictionary 插入顺序。
            FailClose(upper);
            FailClose(lower);
            manager.CloseGroup(UIGroup.Scene, true);
            Assert.AreEqual(1, lower.DestroyCount);
            Assert.AreEqual(1, upper.DestroyCount);
            Assert.AreEqual(0, lower.ResumeCount);
            Assert.AreEqual(1, previous.ResumeCount);
            Assert.IsTrue(previous.gameObject.activeSelf);
        }

        [TestCase(UIOpenMode.Push)] [TestCase(UIOpenMode.Popup)]
        public void FailedNavigationRejectsNewNavigationBeforeOpening(UIOpenMode mode)
        {
            var window = Open<LifecycleFirstPanel>(UIOpenMode.Push);
            var popup = Open<LifecycleSecondPanel>(UIOpenMode.Popup);
            var original = FailClose(popup);
            var next = Prepare<LifecycleThirdPanel>(mode);
            int opened = 0;
            next.OpenAction = () => opened++;
            Assert.AreSame(original, Assert.Throws<InvalidOperationException>(() =>
                manager.Open<LifecycleThirdPanel, UINone>(mode, UINone.Value).GetAwaiter().GetResult()).InnerException);
            Assert.AreEqual(0, opened);
            Assert.AreEqual(0, window.CloseCount);
            Assert.IsFalse(next.DestroyDispatched, "被拒绝的打开不能触碰现有缓存");
        }

        [Test] public void IndependentHudStillWorksWhenNavigationHasFailed()
        {
            var popup = Open<LifecycleFirstPanel>(UIOpenMode.Popup);
            FailClose(popup);
            var hud = Open<LifecycleSecondPanel>(UIOpenMode.Hud);
            Assert.IsTrue(manager.IsOpen<LifecycleSecondPanel>());
            manager.CloseInstance(hud, false);
            Assert.AreEqual(1, hud.CloseCount);
            Assert.IsTrue(popup.gameObject.activeSelf);
        }

        [Test] public void BackDuringCloseCannotCloseUnderlyingWindow()
        {
            var previous = Open<LifecycleFirstPanel>(UIOpenMode.Push);
            var current = Open<LifecycleSecondPanel>(UIOpenMode.Push);
            current.CloseAction = manager.Back;
            Assert.Throws<InvalidOperationException>(() => manager.CloseInstance(current, false));
            Assert.AreEqual(0, previous.CloseCount);
            Assert.AreEqual(0, previous.ResumeCount);
            Assert.AreEqual(1, current.CloseCount);
        }

        [Test] public void GroupCloseDuringCloseRejectsBeforeTouchingOtherMembers()
        {
            var first = Open<LifecycleFirstPanel>(UIOpenMode.Hud);
            var second = Open<LifecycleSecondPanel>(UIOpenMode.Hud);
            first.CloseAction = () => manager.CloseGroup(UIGroup.Scene, true);
            Assert.Throws<InvalidOperationException>(() => manager.CloseInstance(first, false));
            Assert.AreEqual(0, second.CloseCount);
            Assert.AreEqual(0, second.DestroyCount);
        }

        [Test] public void FailedToastKeepsItsSlotUntilExplicitDestruction()
        {
            manager.ConfigureTips(1, 8, 0f);
            var first = Open<LifecycleFirstPanel>(UIOpenMode.Toast);
            var next = Prepare<LifecycleSecondPanel>(UIOpenMode.Toast);
            var pending = manager.Open<LifecycleSecondPanel, UINone>(UIOpenMode.Toast, UINone.Value);
            Assert.AreEqual(UniTaskStatus.Pending, pending.Status);
            FailClose(first);
            manager.ConfigureTips(1, 8, 0f);
            Assert.AreEqual(UniTaskStatus.Pending, pending.Status, "其它操作不能把失败关闭算作腾出名额");
            manager.CloseInstance(first, true);
            Assert.AreEqual(UniTaskStatus.Succeeded, pending.Status);
            Assert.AreSame(next, pending.GetAwaiter().GetResult());
            Assert.AreEqual(1, first.CloseCount);
        }

        [UnityTest] public IEnumerator FailedDestructionCannotBecomeSuccessOnSecondAttempt()
        {
            var previous = Open<LifecycleFirstPanel>(UIOpenMode.Push);
            var current = Open<LifecycleSecondPanel>(UIOpenMode.Push);
            FailClose(current);
            var destroyError = new InvalidOperationException("destroy-secondary");
            current.DestroyAction = () => throw destroyError;
            Assert.AreSame(destroyError, Assert.Throws<InvalidOperationException>(() => manager.CloseInstance(current, true)));
            Assert.AreEqual(0, previous.ResumeCount);
            Assert.AreEqual(1, current.DestroyCount);
            yield return null; // 验证 Unity 已销毁对象的 null 语义不能掩盖错误。
            Assert.IsTrue(current == null);
            Assert.AreSame(destroyError, Assert.Throws<InvalidOperationException>(() => manager.Close(typeof(LifecycleSecondPanel), true)).InnerException);
            Assert.AreSame(destroyError, Assert.Throws<InvalidOperationException>(manager.Back).InnerException);
            Assert.AreEqual(0, previous.CloseCount);
            Assert.IsFalse(previous.gameObject.activeSelf);
        }

        [Test] public void FailedPanelDestructionCannotReenterItself()
        {
            var current = Open<LifecycleFirstPanel>(UIOpenMode.Hud);
            FailClose(current);
            current.DestroyAction = () => manager.Close(typeof(LifecycleFirstPanel), true);
            Assert.Throws<InvalidOperationException>(() => manager.CloseInstance(current, true));
            Assert.AreEqual(1, current.CloseCount);
            Assert.AreEqual(1, current.DestroyCount);
        }

        [TestCase(false)] [TestCase(true)]
        public void ShutdownDuringClosePreservesPrimaryAndDestroysExactlyOnce(bool secondaryFailure)
        {
            var panel = Open<LifecycleFirstPanel>(UIOpenMode.Hud);
            var primary = new InvalidOperationException("shutdown-close-primary");
            panel.CloseAction = () => { manager.Shutdown(); throw primary; };
            if (secondaryFailure)
            {
                panel.DestroyAction = () => throw new InvalidOperationException("shutdown-destroy-secondary");
                LogAssert.Expect(LogType.Exception, new Regex("shutdown-destroy-secondary"));
            }
            Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(() => manager.CloseInstance(panel, false)));
            Assert.AreEqual(1, panel.CloseCount);
            Assert.AreEqual(1, panel.DestroyCount);
            Assert.IsEmpty(Field<Dictionary<UIPanel, Exception>>(manager, "_failedPanels"));
            Assert.IsEmpty(Field<HashSet<UIPanel>>(manager, "_closingPanels"));
            manager.Shutdown();
            Assert.AreEqual(1, panel.DestroyCount);
        }

        [Test] public void ShutdownDuringExplicitDestructionDoesNotReenterFailedPanel()
        {
            var panel = Open<LifecycleFirstPanel>(UIOpenMode.Popup);
            FailClose(panel);
            panel.DestroyAction = () => manager.Shutdown();
            manager.CloseInstance(panel, true);
            Assert.AreEqual(1, panel.CloseCount);
            Assert.AreEqual(1, panel.DestroyCount);
            Assert.IsFalse(manager.IsInited);
            Assert.IsEmpty(Field<Dictionary<UIPanel, Exception>>(manager, "_failedPanels"));
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            foreach (var panel in panels)
            {
                if (panel == null) continue;
                panel.CloseAction = panel.DestroyAction = panel.OpenAction = panel.CompleteAction = null;
            }
            manager.Shutdown();
            foreach (var panel in panels)
            {
                if (panel == null) continue;
                panel.DispatchDestroy();
                UnityEngine.Object.Destroy(panel.gameObject);
            }
            if (root != null) UnityEngine.Object.Destroy(root.gameObject);
            panels.Clear();
            yield return null;
        }
    }
}
