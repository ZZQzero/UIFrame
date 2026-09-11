using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Game.Timer
{
    /// <summary>
    /// 进程内默认 Timer 入口。必须由 Launch 显式 Init/Shutdown。
    /// </summary>
    public static class GameTimer
    {
        private const string RootName = "[GameTimer]";

        private static TimerScheduler scheduler;
        private static UnityTimerRunner runner;
        private static Transform ownedRoot;
        private static bool runnerDestroyedUnexpectedly;
        private static bool shuttingDown;

        public static bool IsInited =>
            !shuttingDown &&
            scheduler != null &&
            !scheduler.IsDisposed;

        public static void Init(
            Transform persistRoot,
            TimerSchedulerOptions options = null)
        {
            if (persistRoot == null)
            {
                throw new ArgumentNullException(nameof(persistRoot));
            }

            if (!persistRoot.gameObject.activeInHierarchy)
            {
                throw new TimerStateException(
                    "GameTimer.Init 要求 persistRoot 处于 activeInHierarchy 状态，" +
                    "否则 UnityTimerRunner 无法驱动 Tick。");
            }

            if (shuttingDown)
            {
                throw new TimerStateException(
                    "GameTimer.Init 被拒绝：GameTimer.Shutdown 正在进行。");
            }

            if (scheduler != null)
            {
                throw new TimerStateException(
                    "GameTimer.Init 重复调用或上次未按流程 Shutdown。");
            }

            var rootObject = new GameObject(RootName);
            rootObject.transform.SetParent(persistRoot, false);
            TimerScheduler createdScheduler = null;
            try
            {
                createdScheduler = TimerScheduler.CreateRuntime(
                    options ?? TimerSchedulerOptions.LargeGameDefault());
                UnityTimerRunner createdRunner =
                    rootObject.AddComponent<UnityTimerRunner>();
                createdRunner.Initialize(createdScheduler);

                ownedRoot = rootObject.transform;
                scheduler = createdScheduler;
                runner = createdRunner;
                runnerDestroyedUnexpectedly = false;
            }
            catch
            {
                if (createdScheduler != null && !createdScheduler.IsDisposed)
                {
                    createdScheduler.Dispose();
                }

                DestroyObject(rootObject);
                throw;
            }
        }

        public static TimerHandle Schedule(
            long delayMs,
            TimerCallback callback,
            TimerClock clock = TimerClock.Scaled,
            object state = null,
            TimerOwner owner = default)
        {
            return RequireScheduler(nameof(Schedule))
                .Schedule(delayMs, callback, clock, state, owner);
        }

        public static TimerHandle Schedule(
            in TimerOptions options,
            TimerCallback callback)
        {
            return RequireScheduler(nameof(Schedule)).Schedule(in options, callback);
        }

        public static TimerHandle ScheduleAt(
            long deadlineMs,
            TimerCallback callback,
            TimerClock clock = TimerClock.Scaled,
            object state = null,
            TimerOwner owner = default)
        {
            return RequireScheduler(nameof(ScheduleAt))
                .ScheduleAt(deadlineMs, callback, clock, state, owner);
        }

        public static bool TrySchedule(
            in TimerOptions options,
            TimerCallback callback,
            out TimerHandle handle)
        {
            return RequireScheduler(nameof(TrySchedule))
                .TrySchedule(in options, callback, out handle);
        }

        public static void Cancel(TimerHandle handle)
        {
            RequireScheduler(nameof(Cancel)).Cancel(handle);
        }

        public static bool TryCancel(TimerHandle handle)
        {
            return RequireScheduler(nameof(TryCancel)).TryCancel(handle);
        }

        public static void Pause(TimerHandle handle)
        {
            RequireScheduler(nameof(Pause)).Pause(handle);
        }

        public static bool TryPause(TimerHandle handle)
        {
            return RequireScheduler(nameof(TryPause)).TryPause(handle);
        }

        public static void Resume(TimerHandle handle)
        {
            RequireScheduler(nameof(Resume)).Resume(handle);
        }

        public static bool TryResume(TimerHandle handle)
        {
            return RequireScheduler(nameof(TryResume)).TryResume(handle);
        }

        public static bool IsActive(TimerHandle handle)
        {
            return RequireScheduler(nameof(IsActive)).IsActive(handle);
        }

        public static long GetRemainingMs(TimerHandle handle)
        {
            return RequireScheduler(nameof(GetRemainingMs)).GetRemainingMs(handle);
        }

        public static bool TryGetRemainingMs(
            TimerHandle handle,
            out long remainingMs)
        {
            return RequireScheduler(nameof(TryGetRemainingMs))
                .TryGetRemainingMs(handle, out remainingMs);
        }

        public static TimerOwner CreateOwner()
        {
            return RequireScheduler(nameof(CreateOwner)).CreateOwner();
        }

        public static int CancelOwner(TimerOwner owner)
        {
            return RequireScheduler(nameof(CancelOwner)).CancelOwner(owner);
        }

        public static void ReleaseOwner(TimerOwner owner)
        {
            RequireScheduler(nameof(ReleaseOwner)).ReleaseOwner(owner);
        }

        public static bool TryReleaseOwner(TimerOwner owner)
        {
            return RequireScheduler(nameof(TryReleaseOwner)).TryReleaseOwner(owner);
        }

        public static UniTask DelayAsync(
            long delayMs,
            TimerClock clock = TimerClock.Scaled,
            CancellationToken cancellationToken = default)
        {
            return RequireScheduler(nameof(DelayAsync))
                .DelayAsync(delayMs, clock, cancellationToken);
        }

        public static TimerSchedulerStats GetStats()
        {
            return RequireScheduler(nameof(GetStats)).GetStats();
        }

        public static void Shutdown()
        {
            if (shuttingDown)
            {
                throw new TimerStateException(
                    "GameTimer.Shutdown 不允许重入。");
            }

            if (scheduler == null)
            {
                throw new TimerStateException(
                    "GameTimer.Shutdown 在未 Init 或已经 Shutdown 后调用。");
            }

            TimerScheduler currentScheduler = scheduler;
            currentScheduler.ValidateShutdown();
            shuttingDown = true;
            UnityTimerRunner currentRunner = runner;
            GameObject rootObject = ownedRoot != null ? ownedRoot.gameObject : null;
            if (currentRunner != null)
            {
                currentRunner.Stop();
            }

            try
            {
                currentScheduler.Dispose();
            }
            finally
            {
                scheduler = null;
                runner = null;
                ownedRoot = null;
                runnerDestroyedUnexpectedly = false;
                shuttingDown = false;
                DestroyObject(rootObject);
            }
        }

        internal static void NotifyRunnerUnavailable(
            UnityTimerRunner unavailable,
            string reason)
        {
            if (runner != unavailable ||
                scheduler == null ||
                runnerDestroyedUnexpectedly)
            {
                return;
            }

            runnerDestroyedUnexpectedly = true;
            Debug.LogError(
                $"[GameTimer] UnityTimerRunner {reason}。禁止直接禁用或销毁 [GameTimer]，" +
                "请修复调用方生命周期，并由 Launch 调用 GameTimer.Shutdown。");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnDomainReload()
        {
            scheduler = null;
            runner = null;
            ownedRoot = null;
            runnerDestroyedUnexpectedly = false;
            shuttingDown = false;
        }

        private static TimerScheduler RequireScheduler(string api)
        {
            if (shuttingDown)
            {
                throw new TimerStateException(
                    $"GameTimer.{api} 被拒绝：GameTimer.Shutdown 正在进行。");
            }

            if (scheduler == null)
            {
                throw new TimerStateException(
                    $"GameTimer.{api} 要求先调用 GameTimer.Init。");
            }

            if (runnerDestroyedUnexpectedly)
            {
                throw new TimerStateException(
                    $"GameTimer.{api} 被拒绝：UnityTimerRunner 已被外部销毁。" +
                    "请修复错误生命周期并调用 GameTimer.Shutdown。");
            }

            return scheduler;
        }

        private static void DestroyObject(GameObject instance)
        {
            if (instance == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(instance);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }
    }

    [AddComponentMenu("")]
    internal sealed class UnityTimerRunner : MonoBehaviour
    {
        private TimerScheduler scheduler;

        internal void Initialize(TimerScheduler value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            if (scheduler != null)
            {
                throw new TimerStateException(
                    "UnityTimerRunner.Initialize 重复调用。");
            }

            scheduler = value;
        }

        internal void Stop()
        {
            if (scheduler == null)
            {
                throw new TimerStateException(
                    "UnityTimerRunner.Stop 要求 Runner 已 Initialize 且未停止。");
            }

            scheduler = null;
            enabled = false;
        }

        private void Update()
        {
            if (scheduler == null)
            {
                throw new TimerStateException(
                    "UnityTimerRunner 未 Initialize，不能 Tick。");
            }

            scheduler.Tick();
        }

        private void OnDestroy()
        {
            if (scheduler != null)
            {
                GameTimer.NotifyRunnerUnavailable(this, "被外部销毁");
            }

            scheduler = null;
        }

        private void OnDisable()
        {
            if (scheduler != null)
            {
                GameTimer.NotifyRunnerUnavailable(this, "被外部禁用");
            }
        }
    }
}
