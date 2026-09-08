using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Timer
{
    public sealed partial class TimerScheduler
    {
        private static readonly TimerCallback DelayCompletedCallback = OnDelayCompleted;
        private readonly ConcurrentQueue<TimerDelayPromise> externalCancellations = new();
        private readonly Queue<TimerDelayPromise> delayCompletions = new();
        private bool drainingDelayCompletions;

        public async UniTask DelayAsync(
            long delayMs,
            TimerClock clock = TimerClock.Scaled,
            CancellationToken cancellationToken = default)
        {
            EnsureUsable();
            cancellationToken.ThrowIfCancellationRequested();

            var promise = new TimerDelayPromise(this, cancellationToken);
            TimerHandle handle = Schedule(
                delayMs,
                DelayCompletedCallback,
                clock,
                promise);
            promise.Bind(handle);
            await promise.Task;
        }

        private static void OnDelayCompleted(in TimerContext context)
        {
            ((TimerDelayPromise)context.State).CompleteSuccessfully();
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

        private void DrainDelayCompletions()
        {
            if (drainingDelayCompletions)
            {
                return;
            }

            drainingDelayCompletions = true;
            try
            {
                while (delayCompletions.Count > 0)
                {
                    delayCompletions.Dequeue().CompleteTask();
                }
            }
            finally
            {
                drainingDelayCompletions = false;
            }
        }

        private void DiscardExternalCancellations()
        {
            while (externalCancellations.TryDequeue(out _))
            {
            }
        }

        private sealed class TimerDelayPromise : ITimerShutdownSink
        {
            private const int Pending = 0;
            private const int Succeeded = 1;
            private const int Canceled = 2;

            private readonly TimerScheduler scheduler;
            private readonly CancellationToken cancellationToken;
            private readonly UniTaskCompletionSource<bool> completion = new();

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

            public UniTask<bool> Task => completion.Task;

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

            public void CompleteTask()
            {
                int value = Volatile.Read(ref outcome);
                if (value == Succeeded)
                {
                    completion.TrySetResult(true);
                    return;
                }

                if (value == Canceled)
                {
                    completion.TrySetCanceled(cancellationToken);
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
