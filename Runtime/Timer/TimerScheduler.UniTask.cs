using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Timer
{
    public sealed partial class TimerScheduler
    {
        private static readonly TimerCallback DelayCompletedCallback = OnDelayCompleted;
        private readonly ConcurrentQueue<TimerDelayPromise> externalCancellations = new();
        private readonly Queue<TimerDelayPromise> delayCompletions = new();
        private int pendingDelayCount;
        private bool drainingDelayCompletions;
        private Exception delayFailure;

        public async UniTask DelayAsync(
            long delayMs,
            TimerClock clock = TimerClock.Scaled,
            CancellationToken cancellationToken = default)
        {
            EnsureUsable();
            cancellationToken.ThrowIfCancellationRequested();
            if (pendingDelayCount >= nodes.Length)
            {
                throw new TimerCapacityExceededException(
                    $"DelayAsync 未交付任务超过硬容量。SchedulerId={schedulerId}, " +
                    $"PendingDelays={pendingDelayCount}, Capacity={nodes.Length}。");
            }

            pendingDelayCount++;
            var promise = new TimerDelayPromise(this, cancellationToken);
            try
            {
                TimerHandle handle = Schedule(
                    delayMs,
                    DelayCompletedCallback,
                    clock,
                    promise);
                promise.Bind(handle);
            }
            catch
            {
                pendingDelayCount--;
                throw;
            }

            await promise.Task;
        }

        private static void OnDelayCompleted(in TimerContext context)
        {
            ((TimerDelayPromise)context.State).CompleteSuccessfully();
        }

        internal void FailPendingDelays(Exception failure)
        {
            EnsureOwnerThread();
            delayFailure = failure;
            // 先解除所有节点和取消注册，再交付结果，续体不能观察到半完成的收尾。
            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i].State is not TimerDelayPromise promise)
                    continue;
                RemoveFromContainer(i);
                FreeNode(i, false);
                promise.Fail();
            }
            DiscardExternalCancellations();
            // 宿主可能在 Timer 回调内被禁用；此时仍由 Tick 的 finally 交付结果。
            if (!ticking)
                DrainDelayCompletions(true);
        }

        private void DrainExternalCancellations()
        {
            while (externalCancellations.TryDequeue(out TimerDelayPromise promise))
            {
                promise.CompleteCancellationOnSchedulerThread();
            }
        }

        private void EnqueueExternalCancellation(TimerDelayPromise promise)
        {
            externalCancellations.Enqueue(promise);
        }

        private void EnqueueDelayCompletion(TimerDelayPromise promise)
        {
            delayCompletions.Enqueue(promise);
        }

        private void DrainDelayCompletions(bool forceAll = false)
        {
            if (drainingDelayCompletions)
            {
                if (!forceAll)
                {
                    throw new TimerStateException(
                        $"Delay completion 不允许普通重入排空。SchedulerId={schedulerId}。");
                }

                DrainAllDelayCompletions();
                return;
            }

            drainingDelayCompletions = true;
            try
            {
                TimerBudget budget =
                    HasRuntimeClock() ? runtimeBudget : simulationBudget;
                var state = new TickBudgetState(budget);
                while (delayCompletions.Count > 0)
                {
                    if (!forceAll && !CanCompleteDelay(ref state))
                    {
                        break;
                    }

                    TimerDelayPromise promise = delayCompletions.Dequeue();
                    DeliverDelayCompletion(promise);
                    state.ExecutedCallbacks++;
                }
            }
            finally
            {
                drainingDelayCompletions = false;
            }
        }

        private void DrainAllDelayCompletions()
        {
            while (delayCompletions.Count > 0)
            {
                DeliverDelayCompletion(delayCompletions.Dequeue());
            }
        }

        private static void DeliverDelayCompletion(
            TimerDelayPromise promise)
        {
            try
            {
                promise.CompleteTask();
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogException(exception);
            }
        }

        private static bool CanCompleteDelay(ref TickBudgetState state)
        {
            if (state.ExecutedCallbacks >= state.Budget.MaxCallbacksPerTick)
            {
                return false;
            }

            if (!state.Started)
            {
                state.Started = true;
                state.StartTimestamp =
                    state.Budget.MaxExecutionMicroseconds > 0
                        ? Stopwatch.GetTimestamp()
                        : 0;
            }

            return !ExceededTimeBudget(
                state.StartTimestamp,
                state.Budget.MaxExecutionMicroseconds);
        }

        private void DiscardExternalCancellations()
        {
            while (externalCancellations.TryDequeue(out _))
            {
            }
        }

        private void ReleaseDelayPromise()
        {
            if (pendingDelayCount <= 0)
            {
                throw new TimerStateException(
                    $"DelayAsync 待交付计数损坏。SchedulerId={schedulerId}。");
            }

            pendingDelayCount--;
        }

        private sealed class TimerDelayPromise : ITimerShutdownSink
        {
            private const int Pending = 0;
            private const int Succeeded = 1;
            private const int Canceled = 2;
            private const int Faulted = 3;

            private readonly TimerScheduler scheduler;
            private readonly CancellationToken cancellationToken;
            private readonly UniTaskCompletionSource completion = new();

            private CancellationTokenRegistration registration;
            private TimerHandle handle;
            private int outcome;
            private int completionQueued;

            public TimerDelayPromise(
                TimerScheduler scheduler,
                CancellationToken cancellationToken)
            {
                this.scheduler = scheduler;
                this.cancellationToken = cancellationToken;
            }

            public UniTask Task => completion.Task;

            public void Bind(TimerHandle value)
            {
                handle = value;
                if (cancellationToken.CanBeCanceled)
                {
                    registration = cancellationToken.Register(
                        static state =>
                        {
                            var promise = (TimerDelayPromise)state;
                            if (promise.TryClaimCancellation())
                            {
                                promise.scheduler.EnqueueExternalCancellation(promise);
                            }
                        },
                        this);
                }
            }

            public void CompleteSuccessfully()
            {
                if (Interlocked.CompareExchange(
                        ref outcome,
                        Succeeded,
                        Pending) != Pending)
                {
                    return;
                }

                registration.Dispose();
                QueueCompletion();
            }

            public void CompleteCancellationOnSchedulerThread()
            {
                if (Volatile.Read(ref outcome) != Canceled)
                {
                    return;
                }

                scheduler.TryCancel(handle);
                registration.Dispose();
                QueueCompletion();
            }

            public void OnSchedulerShutdown()
            {
                Interlocked.CompareExchange(
                    ref outcome,
                    Canceled,
                    Pending);
                registration.Dispose();
                QueueCompletion();
            }

            public void Fail()
            {
                Interlocked.CompareExchange(ref outcome, Faulted, Pending);
                registration.Dispose();
                QueueCompletion();
            }

            public void CompleteTask()
            {
                scheduler.ReleaseDelayPromise();
                int value = Volatile.Read(ref outcome);
                if (value == Succeeded)
                {
                    completion.TrySetResult();
                    return;
                }

                if (value == Canceled)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                if (value == Faulted)
                {
                    completion.TrySetException(scheduler.delayFailure);
                    return;
                }

                throw new TimerStateException(
                    "TimerDelayPromise 未确定完成结果。");
            }

            private bool TryClaimCancellation()
            {
                return Interlocked.CompareExchange(
                           ref outcome,
                           Canceled,
                           Pending) == Pending;
            }

            private void QueueCompletion()
            {
                if (Interlocked.CompareExchange(
                        ref completionQueued,
                        1,
                        0) == 0)
                {
                    scheduler.EnqueueDelayCompletion(this);
                }
            }
        }
    }
}
