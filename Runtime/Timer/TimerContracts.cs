using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Timer
{
    public enum TimerClock : byte
    {
        Scaled = 0,
        Unscaled = 1,
        Realtime = 2,
        Simulation = 3
    }

    public enum TimerRepeatMode : byte
    {
        FixedRate = 0,
        FixedDelay = 1
    }

    public enum TimerCatchUpPolicy : byte
    {
        Coalesce = 0,
        Skip = 1,
        FireAll = 2
    }

    public enum TimerCatchUpOverflowPolicy : byte
    {
        Skip = 0,
        Coalesce = 1
    }

    public enum TimerExceptionPolicy : byte
    {
        CancelTimer = 0,
        Continue = 1
    }

    public readonly struct TimerHandle : IEquatable<TimerHandle>
    {
        public int SchedulerId { get; }
        public int Slot { get; }
        public uint Generation { get; }
        public bool IsValid => SchedulerId != 0 && Generation != 0;

        internal TimerHandle(int schedulerId, int slot, uint generation)
        {
            SchedulerId = schedulerId;
            Slot = slot;
            Generation = generation;
        }

        public bool Equals(TimerHandle other)
        {
            return SchedulerId == other.SchedulerId &&
                   Slot == other.Slot &&
                   Generation == other.Generation;
        }

        public override bool Equals(object obj)
        {
            return obj is TimerHandle other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = SchedulerId;
                hash = (hash * 397) ^ Slot;
                hash = (hash * 397) ^ (int)Generation;
                return hash;
            }
        }

        public static bool operator ==(TimerHandle left, TimerHandle right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(TimerHandle left, TimerHandle right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            return $"TimerHandle(Scheduler={SchedulerId}, Slot={Slot}, Generation={Generation})";
        }
    }

    public readonly struct TimerOwner : IEquatable<TimerOwner>
    {
        public int SchedulerId { get; }
        public int Slot { get; }
        public uint Generation { get; }
        public bool IsValid => SchedulerId != 0 && Generation != 0;

        internal TimerOwner(int schedulerId, int slot, uint generation)
        {
            SchedulerId = schedulerId;
            Slot = slot;
            Generation = generation;
        }

        public bool Equals(TimerOwner other)
        {
            return SchedulerId == other.SchedulerId &&
                   Slot == other.Slot &&
                   Generation == other.Generation;
        }

        public override bool Equals(object obj)
        {
            return obj is TimerOwner other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = SchedulerId;
                hash = (hash * 397) ^ Slot;
                hash = (hash * 397) ^ (int)Generation;
                return hash;
            }
        }

        public static bool operator ==(TimerOwner left, TimerOwner right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(TimerOwner left, TimerOwner right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            return $"TimerOwner(Scheduler={SchedulerId}, Slot={Slot}, Generation={Generation})";
        }
    }

    public delegate void TimerCallback(in TimerContext context);

    public readonly struct TimerContext
    {
        public TimerHandle Handle { get; }
        public object State { get; }
        public long ScheduledTimeMs { get; }
        public long ActualTimeMs { get; }
        public int CoalescedFireCount { get; }

        internal TimerContext(
            TimerHandle handle,
            object state,
            long scheduledTimeMs,
            long actualTimeMs,
            int coalescedFireCount)
        {
            Handle = handle;
            State = state;
            ScheduledTimeMs = scheduledTimeMs;
            ActualTimeMs = actualTimeMs;
            CoalescedFireCount = coalescedFireCount;
        }
    }

    public readonly struct TimerOptions
    {
        public TimerClock Clock { get; }
        public long DelayMs { get; }
        public long IntervalMs { get; }
        public int RepeatCount { get; }
        public TimerRepeatMode RepeatMode { get; }
        public TimerCatchUpPolicy CatchUpPolicy { get; }
        public TimerCatchUpOverflowPolicy CatchUpOverflowPolicy { get; }
        public byte MaxCatchUpPerTick { get; }
        public TimerExceptionPolicy ExceptionPolicy { get; }
        public TimerOwner Owner { get; }
        public object State { get; }

        private TimerOptions(
            TimerClock clock,
            long delayMs,
            long intervalMs,
            int repeatCount,
            TimerRepeatMode repeatMode,
            TimerCatchUpPolicy catchUpPolicy,
            TimerCatchUpOverflowPolicy catchUpOverflowPolicy,
            byte maxCatchUpPerTick,
            TimerExceptionPolicy exceptionPolicy,
            TimerOwner owner,
            object state)
        {
            Clock = clock;
            DelayMs = delayMs;
            IntervalMs = intervalMs;
            RepeatCount = repeatCount;
            RepeatMode = repeatMode;
            CatchUpPolicy = catchUpPolicy;
            CatchUpOverflowPolicy = catchUpOverflowPolicy;
            MaxCatchUpPerTick = maxCatchUpPerTick;
            ExceptionPolicy = exceptionPolicy;
            Owner = owner;
            State = state;
        }

        public static TimerOptions Once(
            long delayMs,
            TimerClock clock = TimerClock.Scaled,
            TimerOwner owner = default,
            object state = null)
        {
            return new TimerOptions(
                clock,
                delayMs,
                0,
                1,
                TimerRepeatMode.FixedRate,
                TimerCatchUpPolicy.Coalesce,
                TimerCatchUpOverflowPolicy.Skip,
                1,
                TimerExceptionPolicy.CancelTimer,
                owner,
                state);
        }

        public static TimerOptions Repeat(
            long delayMs,
            long intervalMs,
            int repeatCount = -1,
            TimerClock clock = TimerClock.Scaled,
            TimerOwner owner = default,
            object state = null)
        {
            return new TimerOptions(
                clock,
                delayMs,
                intervalMs,
                repeatCount,
                TimerRepeatMode.FixedRate,
                TimerCatchUpPolicy.Coalesce,
                TimerCatchUpOverflowPolicy.Skip,
                1,
                TimerExceptionPolicy.CancelTimer,
                owner,
                state);
        }

        public TimerOptions WithRepeatMode(TimerRepeatMode value)
        {
            return Copy(repeatMode: value);
        }

        public TimerOptions WithCatchUp(
            TimerCatchUpPolicy value,
            byte maxCatchUpPerTick = 1,
            TimerCatchUpOverflowPolicy overflowPolicy =
                TimerCatchUpOverflowPolicy.Skip)
        {
            return Copy(
                catchUpPolicy: value,
                catchUpOverflowPolicy: overflowPolicy,
                maxCatchUpPerTick: maxCatchUpPerTick);
        }

        public TimerOptions WithExceptionPolicy(TimerExceptionPolicy value)
        {
            return Copy(exceptionPolicy: value);
        }

        public TimerOptions WithOwner(TimerOwner value)
        {
            return Copy(owner: value);
        }

        public TimerOptions WithState(object value)
        {
            return new TimerOptions(
                Clock,
                DelayMs,
                IntervalMs,
                RepeatCount,
                RepeatMode,
                CatchUpPolicy,
                CatchUpOverflowPolicy,
                MaxCatchUpPerTick,
                ExceptionPolicy,
                Owner,
                value);
        }

        private TimerOptions Copy(
            TimerRepeatMode? repeatMode = null,
            TimerCatchUpPolicy? catchUpPolicy = null,
            TimerCatchUpOverflowPolicy? catchUpOverflowPolicy = null,
            byte? maxCatchUpPerTick = null,
            TimerExceptionPolicy? exceptionPolicy = null,
            TimerOwner? owner = null,
            object state = null)
        {
            return new TimerOptions(
                Clock,
                DelayMs,
                IntervalMs,
                RepeatCount,
                repeatMode ?? RepeatMode,
                catchUpPolicy ?? CatchUpPolicy,
                catchUpOverflowPolicy ?? CatchUpOverflowPolicy,
                maxCatchUpPerTick ?? MaxCatchUpPerTick,
                exceptionPolicy ?? ExceptionPolicy,
                owner ?? Owner,
                state ?? State);
        }
    }

    public readonly struct TimerBudget
    {
        public int MaxCallbacksPerTick { get; }
        public long MaxExecutionMicroseconds { get; }
        public int MaxCatchUpCallbacksPerTimer { get; }

        public TimerBudget(
            int maxCallbacksPerTick,
            long maxExecutionMicroseconds,
            int maxCatchUpCallbacksPerTimer)
        {
            MaxCallbacksPerTick = maxCallbacksPerTick;
            MaxExecutionMicroseconds = maxExecutionMicroseconds;
            MaxCatchUpCallbacksPerTimer = maxCatchUpCallbacksPerTimer;
        }

        public static TimerBudget RuntimeDefault => new(4096, 2000, 8);
        public static TimerBudget SimulationDefault => new(4096, 0, 8);
    }

    public sealed class TimerSchedulerOptions
    {
        public int InitialCapacity { get; set; } = 64;
        public int MaxCapacity { get; set; } = 131072;
        public int InitialOwnerCapacity { get; set; } = 16;
        public int MaxOwnerCapacity { get; set; } = 16384;
        public bool AllowRuntimeGrowth { get; set; }
        public int TickResolutionMs { get; set; } = 1;
        public int FastForwardThresholdTicks { get; set; } = 4096;
        public TimerBudget RuntimeBudget { get; set; } = TimerBudget.RuntimeDefault;
        public TimerBudget SimulationBudget { get; set; } = TimerBudget.SimulationDefault;

        public static TimerSchedulerOptions LargeGameDefault()
        {
            return new TimerSchedulerOptions
            {
                InitialCapacity = 100000,
                MaxCapacity = 131072,
                InitialOwnerCapacity = 4096,
                MaxOwnerCapacity = 16384,
                AllowRuntimeGrowth = false,
                TickResolutionMs = 1,
                FastForwardThresholdTicks = 4096,
                RuntimeBudget = TimerBudget.RuntimeDefault,
                SimulationBudget = TimerBudget.SimulationDefault
            };
        }
    }

    public readonly struct TimerSchedulerStats
    {
        public int ActiveCount { get; }
        public int PausedCount { get; }
        public int OwnerCount { get; }
        public int OverflowHeapCount { get; }
        public int PeakActiveCount { get; }
        public int ScheduledThisTick { get; }
        public int CancelledThisTick { get; }
        public int DueThisTick { get; }
        public int ExecutedThisTick { get; }
        public int DeferredThisTick { get; }
        public long OldestOverdueMs { get; }
        public long DroppedCatchUpCount { get; }
        public long FastForwardCount { get; }
        public int Level0Count { get; }
        public int Level1Count { get; }
        public int Level2Count { get; }
        public int Level3Count { get; }
        public long LastTickMicroseconds { get; }
        public long MaxCallbackMicroseconds { get; }
        public int ConsecutiveOverloadTicks { get; }
        public long FastForwardScannedCount { get; }

        internal TimerSchedulerStats(
            int activeCount,
            int pausedCount,
            int ownerCount,
            int overflowHeapCount,
            int peakActiveCount,
            int scheduledThisTick,
            int cancelledThisTick,
            int dueThisTick,
            int executedThisTick,
            int deferredThisTick,
            long oldestOverdueMs,
            long droppedCatchUpCount,
            long fastForwardCount,
            int level0Count,
            int level1Count,
            int level2Count,
            int level3Count,
            long lastTickMicroseconds,
            long maxCallbackMicroseconds,
            int consecutiveOverloadTicks,
            long fastForwardScannedCount)
        {
            ActiveCount = activeCount;
            PausedCount = pausedCount;
            OwnerCount = ownerCount;
            OverflowHeapCount = overflowHeapCount;
            PeakActiveCount = peakActiveCount;
            ScheduledThisTick = scheduledThisTick;
            CancelledThisTick = cancelledThisTick;
            DueThisTick = dueThisTick;
            ExecutedThisTick = executedThisTick;
            DeferredThisTick = deferredThisTick;
            OldestOverdueMs = oldestOverdueMs;
            DroppedCatchUpCount = droppedCatchUpCount;
            FastForwardCount = fastForwardCount;
            Level0Count = level0Count;
            Level1Count = level1Count;
            Level2Count = level2Count;
            Level3Count = level3Count;
            LastTickMicroseconds = lastTickMicroseconds;
            MaxCallbackMicroseconds = maxCallbackMicroseconds;
            ConsecutiveOverloadTicks = consecutiveOverloadTicks;
            FastForwardScannedCount = fastForwardScannedCount;
        }
    }

    public readonly struct TimerTickResult
    {
        public int DueCount { get; }
        public int ExecutedCount { get; }
        public int DeferredCount { get; }

        internal TimerTickResult(int dueCount, int executedCount, int deferredCount)
        {
            DueCount = dueCount;
            ExecutedCount = executedCount;
            DeferredCount = deferredCount;
        }
    }

    public interface ITimerScheduler : IDisposable
    {
        TimerHandle Schedule(
            long delayMs,
            TimerCallback callback,
            TimerClock clock = TimerClock.Scaled,
            object state = null,
            TimerOwner owner = default);

        TimerHandle Schedule(in TimerOptions options, TimerCallback callback);

        TimerHandle ScheduleAt(
            long deadlineMs,
            TimerCallback callback,
            TimerClock clock = TimerClock.Scaled,
            object state = null,
            TimerOwner owner = default);

        bool TrySchedule(
            in TimerOptions options,
            TimerCallback callback,
            out TimerHandle handle);

        void Cancel(TimerHandle handle);
        bool TryCancel(TimerHandle handle);
        int CancelOwner(TimerOwner owner);
        void Pause(TimerHandle handle);
        bool TryPause(TimerHandle handle);
        void Resume(TimerHandle handle);
        bool TryResume(TimerHandle handle);
        bool IsActive(TimerHandle handle);
        long GetRemainingMs(TimerHandle handle);
        bool TryGetRemainingMs(TimerHandle handle, out long remainingMs);
        TimerOwner CreateOwner();
        void ReleaseOwner(TimerOwner owner);
        bool TryReleaseOwner(TimerOwner owner);
        void Reserve(int timerCapacity, int ownerCapacity);
        UniTask DelayAsync(
            long delayMs,
            TimerClock clock = TimerClock.Scaled,
            CancellationToken cancellationToken = default);
        TimerTickResult Tick();
        TimerSchedulerStats GetStats();
        void Clear();
    }

    public class TimerStateException : InvalidOperationException
    {
        public TimerStateException(string message) : base(message)
        {
        }
    }

    public class TimerOwnershipException : InvalidOperationException
    {
        public TimerOwnershipException(string message) : base(message)
        {
        }
    }

    public class TimerThreadException : InvalidOperationException
    {
        public TimerThreadException(string message) : base(message)
        {
        }
    }

    public class TimerCapacityExceededException : InvalidOperationException
    {
        public TimerCapacityExceededException(string message) : base(message)
        {
        }
    }

    public class TimerClockException : InvalidOperationException
    {
        public TimerClockException(string message) : base(message)
        {
        }
    }
}
