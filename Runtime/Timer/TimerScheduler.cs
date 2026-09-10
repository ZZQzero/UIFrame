using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using Unity.Profiling;

namespace Game.Timer
{
    public sealed partial class TimerScheduler : IDisposable
    {
        private const int Level0Size = 256;
        private const int LevelSize = 64;
        private const int Level1Offset = Level0Size;
        private const int Level2Offset = Level1Offset + LevelSize;
        private const int Level3Offset = Level2Offset + LevelSize;
        private const int BucketCount = Level3Offset + LevelSize;
        private const long Level0Range = 1L << 8;
        private const long Level1Range = 1L << 14;
        private const long Level2Range = 1L << 20;
        private const long WheelRange = 1L << 26;

        private static int nextSchedulerId;
        private static readonly ProfilerMarker TickProfilerMarker =
            new("Game.Timer.Tick");
        private static readonly ProfilerMarker FastForwardProfilerMarker =
            new("Game.Timer.FastForward");
        private static readonly ProfilerMarker DispatchProfilerMarker =
            new("Game.Timer.Dispatch");
        private static readonly ProfilerMarker CallbackProfilerMarker =
            new("Game.Timer.Callback");

        private readonly int schedulerId;
        private readonly int ownerThreadId;
        private readonly int maxCapacity;
        private readonly int maxOwnerCapacity;
        private readonly bool allowRuntimeGrowth;
        private readonly int resolutionMs;
        private readonly int fastForwardThresholdTicks;
        private readonly TimerBudget runtimeBudget;
        private readonly TimerBudget simulationBudget;
        private readonly ClockState[] clocks = new ClockState[4];

        private TimerNode[] nodes;
        private int[] freeSlots;
        private int freeSlotCount;
        private OwnerSlot[] owners;
        private int[] freeOwnerSlots;
        private int freeOwnerSlotCount;
        private int pendingHead = -1;
        private int pendingTail = -1;

        private long nextSequence;
        private int runtimeClockStartIndex;
        private bool tickCallActive;
        private bool ticking;
        private bool clearing;
        private bool shuttingDown;
        private bool disposed;
        private int activeCount;
        private int pausedCount;
        private int ownerCount;
        private int peakActiveCount;
        private int scheduledThisTick;
        private int cancelledThisTick;
        private int scheduledSinceLastTick;
        private int cancelledSinceLastTick;
        private int dueThisTick;
        private int executedThisTick;
        private int deferredThisTick;
        private long oldestOverdueMs;
        private long droppedCatchUpCount;
        private long fastForwardCount;
        private long fastForwardScannedCount;
        private long lastTickMicroseconds;
        private long maxCallbackMicroseconds;
        private bool runtimeGrowthWarningWritten;

        public int SchedulerId => schedulerId;
        public bool IsDisposed => disposed;

        public TimerScheduler(
            ITimeSource timeSource,
            TimerClock clock,
            TimerSchedulerOptions options = null)
            : this(options)
        {
            if (timeSource == null)
            {
                throw new ArgumentNullException(nameof(timeSource));
            }

            ValidateClock(clock);
            clocks[(int)clock] = new ClockState(timeSource, nodes.Length);
        }

        private TimerScheduler(TimerSchedulerOptions options)
        {
            TimerSchedulerOptions value = options ?? new TimerSchedulerOptions();
            ValidateSchedulerOptions(value);

            schedulerId = Interlocked.Increment(ref nextSchedulerId);
            if (schedulerId == 0)
            {
                throw new TimerStateException(
                    "TimerSchedulerId 已溢出到无效值 0，不能继续创建 Scheduler。");
            }

            ownerThreadId = Thread.CurrentThread.ManagedThreadId;
            maxCapacity = value.MaxCapacity;
            maxOwnerCapacity = value.MaxOwnerCapacity;
            allowRuntimeGrowth = value.AllowRuntimeGrowth;
            resolutionMs = value.TickResolutionMs;
            fastForwardThresholdTicks = value.FastForwardThresholdTicks;
            runtimeBudget = value.RuntimeBudget;
            simulationBudget = value.SimulationBudget;

            nodes = new TimerNode[value.InitialCapacity];
            freeSlots = new int[value.InitialCapacity];
            for (int i = value.InitialCapacity - 1; i >= 0; i--)
            {
                freeSlots[freeSlotCount++] = i;
            }

            owners = new OwnerSlot[value.InitialOwnerCapacity];
            freeOwnerSlots = new int[value.InitialOwnerCapacity];
            for (int i = value.InitialOwnerCapacity - 1; i >= 0; i--)
            {
                freeOwnerSlots[freeOwnerSlotCount++] = i;
            }
        }

        public static TimerScheduler CreateRuntime(TimerSchedulerOptions options = null)
        {
            var scheduler = new TimerScheduler(options);
            scheduler.clocks[(int)TimerClock.Scaled] =
                new ClockState(new UnityTimeSource(UnityTimeKind.Scaled), scheduler.nodes.Length);
            scheduler.clocks[(int)TimerClock.Unscaled] =
                new ClockState(new UnityTimeSource(UnityTimeKind.Unscaled), scheduler.nodes.Length);
            scheduler.clocks[(int)TimerClock.Realtime] =
                new ClockState(new UnityTimeSource(UnityTimeKind.Realtime), scheduler.nodes.Length);
            return scheduler;
        }

        public static TimerScheduler CreateSimulation(
            SimulationClock clock,
            TimerSchedulerOptions options = null)
        {
            if (clock == null)
            {
                throw new ArgumentNullException(nameof(clock));
            }

            return new TimerScheduler(clock, TimerClock.Simulation, options);
        }

        public TimerHandle Schedule(
            long delayMs,
            TimerCallback callback,
            TimerClock clock = TimerClock.Scaled,
            object state = null,
            TimerOwner owner = default)
        {
            TimerOptions options = TimerOptions.Once(delayMs, clock, owner, state);
            return Schedule(in options, callback);
        }

        public TimerHandle Schedule(in TimerOptions options, TimerCallback callback)
        {
            EnsureUsable();
            ValidateTimerOptions(in options, callback, nameof(Schedule));
            long nowMs = GetClockNowForApi(options.Clock);
            long dueTimeMs;
            try
            {
                dueTimeMs = checked(nowMs + options.DelayMs);
            }
            catch (OverflowException)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.DelayMs,
                    $"Schedule 时间溢出。SchedulerId={schedulerId}, NowMs={nowMs}。");
            }

            if (!TryScheduleCore(
                    in options,
                    callback,
                    dueTimeMs,
                    nameof(Schedule),
                    out TimerHandle handle))
            {
                throw new TimerCapacityExceededException(
                    $"Schedule 超过 Timer 硬容量。SchedulerId={schedulerId}, " +
                    $"Active={activeCount}, Capacity={nodes.Length}, MaxCapacity={maxCapacity}。");
            }

            return handle;
        }

        public TimerHandle ScheduleAt(
            long deadlineMs,
            TimerCallback callback,
            TimerClock clock = TimerClock.Scaled,
            object state = null,
            TimerOwner owner = default)
        {
            EnsureUsable();
            if (deadlineMs < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(deadlineMs),
                    deadlineMs,
                    "ScheduleAt Deadline 不能小于 0。");
            }

            TimerOptions options = TimerOptions.Once(0, clock, owner, state);
            ValidateTimerOptions(in options, callback, nameof(ScheduleAt));
            GetClockNowForApi(clock);
            if (!TryScheduleCore(
                    in options,
                    callback,
                    deadlineMs,
                    nameof(ScheduleAt),
                    out TimerHandle handle))
            {
                throw new TimerCapacityExceededException(
                    $"ScheduleAt 超过 Timer 硬容量。SchedulerId={schedulerId}, " +
                    $"Active={activeCount}, Capacity={nodes.Length}, MaxCapacity={maxCapacity}。");
            }

            return handle;
        }

        public bool TrySchedule(
            in TimerOptions options,
            TimerCallback callback,
            out TimerHandle handle)
        {
            EnsureUsable();
            ValidateTimerOptions(in options, callback, nameof(TrySchedule));
            long nowMs = GetClockNowForApi(options.Clock);
            long dueTimeMs;
            try
            {
                dueTimeMs = checked(nowMs + options.DelayMs);
            }
            catch (OverflowException)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.DelayMs,
                    $"TrySchedule 时间溢出。SchedulerId={schedulerId}, NowMs={nowMs}。");
            }

            return TryScheduleCore(
                in options,
                callback,
                dueTimeMs,
                nameof(TrySchedule),
                out handle);
        }

        public void Cancel(TimerHandle handle)
        {
            EnsureUsable();
            int slot = RequireActiveHandle(handle, nameof(Cancel));
            CancelSlot(slot, true);
        }

        public bool TryCancel(TimerHandle handle)
        {
            EnsureUsable();
            if (!TryGetActiveSlot(handle, nameof(TryCancel), out int slot))
            {
                return false;
            }

            CancelSlot(slot, true);
            return true;
        }

        public int CancelOwner(TimerOwner owner)
        {
            EnsureUsable();
            int ownerSlot = RequireOwner(owner, nameof(CancelOwner));
            int cancelled = 0;
            int slot = owners[ownerSlot].Head;
            while (slot >= 0)
            {
                int next = nodes[slot].NextInOwner;
                CancelSlot(slot, true);
                cancelled++;
                slot = next;
            }

            return cancelled;
        }

        public void Pause(TimerHandle handle)
        {
            EnsureUsable();
            int slot = RequireActiveHandle(handle, nameof(Pause));
            if (!TryPauseSlot(slot))
            {
                throw StateError(nameof(Pause), handle, "Scheduled 或可重复的 Executing", nodes[slot].Status);
            }
        }

        public bool TryPause(TimerHandle handle)
        {
            EnsureUsable();
            if (!TryGetActiveSlot(handle, nameof(TryPause), out int slot))
            {
                return false;
            }

            return TryPauseSlot(slot);
        }

        public void Resume(TimerHandle handle)
        {
            EnsureUsable();
            int slot = RequireActiveHandle(handle, nameof(Resume));
            if (!TryResumeSlot(slot))
            {
                throw StateError(nameof(Resume), handle, "Paused", nodes[slot].Status);
            }
        }

        public bool TryResume(TimerHandle handle)
        {
            EnsureUsable();
            if (!TryGetActiveSlot(handle, nameof(TryResume), out int slot))
            {
                return false;
            }

            return TryResumeSlot(slot);
        }

        public bool IsActive(TimerHandle handle)
        {
            EnsureUsable();
            ValidateHandleIdentity(handle, nameof(IsActive));
            return (uint)handle.Slot < (uint)nodes.Length &&
                   nodes[handle.Slot].Generation == handle.Generation &&
                   nodes[handle.Slot].Status != TimerNodeStatus.Free &&
                   nodes[handle.Slot].Status != TimerNodeStatus.Cancelled;
        }

        public long GetRemainingMs(TimerHandle handle)
        {
            EnsureUsable();
            int slot = RequireActiveHandle(handle, nameof(GetRemainingMs));
            return GetRemainingMs(slot);
        }

        public bool TryGetRemainingMs(TimerHandle handle, out long remainingMs)
        {
            EnsureUsable();
            if (!TryGetActiveSlot(handle, nameof(TryGetRemainingMs), out int slot))
            {
                remainingMs = 0;
                return false;
            }

            remainingMs = GetRemainingMs(slot);
            return true;
        }

        public TimerOwner CreateOwner()
        {
            EnsureUsable();
            if (!TryAllocOwner(out int slot))
            {
                throw new TimerCapacityExceededException(
                    $"CreateOwner 超过 Owner 硬容量。SchedulerId={schedulerId}, " +
                    $"ActiveOwners={ownerCount}, Capacity={owners.Length}, MaxCapacity={maxOwnerCapacity}。");
            }

            ref OwnerSlot value = ref owners[slot];
            if (value.Generation == 0)
            {
                value.Generation = 1;
            }

            value.Active = true;
            value.Head = -1;
            value.TimerCount = 0;
            ownerCount++;
            return new TimerOwner(schedulerId, slot, value.Generation);
        }

        public void ReleaseOwner(TimerOwner owner)
        {
            EnsureUsable();
            int slot = RequireOwner(owner, nameof(ReleaseOwner));
            if (owners[slot].TimerCount != 0)
            {
                throw new TimerStateException(
                    $"ReleaseOwner 要求 Owner 无活跃 Timer。SchedulerId={schedulerId}, " +
                    $"OwnerSlot={slot}, Generation={owner.Generation}, " +
                    $"TimerCount={owners[slot].TimerCount}。请先 CancelOwner。");
            }

            FreeOwner(slot);
        }

        public bool TryReleaseOwner(TimerOwner owner)
        {
            EnsureUsable();
            ValidateOwnerIdentity(owner, nameof(TryReleaseOwner));
            if ((uint)owner.Slot >= (uint)owners.Length)
            {
                return false;
            }

            ref OwnerSlot slot = ref owners[owner.Slot];
            if (!slot.Active || slot.Generation != owner.Generation || slot.TimerCount != 0)
            {
                return false;
            }

            FreeOwner(owner.Slot);
            return true;
        }

        public void Reserve(int timerCapacity, int ownerCapacity)
        {
            EnsureUsable();
            if (ticking || activeCount != 0 || ownerCount != 0)
            {
                throw new TimerStateException(
                    $"Reserve 只能在 Scheduler 空闲时调用。SchedulerId={schedulerId}, " +
                    $"Ticking={ticking}, Active={activeCount}, Owners={ownerCount}。");
            }

            if (timerCapacity <= 0 || timerCapacity > maxCapacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timerCapacity),
                    timerCapacity,
                    $"Timer 容量必须在 1..{maxCapacity}。");
            }

            if (ownerCapacity <= 0 || ownerCapacity > maxOwnerCapacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ownerCapacity),
                    ownerCapacity,
                    $"Owner 容量必须在 1..{maxOwnerCapacity}。");
            }

            if (timerCapacity > nodes.Length)
            {
                GrowNodes(timerCapacity);
            }

            if (ownerCapacity > owners.Length)
            {
                GrowOwners(ownerCapacity);
            }
        }

        public TimerTickResult Tick()
        {
            EnsureUsable();
            if (tickCallActive)
            {
                throw new TimerStateException(
                    $"Tick 不允许重入。SchedulerId={schedulerId}。");
            }

            tickCallActive = true;
            try
            {
                return TickCore();
            }
            finally
            {
                tickCallActive = false;
            }
        }

        private TimerTickResult TickCore()
        {
            using ProfilerMarker.AutoScope _ = TickProfilerMarker.Auto();
            bool measureTick = HasRuntimeClock();
            long tickStartTimestamp = measureTick ? Stopwatch.GetTimestamp() : 0;
            var runtimeBudgetState = new TickBudgetState(runtimeBudget);
            var simulationBudgetState = new TickBudgetState(simulationBudget);
            ticking = true;
            ResetTickStats();
            try
            {
                DrainExternalCancellations();
                CaptureClockSnapshots();
                int firstRuntimeClock = runtimeClockStartIndex;
                runtimeClockStartIndex =
                    (runtimeClockStartIndex + 1) % (int)TimerClock.Simulation;
                for (int offset = 0;
                     offset < (int)TimerClock.Simulation;
                     offset++)
                {
                    int i =
                        (firstRuntimeClock + offset) %
                        (int)TimerClock.Simulation;
                    ClockState clock = clocks[i];
                    if (clock == null)
                    {
                        continue;
                    }

                    ProcessClock(
                        clock,
                        (TimerClock)i,
                        ref runtimeBudgetState);
                }

                ClockState simulationClock = clocks[(int)TimerClock.Simulation];
                if (simulationClock != null)
                {
                    ProcessClock(
                        simulationClock,
                        TimerClock.Simulation,
                        ref simulationBudgetState);
                }
            }
            finally
            {
                try
                {
                    CommitPending();
                }
                finally
                {
                    ticking = false;
                }

                try
                {
                    DrainExternalCancellations();
                    DrainDelayCompletions();
                }
                finally
                {
                    lastTickMicroseconds = measureTick
                        ? ToMicroseconds(Stopwatch.GetTimestamp() - tickStartTimestamp)
                        : 0;
                }
            }

            return new TimerTickResult(dueThisTick, executedThisTick, deferredThisTick);
        }

        public TimerSchedulerStats GetStats()
        {
            EnsureUsable();
            int overflowCount = 0;
            int level0Count = 0;
            int level1Count = 0;
            int level2Count = 0;
            int level3Count = 0;
            int consecutiveOverloadTicks = 0;
            for (int i = 0; i < clocks.Length; i++)
            {
                if (clocks[i] != null)
                {
                    overflowCount += clocks[i].OverflowCount;
                    level0Count += clocks[i].Level0Count;
                    level1Count += clocks[i].Level1Count;
                    level2Count += clocks[i].Level2Count;
                    level3Count += clocks[i].Level3Count;
                    consecutiveOverloadTicks = Math.Max(
                        consecutiveOverloadTicks,
                        clocks[i].ConsecutiveOverloadTicks);
                }
            }

            return new TimerSchedulerStats(
                activeCount,
                pausedCount,
                ownerCount,
                overflowCount,
                peakActiveCount,
                scheduledThisTick,
                cancelledThisTick,
                dueThisTick,
                executedThisTick,
                deferredThisTick,
                oldestOverdueMs,
                droppedCatchUpCount,
                fastForwardCount,
                level0Count,
                level1Count,
                level2Count,
                level3Count,
                lastTickMicroseconds,
                maxCallbackMicroseconds,
                consecutiveOverloadTicks,
                fastForwardScannedCount);
        }

        public void Clear()
        {
            EnsureUsable();
            if (ticking)
            {
                throw new TimerStateException(
                    $"Clear 不能在回调或 Tick 中执行。SchedulerId={schedulerId}。");
            }

            clearing = true;
            try
            {
                ClearTimers(true);
                DiscardExternalCancellations();
            }
            finally
            {
                try
                {
                    DrainDelayCompletions(true);
                }
                finally
                {
                    clearing = false;
                }
            }
        }

        public void Dispose()
        {
            EnsureOwnerThread();
            if (disposed)
            {
                throw new ObjectDisposedException(
                    nameof(TimerScheduler),
                    $"Dispose 重复调用。SchedulerId={schedulerId}。");
            }

            if (ticking)
            {
                throw new TimerStateException(
                    $"Dispose 不能在回调或 Tick 中执行。SchedulerId={schedulerId}。");
            }

            shuttingDown = true;
            try
            {
                ClearTimers(true);
                DiscardExternalCancellations();
                for (int i = 0; i < owners.Length; i++)
                {
                    if (owners[i].Active)
                    {
                        FreeOwner(i);
                    }
                }
            }
            finally
            {
                disposed = true;
                try
                {
                    DrainDelayCompletions(true);
                }
                finally
                {
                    shuttingDown = false;
                }
            }
        }

        internal bool IsOwnerThread =>
            Thread.CurrentThread.ManagedThreadId == ownerThreadId;

        internal void ValidateShutdown()
        {
            EnsureUsable();
            if (ticking)
            {
                throw new TimerStateException(
                    $"Shutdown/Dispose 不能在回调或 Tick 中执行。SchedulerId={schedulerId}。");
            }
        }

        private bool TryScheduleCore(
            in TimerOptions options,
            TimerCallback callback,
            long dueTimeMs,
            string api,
            out TimerHandle handle)
        {
            if (!TryAllocNode(out int slot))
            {
                handle = default;
                return false;
            }

            if (nextSequence == long.MaxValue)
            {
                freeSlots[freeSlotCount++] = slot;
                throw new TimerStateException(
                    $"Timer Sequence 已耗尽。SchedulerId={schedulerId}。");
            }

            ref TimerNode node = ref nodes[slot];
            if (node.Generation == 0)
            {
                node.Generation = 1;
            }

            node.DueTimeMs = dueTimeMs;
            node.IntervalMs = options.IntervalMs;
            node.PausedRemainingMs = 0;
            node.Sequence = ++nextSequence;
            node.Callback = callback;
            node.State = options.State;
            node.RemainingCount = options.RepeatCount;
            node.OwnerSlot = -1;
            node.NextInBucket = -1;
            node.PreviousInBucket = -1;
            node.NextInOwner = -1;
            node.PreviousInOwner = -1;
            node.BucketIndex = -1;
            node.HeapIndex = -1;
            node.Status = TimerNodeStatus.Scheduled;
            node.Container = TimerContainer.None;
            node.Clock = options.Clock;
            node.RepeatMode = options.RepeatMode;
            node.CatchUpPolicy = options.CatchUpPolicy;
            node.CatchUpOverflowPolicy = options.CatchUpOverflowPolicy;
            node.MaxCatchUpPerTick = options.MaxCatchUpPerTick;
            node.ExceptionPolicy = options.ExceptionPolicy;
            node.PauseRequested = false;
            node.CanPauseExecuting = false;

            if (options.Owner.IsValid)
            {
                int ownerSlot = RequireOwner(options.Owner, api);
                AttachOwner(slot, ownerSlot);
            }

            activeCount++;
            if (ticking)
            {
                scheduledThisTick++;
            }
            else
            {
                scheduledSinceLastTick++;
            }
            if (activeCount > peakActiveCount)
            {
                peakActiveCount = activeCount;
            }

            handle = new TimerHandle(schedulerId, slot, node.Generation);
            if (ticking)
            {
                AppendPending(slot);
            }
            else
            {
                PlaceNode(slot);
            }

            return true;
        }

        private void CaptureClockSnapshots()
        {
            for (int i = 0; i < clocks.Length; i++)
            {
                ClockState clock = clocks[i];
                if (clock != null)
                {
                    ReadClockNow(clock, (TimerClock)i);
                }
            }
        }

        private void ProcessClock(
            ClockState clock,
            TimerClock clockType,
            ref TickBudgetState budgetState)
        {
            long nowMs = clock.LastNowMs;
            long targetTick = nowMs / resolutionMs;
            long deltaTicks = targetTick - clock.CurrentTick;

            if (deltaTicks > fastForwardThresholdTicks)
            {
                FastForward(clock, clockType, nowMs, targetTick);
            }
            else
            {
                while (clock.CurrentTick < targetTick)
                {
                    clock.CurrentTick++;
                    CascadeForTick(clock);
                    DrainBucket(clock, (int)(clock.CurrentTick & (Level0Size - 1)), nowMs);
                }

                MigrateOverflow(clock, nowMs);
            }

            DispatchOverdue(
                clock,
                clockType,
                nowMs,
                ref budgetState);
        }

        private long ReadClockNow(ClockState clock, TimerClock clockType)
        {
            long nowMs = clock.Source.NowMs;
            if (nowMs < 0)
            {
                throw new TimerClockException(
                    $"时钟返回负数。SchedulerId={schedulerId}, Clock={clockType}, NowMs={nowMs}。");
            }

            if (!clock.Initialized)
            {
                clock.Initialized = true;
                clock.LastNowMs = nowMs;
                clock.CurrentTick = nowMs / resolutionMs;
                return nowMs;
            }

            if (nowMs < clock.LastNowMs)
            {
                throw new TimerClockException(
                    $"时钟发生回退。SchedulerId={schedulerId}, Clock={clockType}, " +
                    $"PreviousMs={clock.LastNowMs}, CurrentMs={nowMs}。");
            }

            clock.LastNowMs = nowMs;
            return nowMs;
        }

        private long GetClockNowForApi(TimerClock clockType)
        {
            ValidateClock(clockType);
            ClockState clock = clocks[(int)clockType];
            if (clock == null)
            {
                throw new TimerClockException(
                    $"Scheduler 未配置时钟。SchedulerId={schedulerId}, Clock={clockType}。");
            }

            return ticking && clock.Initialized
                ? clock.LastNowMs
                : ReadClockNow(clock, clockType);
        }

        private void CascadeForTick(ClockState clock)
        {
            if ((clock.CurrentTick & (Level0Size - 1)) != 0)
            {
                return;
            }

            int level1 = Level1Offset + (int)((clock.CurrentTick >> 8) & (LevelSize - 1));
            CascadeBucket(clock, level1);
            if (((clock.CurrentTick >> 8) & (LevelSize - 1)) != 0)
            {
                return;
            }

            int level2 = Level2Offset + (int)((clock.CurrentTick >> 14) & (LevelSize - 1));
            CascadeBucket(clock, level2);
            if (((clock.CurrentTick >> 14) & (LevelSize - 1)) != 0)
            {
                return;
            }

            int level3 = Level3Offset + (int)((clock.CurrentTick >> 20) & (LevelSize - 1));
            CascadeBucket(clock, level3);
        }

        private void CascadeBucket(ClockState clock, int bucketIndex)
        {
            int slot = DetachBucket(clock, bucketIndex);
            while (slot >= 0)
            {
                int next = nodes[slot].NextInBucket;
                DecrementLevelCount(clock, bucketIndex);
                ResetContainerLinks(slot);
                PlaceNode(slot);
                slot = next;
            }
        }

        private void DrainBucket(ClockState clock, int bucketIndex, long nowMs)
        {
            int slot = DetachBucket(clock, bucketIndex);
            while (slot >= 0)
            {
                int next = nodes[slot].NextInBucket;
                DecrementLevelCount(clock, bucketIndex);
                ResetContainerLinks(slot);
                if (nodes[slot].DueTimeMs <= nowMs)
                {
                    HeapPush(clock, slot, true);
                }
                else
                {
                    PlaceNode(slot);
                }

                slot = next;
            }
        }

        private int DetachBucket(ClockState clock, int bucketIndex)
        {
            int head = clock.BucketHeads[bucketIndex];
            clock.BucketHeads[bucketIndex] = -1;
            clock.BucketTails[bucketIndex] = -1;
            return head;
        }

        private void FastForward(
            ClockState clock,
            TimerClock clockType,
            long nowMs,
            long targetTick)
        {
            using ProfilerMarker.AutoScope _ = FastForwardProfilerMarker.Auto();
            ClearClockContainers(clock);
            clock.CurrentTick = targetTick;
            long scanned = 0;
            for (int i = 0; i < nodes.Length; i++)
            {
                scanned++;
                ref TimerNode node = ref nodes[i];
                if (node.Status != TimerNodeStatus.Scheduled ||
                    node.Clock != clockType ||
                    node.Container == TimerContainer.Pending)
                {
                    continue;
                }

                ResetContainerLinks(i);
                if (node.DueTimeMs <= nowMs)
                {
                    HeapPush(clock, i, true);
                }
                else
                {
                    PlaceNode(i);
                }
            }

            fastForwardCount = SaturatingAdd(fastForwardCount, 1);
            fastForwardScannedCount =
                SaturatingAdd(fastForwardScannedCount, scanned);
        }

        private void MigrateOverflow(ClockState clock, long nowMs)
        {
            while (clock.OverflowCount > 0)
            {
                int slot = HeapPeek(clock, false);
                long dueTick = ToTickCeiling(nodes[slot].DueTimeMs);
                if (dueTick - clock.CurrentTick >= WheelRange)
                {
                    break;
                }

                HeapPop(clock, false);
                if (nodes[slot].DueTimeMs <= nowMs)
                {
                    HeapPush(clock, slot, true);
                }
                else
                {
                    PlaceNode(slot);
                }
            }
        }

        private void DispatchOverdue(
            ClockState clock,
            TimerClock clockType,
            long nowMs,
            ref TickBudgetState budgetState)
        {
            using ProfilerMarker.AutoScope _ = DispatchProfilerMarker.Auto();
            TimerBudget budget = budgetState.Budget;
            if (clock.OverdueCount > 0 && !budgetState.Started)
            {
                budgetState.Started = true;
                budgetState.StartTimestamp =
                    budget.MaxExecutionMicroseconds > 0
                        ? Stopwatch.GetTimestamp()
                        : 0;
            }

            int deferredPendingCount = 0;
            long oldestPendingDueTimeMs = long.MaxValue;
            while (clock.OverdueCount > 0)
            {
                if (budgetState.ExecutedCallbacks >= budget.MaxCallbacksPerTick ||
                    ExceededTimeBudget(
                        budgetState.StartTimestamp,
                        budget.MaxExecutionMicroseconds))
                {
                    break;
                }

                int slot = HeapPop(clock, true);
                ref TimerNode node = ref nodes[slot];
                if (node.Status != TimerNodeStatus.Scheduled)
                {
                    throw new TimerStateException(
                        $"Overdue Heap 包含非 Scheduled 节点。SchedulerId={schedulerId}, " +
                        $"Slot={slot}, State={node.Status}。");
                }

                node.Status = TimerNodeStatus.Executing;
                dueThisTick++;
                int remainingBudget =
                    budget.MaxCallbacksPerTick - budgetState.ExecutedCallbacks;
                int executed = ExecuteNode(
                    slot,
                    nowMs,
                    Math.Min(remainingBudget, budget.MaxCatchUpCallbacksPerTimer),
                    budgetState.StartTimestamp,
                    budget.MaxExecutionMicroseconds);
                budgetState.ExecutedCallbacks += executed;
                executedThisTick += executed;

                ref TimerNode current = ref nodes[slot];
                if (current.Status == TimerNodeStatus.Scheduled &&
                    current.Container == TimerContainer.Pending &&
                    current.DueTimeMs <= nowMs)
                {
                    deferredPendingCount++;
                    oldestPendingDueTimeMs = Math.Min(
                        oldestPendingDueTimeMs,
                        current.DueTimeMs);
                }
            }

            int deferredCount = clock.OverdueCount + deferredPendingCount;
            if (deferredCount > 0)
            {
                deferredThisTick += deferredCount;
                clock.ConsecutiveOverloadTicks++;
                long oldestDueTimeMs = oldestPendingDueTimeMs;
                if (clock.OverdueCount > 0)
                {
                    int first = HeapPeek(clock, true);
                    oldestDueTimeMs = Math.Min(
                        oldestDueTimeMs,
                        nodes[first].DueTimeMs);
                }

                long overdueMs = Math.Max(0, nowMs - oldestDueTimeMs);
                if (overdueMs > oldestOverdueMs)
                {
                    oldestOverdueMs = overdueMs;
                }

                if (clock.ConsecutiveOverloadTicks >= 60 &&
                    !clock.OverloadWarningWritten)
                {
                    clock.OverloadWarningWritten = true;
                    UnityEngine.Debug.LogWarning(
                        $"[GameTimer] Timer 连续过载。SchedulerId={schedulerId}, " +
                        $"Clock={clockType}, ConsecutiveTicks={clock.ConsecutiveOverloadTicks}, " +
                        $"Deferred={deferredCount}, OldestOverdueMs={overdueMs}。");
                }
            }
            else
            {
                clock.ConsecutiveOverloadTicks = 0;
                clock.OverloadWarningWritten = false;
            }
        }

        private int ExecuteNode(
            int slot,
            long nowMs,
            int callbackBudget,
            long startTimestamp,
            long maxExecutionMicroseconds)
        {
            ref TimerNode node = ref nodes[slot];
            TimerHandle handle =
                new TimerHandle(schedulerId, slot, node.Generation);
            int executed = 0;

            if (node.RemainingCount == 1)
            {
                InvokeCallback(
                    slot,
                    handle,
                    node.DueTimeMs,
                    nowMs,
                    1,
                    false);
                executed = 1;
                CompleteAfterCallback(slot, nowMs, 1, true, false);
                return executed;
            }

            bool timeRangeExhausted = false;
            long duePeriods;
            if (node.RepeatMode == TimerRepeatMode.FixedDelay)
            {
                duePeriods = 1;
            }
            else
            {
                long quotient = Math.Max(0, nowMs - node.DueTimeMs) / node.IntervalMs;
                timeRangeExhausted =
                    quotient == long.MaxValue && node.RemainingCount < 0;
                duePeriods = quotient == long.MaxValue
                    ? long.MaxValue
                    : quotient + 1;
            }

            if (node.RemainingCount > 0)
            {
                duePeriods = Math.Min(duePeriods, node.RemainingCount);
                timeRangeExhausted = false;
            }

            switch (node.CatchUpPolicy)
            {
                case TimerCatchUpPolicy.Coalesce:
                {
                    int coalesced = duePeriods > int.MaxValue
                        ? int.MaxValue
                        : (int)duePeriods;
                    InvokeCallback(
                        slot,
                        handle,
                        node.DueTimeMs,
                        nowMs,
                        coalesced,
                        HasFuturePeriods(ref node, duePeriods));
                    executed = 1;
                    CompleteAfterCallback(
                        slot,
                        nowMs,
                        duePeriods,
                        false,
                        timeRangeExhausted);
                    break;
                }
                case TimerCatchUpPolicy.Skip:
                    InvokeCallback(
                        slot,
                        handle,
                        node.DueTimeMs,
                        nowMs,
                        1,
                        HasFuturePeriods(ref node, duePeriods));
                    executed = 1;
                    if (duePeriods > 1)
                    {
                        droppedCatchUpCount = SaturatingAdd(
                            droppedCatchUpCount,
                            duePeriods - 1);
                    }

                    CompleteAfterCallback(
                        slot,
                        nowMs,
                        duePeriods,
                        false,
                        timeRangeExhausted);
                    break;
                case TimerCatchUpPolicy.FireAll:
                {
                    bool overflowedPerTimer =
                        duePeriods > node.MaxCatchUpPerTick;
                    int directLimit = node.MaxCatchUpPerTick;
                    if (overflowedPerTimer &&
                        node.CatchUpOverflowPolicy ==
                        TimerCatchUpOverflowPolicy.Coalesce)
                    {
                        directLimit--;
                    }

                    long allowed = Math.Min(
                        duePeriods,
                        Math.Min(directLimit, callbackBudget));
                    long consumed = 0;
                    while (consumed < allowed)
                    {
                        long scheduledTime = CheckedAddTime(
                            node.DueTimeMs,
                            CheckedMultiply(node.IntervalMs, consumed, slot),
                            slot);
                        InvokeCallback(
                            slot,
                            handle,
                            scheduledTime,
                            nowMs,
                            1,
                            HasFuturePeriods(ref node, consumed + 1));
                        executed++;
                        consumed++;
                        if (nodes[slot].Status == TimerNodeStatus.Cancelled ||
                            nodes[slot].PauseRequested ||
                            ExceededTimeBudget(startTimestamp, maxExecutionMicroseconds))
                        {
                            break;
                        }
                    }

                    bool callbackStopped =
                        nodes[slot].Status == TimerNodeStatus.Cancelled ||
                        nodes[slot].PauseRequested;
                    bool timeBudgetStopped =
                        ExceededTimeBudget(startTimestamp, maxExecutionMicroseconds);
                    long logicalConsumed = consumed;

                    if (!callbackStopped &&
                        !timeBudgetStopped &&
                        overflowedPerTimer &&
                        consumed == directLimit)
                    {
                        long overflowPeriods = duePeriods - consumed;
                        if (node.CatchUpOverflowPolicy ==
                            TimerCatchUpOverflowPolicy.Skip)
                        {
                            droppedCatchUpCount = SaturatingAdd(
                                droppedCatchUpCount,
                                overflowPeriods);
                            logicalConsumed = duePeriods;
                        }
                        else if (executed < callbackBudget &&
                                 executed < node.MaxCatchUpPerTick)
                        {
                            int coalesced = overflowPeriods > int.MaxValue
                                ? int.MaxValue
                                : (int)overflowPeriods;
                            long scheduledTime = CheckedAddTime(
                                node.DueTimeMs,
                                CheckedMultiply(node.IntervalMs, consumed, slot),
                                slot);
                            InvokeCallback(
                                slot,
                                handle,
                                scheduledTime,
                                nowMs,
                                coalesced,
                                HasFuturePeriods(ref node, duePeriods));
                            executed++;
                            logicalConsumed = duePeriods;
                        }
                    }

                    CompleteAfterCallback(
                        slot,
                        nowMs,
                        logicalConsumed,
                        false,
                        timeRangeExhausted && logicalConsumed == duePeriods);
                    break;
                }
                default:
                    throw new TimerStateException(
                        $"Timer 包含未知 CatchUpPolicy。SchedulerId={schedulerId}, " +
                        $"Slot={slot}, Value={(byte)node.CatchUpPolicy}。");
            }

            return executed;
        }

        private void InvokeCallback(
            int slot,
            TimerHandle handle,
            long scheduledTimeMs,
            long actualTimeMs,
            int coalescedFireCount,
            bool canPauseExecuting)
        {
            using ProfilerMarker.AutoScope _ = CallbackProfilerMarker.Auto();
            ref TimerNode node = ref nodes[slot];
            bool measureCallback = node.Clock != TimerClock.Simulation;
            long callbackStartTimestamp =
                measureCallback ? Stopwatch.GetTimestamp() : 0;
            var context = new TimerContext(
                handle,
                node.State,
                scheduledTimeMs,
                actualTimeMs,
                coalescedFireCount);
            node.CanPauseExecuting = canPauseExecuting;

            try
            {
                node.Callback(in context);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogException(exception);
                if (node.ExceptionPolicy == TimerExceptionPolicy.CancelTimer)
                {
                    node.Status = TimerNodeStatus.Cancelled;
                    DetachOwner(slot);
                }
            }
            finally
            {
                node.CanPauseExecuting = false;
                if (measureCallback)
                {
                    long elapsed =
                        ToMicroseconds(Stopwatch.GetTimestamp() - callbackStartTimestamp);
                    if (elapsed > maxCallbackMicroseconds)
                    {
                        maxCallbackMicroseconds = elapsed;
                    }
                }
            }
        }

        private static bool HasFuturePeriods(
            ref TimerNode node,
            long consumedPeriods)
        {
            return node.RemainingCount < 0 ||
                   consumedPeriods < node.RemainingCount;
        }

        private void CompleteAfterCallback(
            int slot,
            long nowMs,
            long consumedPeriods,
            bool oneShot,
            bool timeRangeExhausted)
        {
            ref TimerNode node = ref nodes[slot];
            if (node.Status == TimerNodeStatus.Cancelled)
            {
                FreeNode(slot, false);
                return;
            }

            if (oneShot)
            {
                FreeNode(slot, false);
                return;
            }

            if (node.RemainingCount > 0)
            {
                long consumed = Math.Min(consumedPeriods, node.RemainingCount);
                node.RemainingCount -= (int)consumed;
                if (node.RemainingCount == 0)
                {
                    FreeNode(slot, false);
                    return;
                }
            }

            if (timeRangeExhausted)
            {
                FailTimerTimeRange(
                    slot,
                    "FixedRate 无法表示 long.MaxValue 之后的下一次 Deadline。");
                return;
            }

            try
            {
                if (node.RepeatMode == TimerRepeatMode.FixedDelay)
                {
                    node.DueTimeMs = CheckedAddTime(nowMs, node.IntervalMs, slot);
                }
                else
                {
                    node.DueTimeMs = CheckedAddTime(
                        node.DueTimeMs,
                        CheckedMultiply(node.IntervalMs, consumedPeriods, slot),
                        slot);
                }
            }
            catch (TimerClockException exception)
            {
                UnityEngine.Debug.LogException(exception);
                node.PauseRequested = false;
                FreeNode(slot, false);
                return;
            }

            if (node.PauseRequested)
            {
                node.PauseRequested = false;
                node.PausedRemainingMs = Math.Max(0, node.DueTimeMs - nowMs);
                node.Status = TimerNodeStatus.Paused;
                pausedCount++;
                return;
            }

            node.Status = TimerNodeStatus.Scheduled;
            if (node.DueTimeMs <= nowMs)
            {
                AppendPending(slot);
            }
            else
            {
                PlaceNode(slot);
            }
        }

        private bool TryPauseSlot(int slot)
        {
            ref TimerNode node = ref nodes[slot];
            if (node.Status == TimerNodeStatus.Executing)
            {
                if (!node.CanPauseExecuting || node.PauseRequested)
                {
                    return false;
                }

                node.PauseRequested = true;
                return true;
            }

            if (node.Status != TimerNodeStatus.Scheduled)
            {
                return false;
            }

            long nowMs = GetClockNowForApi(node.Clock);
            long remainingMs = Math.Max(0, node.DueTimeMs - nowMs);
            RemoveFromContainer(slot);
            node.PausedRemainingMs = remainingMs;
            node.Status = TimerNodeStatus.Paused;
            pausedCount++;
            return true;
        }

        private bool TryResumeSlot(int slot)
        {
            ref TimerNode node = ref nodes[slot];
            if (node.Status != TimerNodeStatus.Paused)
            {
                return false;
            }

            long nowMs = GetClockNowForApi(node.Clock);
            node.DueTimeMs = CheckedAddTime(nowMs, node.PausedRemainingMs, slot);
            node.PausedRemainingMs = 0;
            node.Status = TimerNodeStatus.Scheduled;
            pausedCount--;
            if (ticking)
            {
                AppendPending(slot);
            }
            else
            {
                PlaceNode(slot);
            }

            return true;
        }

        private long GetRemainingMs(int slot)
        {
            ref TimerNode node = ref nodes[slot];
            if (node.Status == TimerNodeStatus.Paused)
            {
                return node.PausedRemainingMs;
            }

            long nowMs = GetClockNowForApi(node.Clock);
            return Math.Max(0, node.DueTimeMs - nowMs);
        }

        private void CancelSlot(int slot, bool countCancellation)
        {
            ref TimerNode node = ref nodes[slot];
            if (node.Status == TimerNodeStatus.Executing)
            {
                node.Status = TimerNodeStatus.Cancelled;
                node.PauseRequested = false;
                DetachOwner(slot);
            }
            else
            {
                RemoveFromContainer(slot);
                FreeNode(slot, false);
            }

            if (countCancellation)
            {
                if (ticking)
                {
                    cancelledThisTick++;
                }
                else
                {
                    cancelledSinceLastTick++;
                }
            }
        }

        private void PlaceNode(int slot)
        {
            ref TimerNode node = ref nodes[slot];
            ClockState clock = clocks[(int)node.Clock];
            long nowMs = clock.LastNowMs;
            if (!clock.Initialized)
            {
                nowMs = ReadClockNow(clock, node.Clock);
            }

            if (node.DueTimeMs <= nowMs)
            {
                HeapPush(clock, slot, true);
                return;
            }

            long dueTick = ToTickCeiling(node.DueTimeMs);
            long delta = dueTick - clock.CurrentTick;
            if (delta < 0)
            {
                HeapPush(clock, slot, true);
                return;
            }

            int bucket;
            if (delta < Level0Range)
            {
                bucket = (int)(dueTick & (Level0Size - 1));
            }
            else if (delta < Level1Range)
            {
                bucket = Level1Offset + (int)((dueTick >> 8) & (LevelSize - 1));
            }
            else if (delta < Level2Range)
            {
                bucket = Level2Offset + (int)((dueTick >> 14) & (LevelSize - 1));
            }
            else if (delta < WheelRange)
            {
                bucket = Level3Offset + (int)((dueTick >> 20) & (LevelSize - 1));
            }
            else
            {
                HeapPush(clock, slot, false);
                return;
            }

            AppendBucket(clock, bucket, slot);
        }

        private long ToTickCeiling(long timeMs)
        {
            long tick = timeMs / resolutionMs;
            return timeMs % resolutionMs == 0 ? tick : tick + 1;
        }

        private void AppendBucket(ClockState clock, int bucket, int slot)
        {
            ref TimerNode node = ref nodes[slot];
            int tail = clock.BucketTails[bucket];
            node.Container = TimerContainer.Wheel;
            node.BucketIndex = bucket;
            node.PreviousInBucket = tail;
            node.NextInBucket = -1;
            if (tail >= 0)
            {
                nodes[tail].NextInBucket = slot;
            }
            else
            {
                clock.BucketHeads[bucket] = slot;
            }

            clock.BucketTails[bucket] = slot;
            IncrementLevelCount(clock, bucket);
        }

        private void RemoveFromContainer(int slot)
        {
            ref TimerNode node = ref nodes[slot];
            ClockState clock = clocks[(int)node.Clock];
            switch (node.Container)
            {
                case TimerContainer.None:
                    return;
                case TimerContainer.Wheel:
                    RemoveFromBucket(clock, slot);
                    break;
                case TimerContainer.Overflow:
                    HeapRemove(clock, slot, false);
                    break;
                case TimerContainer.Overdue:
                    HeapRemove(clock, slot, true);
                    break;
                case TimerContainer.Pending:
                    RemovePending(slot);
                    break;
                default:
                    throw new TimerStateException(
                        $"Timer Container 非法。SchedulerId={schedulerId}, Slot={slot}, " +
                        $"Container={(byte)node.Container}。");
            }
        }

        private void RemoveFromBucket(ClockState clock, int slot)
        {
            ref TimerNode node = ref nodes[slot];
            int previous = node.PreviousInBucket;
            int next = node.NextInBucket;
            if (previous >= 0)
            {
                nodes[previous].NextInBucket = next;
            }
            else
            {
                clock.BucketHeads[node.BucketIndex] = next;
            }

            if (next >= 0)
            {
                nodes[next].PreviousInBucket = previous;
            }
            else
            {
                clock.BucketTails[node.BucketIndex] = previous;
            }

            DecrementLevelCount(clock, node.BucketIndex);
            ResetContainerLinks(slot);
        }

        private void AppendPending(int slot)
        {
            ref TimerNode node = ref nodes[slot];
            node.Container = TimerContainer.Pending;
            node.PreviousInBucket = pendingTail;
            node.NextInBucket = -1;
            if (pendingTail >= 0)
            {
                nodes[pendingTail].NextInBucket = slot;
            }
            else
            {
                pendingHead = slot;
            }

            pendingTail = slot;
        }

        private void RemovePending(int slot)
        {
            ref TimerNode node = ref nodes[slot];
            int previous = node.PreviousInBucket;
            int next = node.NextInBucket;
            if (previous >= 0)
            {
                nodes[previous].NextInBucket = next;
            }
            else
            {
                pendingHead = next;
            }

            if (next >= 0)
            {
                nodes[next].PreviousInBucket = previous;
            }
            else
            {
                pendingTail = previous;
            }

            ResetContainerLinks(slot);
        }

        private void CommitPending()
        {
            int slot = pendingHead;
            pendingHead = -1;
            pendingTail = -1;
            while (slot >= 0)
            {
                int next = nodes[slot].NextInBucket;
                ResetContainerLinks(slot);
                if (nodes[slot].Status == TimerNodeStatus.Scheduled)
                {
                    PlaceNode(slot);
                }

                slot = next;
            }
        }

        private void ResetContainerLinks(int slot)
        {
            ref TimerNode node = ref nodes[slot];
            node.Container = TimerContainer.None;
            node.BucketIndex = -1;
            node.HeapIndex = -1;
            node.PreviousInBucket = -1;
            node.NextInBucket = -1;
        }

        private void HeapPush(ClockState clock, int slot, bool overdue)
        {
            int[] heap = overdue ? clock.OverdueHeap : clock.OverflowHeap;
            int count = overdue ? clock.OverdueCount : clock.OverflowCount;
            heap[count] = slot;
            nodes[slot].HeapIndex = count;
            nodes[slot].Container =
                overdue ? TimerContainer.Overdue : TimerContainer.Overflow;
            if (overdue)
            {
                clock.OverdueCount++;
            }
            else
            {
                clock.OverflowCount++;
            }

            SiftUp(heap, count);
        }

        private int HeapPeek(ClockState clock, bool overdue)
        {
            return overdue ? clock.OverdueHeap[0] : clock.OverflowHeap[0];
        }

        private int HeapPop(ClockState clock, bool overdue)
        {
            int[] heap = overdue ? clock.OverdueHeap : clock.OverflowHeap;
            int count = overdue ? clock.OverdueCount : clock.OverflowCount;
            int slot = heap[0];
            int newCount = count - 1;
            if (newCount > 0)
            {
                int moved = heap[newCount];
                heap[0] = moved;
                nodes[moved].HeapIndex = 0;
                SiftDown(heap, 0, newCount);
            }

            if (overdue)
            {
                clock.OverdueCount = newCount;
            }
            else
            {
                clock.OverflowCount = newCount;
            }

            heap[newCount] = -1;
            ResetContainerLinks(slot);
            return slot;
        }

        private void HeapRemove(ClockState clock, int slot, bool overdue)
        {
            int[] heap = overdue ? clock.OverdueHeap : clock.OverflowHeap;
            int count = overdue ? clock.OverdueCount : clock.OverflowCount;
            int index = nodes[slot].HeapIndex;
            int newCount = count - 1;
            if ((uint)index >= (uint)count)
            {
                throw new TimerStateException(
                    $"HeapIndex 非法。SchedulerId={schedulerId}, Slot={slot}, " +
                    $"Index={index}, Count={count}。");
            }

            if (index != newCount)
            {
                int moved = heap[newCount];
                heap[index] = moved;
                nodes[moved].HeapIndex = index;
                if (index > 0 && ComesBefore(moved, heap[(index - 1) >> 1]))
                {
                    SiftUp(heap, index);
                }
                else
                {
                    SiftDown(heap, index, newCount);
                }
            }

            heap[newCount] = -1;
            if (overdue)
            {
                clock.OverdueCount = newCount;
            }
            else
            {
                clock.OverflowCount = newCount;
            }

            ResetContainerLinks(slot);
        }

        private void SiftUp(int[] heap, int index)
        {
            int slot = heap[index];
            while (index > 0)
            {
                int parent = (index - 1) >> 1;
                int parentSlot = heap[parent];
                if (!ComesBefore(slot, parentSlot))
                {
                    break;
                }

                heap[index] = parentSlot;
                nodes[parentSlot].HeapIndex = index;
                index = parent;
            }

            heap[index] = slot;
            nodes[slot].HeapIndex = index;
        }

        private void SiftDown(int[] heap, int index, int count)
        {
            int slot = heap[index];
            int half = count >> 1;
            while (index < half)
            {
                int left = (index << 1) + 1;
                int right = left + 1;
                int best = left;
                if (right < count && ComesBefore(heap[right], heap[left]))
                {
                    best = right;
                }

                if (!ComesBefore(heap[best], slot))
                {
                    break;
                }

                heap[index] = heap[best];
                nodes[heap[index]].HeapIndex = index;
                index = best;
            }

            heap[index] = slot;
            nodes[slot].HeapIndex = index;
        }

        private bool ComesBefore(int leftSlot, int rightSlot)
        {
            ref TimerNode left = ref nodes[leftSlot];
            ref TimerNode right = ref nodes[rightSlot];
            return left.DueTimeMs < right.DueTimeMs ||
                   (left.DueTimeMs == right.DueTimeMs &&
                    left.Sequence < right.Sequence);
        }

        private void AttachOwner(int slot, int ownerSlot)
        {
            ref OwnerSlot owner = ref owners[ownerSlot];
            ref TimerNode node = ref nodes[slot];
            node.OwnerSlot = ownerSlot;
            node.PreviousInOwner = -1;
            node.NextInOwner = owner.Head;
            if (owner.Head >= 0)
            {
                nodes[owner.Head].PreviousInOwner = slot;
            }

            owner.Head = slot;
            owner.TimerCount++;
        }

        private void DetachOwner(int slot)
        {
            ref TimerNode node = ref nodes[slot];
            if (node.OwnerSlot < 0)
            {
                return;
            }

            ref OwnerSlot owner = ref owners[node.OwnerSlot];
            if (node.PreviousInOwner >= 0)
            {
                nodes[node.PreviousInOwner].NextInOwner = node.NextInOwner;
            }
            else
            {
                owner.Head = node.NextInOwner;
            }

            if (node.NextInOwner >= 0)
            {
                nodes[node.NextInOwner].PreviousInOwner = node.PreviousInOwner;
            }

            owner.TimerCount--;
            node.OwnerSlot = -1;
            node.PreviousInOwner = -1;
            node.NextInOwner = -1;
        }

        private void FreeNode(int slot, bool notifyShutdown)
        {
            ref TimerNode node = ref nodes[slot];
            if (notifyShutdown && node.State is ITimerShutdownSink sink)
            {
                sink.OnSchedulerShutdown();
            }

            bool wasPaused = node.Status == TimerNodeStatus.Paused;
            DetachOwner(slot);
            node.Callback = null;
            node.State = null;
            node.Status = TimerNodeStatus.Free;
            node.Container = TimerContainer.None;
            node.PauseRequested = false;
            node.CanPauseExecuting = false;
            node.Generation = NextGeneration(node.Generation);
            node.NextInBucket = -1;
            node.PreviousInBucket = -1;
            node.NextInOwner = -1;
            node.PreviousInOwner = -1;
            node.BucketIndex = -1;
            node.HeapIndex = -1;
            freeSlots[freeSlotCount++] = slot;
            activeCount--;
            if (wasPaused)
            {
                pausedCount--;
            }
        }

        private void ClearTimers(bool notifyShutdown)
        {
            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i].Status == TimerNodeStatus.Free)
                {
                    continue;
                }

                RemoveFromContainer(i);
                FreeNode(i, notifyShutdown);
            }

            pendingHead = -1;
            pendingTail = -1;
            for (int i = 0; i < clocks.Length; i++)
            {
                if (clocks[i] != null)
                {
                    ClearClockContainers(clocks[i]);
                }
            }
        }

        private void ClearClockContainers(ClockState clock)
        {
            for (int i = 0; i < BucketCount; i++)
            {
                clock.BucketHeads[i] = -1;
                clock.BucketTails[i] = -1;
            }

            for (int i = 0; i < clock.OverflowCount; i++)
            {
                clock.OverflowHeap[i] = -1;
            }

            for (int i = 0; i < clock.OverdueCount; i++)
            {
                clock.OverdueHeap[i] = -1;
            }

            clock.OverflowCount = 0;
            clock.OverdueCount = 0;
            clock.Level0Count = 0;
            clock.Level1Count = 0;
            clock.Level2Count = 0;
            clock.Level3Count = 0;
        }

        private bool TryAllocNode(out int slot)
        {
            if (freeSlotCount == 0)
            {
                if (!allowRuntimeGrowth || nodes.Length >= maxCapacity)
                {
                    slot = -1;
                    return false;
                }

                int target = Math.Min(maxCapacity, Math.Max(nodes.Length + 1, nodes.Length * 2));
                WarnRuntimeGrowth("Timer", nodes.Length, target);
                GrowNodes(target);
            }

            slot = freeSlots[--freeSlotCount];
            return true;
        }

        private void GrowNodes(int target)
        {
            int oldLength = nodes.Length;
            Array.Resize(ref nodes, target);
            Array.Resize(ref freeSlots, target);
            for (int i = target - 1; i >= oldLength; i--)
            {
                freeSlots[freeSlotCount++] = i;
            }

            for (int i = 0; i < clocks.Length; i++)
            {
                clocks[i]?.GrowHeaps(target);
            }
        }

        private bool TryAllocOwner(out int slot)
        {
            if (freeOwnerSlotCount == 0)
            {
                if (!allowRuntimeGrowth || owners.Length >= maxOwnerCapacity)
                {
                    slot = -1;
                    return false;
                }

                int target = Math.Min(
                    maxOwnerCapacity,
                    Math.Max(owners.Length + 1, owners.Length * 2));
                WarnRuntimeGrowth("Owner", owners.Length, target);
                GrowOwners(target);
            }

            slot = freeOwnerSlots[--freeOwnerSlotCount];
            return true;
        }

        private void GrowOwners(int target)
        {
            int oldLength = owners.Length;
            Array.Resize(ref owners, target);
            Array.Resize(ref freeOwnerSlots, target);
            for (int i = target - 1; i >= oldLength; i--)
            {
                freeOwnerSlots[freeOwnerSlotCount++] = i;
            }
        }

        private void WarnRuntimeGrowth(string kind, int previous, int target)
        {
            if (runtimeGrowthWarningWritten)
            {
                return;
            }

            runtimeGrowthWarningWritten = true;
            UnityEngine.Debug.LogWarning(
                $"[GameTimer] Scheduler 发生运行时扩容。SchedulerId={schedulerId}, " +
                $"Kind={kind}, Previous={previous}, Target={target}。" +
                "请在初始化阶段 Reserve 足够容量。");
        }

        private void FreeOwner(int slot)
        {
            ref OwnerSlot owner = ref owners[slot];
            owner.Active = false;
            owner.Head = -1;
            owner.TimerCount = 0;
            owner.Generation = NextGeneration(owner.Generation);
            freeOwnerSlots[freeOwnerSlotCount++] = slot;
            ownerCount--;
        }

        private int RequireActiveHandle(TimerHandle handle, string api)
        {
            ValidateHandleIdentity(handle, api);
            if ((uint)handle.Slot >= (uint)nodes.Length)
            {
                throw OwnershipError(api, handle, "Slot 超出范围");
            }

            ref TimerNode node = ref nodes[handle.Slot];
            if (node.Generation != handle.Generation ||
                node.Status == TimerNodeStatus.Free ||
                node.Status == TimerNodeStatus.Cancelled)
            {
                throw OwnershipError(
                    api,
                    handle,
                    $"Handle 已过期或不活跃，ActualGeneration={node.Generation}, State={node.Status}");
            }

            return handle.Slot;
        }

        private bool TryGetActiveSlot(TimerHandle handle, string api, out int slot)
        {
            ValidateHandleIdentity(handle, api);
            if ((uint)handle.Slot >= (uint)nodes.Length)
            {
                slot = -1;
                return false;
            }

            ref TimerNode node = ref nodes[handle.Slot];
            if (node.Generation != handle.Generation ||
                node.Status == TimerNodeStatus.Free ||
                node.Status == TimerNodeStatus.Cancelled)
            {
                slot = -1;
                return false;
            }

            slot = handle.Slot;
            return true;
        }

        private void ValidateHandleIdentity(TimerHandle handle, string api)
        {
            if (!handle.IsValid)
            {
                throw OwnershipError(api, handle, "默认或无效 Handle");
            }

            if (handle.SchedulerId != schedulerId)
            {
                throw OwnershipError(
                    api,
                    handle,
                    $"Handle 属于其他 Scheduler，Expected={schedulerId}");
            }
        }

        private int RequireOwner(TimerOwner owner, string api)
        {
            ValidateOwnerIdentity(owner, api);
            if ((uint)owner.Slot >= (uint)owners.Length)
            {
                throw OwnerError(api, owner, "Owner Slot 超出范围");
            }

            ref OwnerSlot slot = ref owners[owner.Slot];
            if (!slot.Active || slot.Generation != owner.Generation)
            {
                throw OwnerError(
                    api,
                    owner,
                    $"Owner 已释放，ActualGeneration={slot.Generation}, Active={slot.Active}");
            }

            return owner.Slot;
        }

        private void ValidateOwnerIdentity(TimerOwner owner, string api)
        {
            if (!owner.IsValid)
            {
                throw OwnerError(api, owner, "默认或无效 Owner");
            }

            if (owner.SchedulerId != schedulerId)
            {
                throw OwnerError(
                    api,
                    owner,
                    $"Owner 属于其他 Scheduler，Expected={schedulerId}");
            }
        }

        private void ValidateTimerOptions(
            in TimerOptions options,
            TimerCallback callback,
            string api)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            ValidateClock(options.Clock);
            if (options.DelayMs < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.DelayMs,
                    "DelayMs 不能小于 0。");
            }

            if (options.RepeatCount == 0 || options.RepeatCount < -1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.RepeatCount,
                    "RepeatCount 只能为 -1 或大于 0。");
            }

            if (options.RepeatCount == 1 && options.IntervalMs != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.IntervalMs,
                    "一次性 Timer 的 IntervalMs 必须为 0。");
            }

            if (options.RepeatCount != 1 && options.IntervalMs <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.IntervalMs,
                    "重复 Timer 的 IntervalMs 必须大于 0。");
            }

            if ((byte)options.RepeatMode > (byte)TimerRepeatMode.FixedDelay)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "RepeatMode 非法。");
            }

            if ((byte)options.CatchUpPolicy > (byte)TimerCatchUpPolicy.FireAll)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "CatchUpPolicy 非法。");
            }

            if ((byte)options.CatchUpOverflowPolicy >
                (byte)TimerCatchUpOverflowPolicy.Coalesce)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "CatchUpOverflowPolicy 非法。");
            }

            if ((byte)options.ExceptionPolicy > (byte)TimerExceptionPolicy.Continue)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "ExceptionPolicy 非法。");
            }

            if (options.MaxCatchUpPerTick == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "MaxCatchUpPerTick 必须大于 0。");
            }

            if (clocks[(int)options.Clock] == null)
            {
                throw new TimerClockException(
                    $"Scheduler 未配置时钟。SchedulerId={schedulerId}, Clock={options.Clock}。");
            }

            if (options.Owner.IsValid)
            {
                RequireOwner(options.Owner, api);
            }
        }

        private static void ValidateClock(TimerClock clock)
        {
            if ((byte)clock > (byte)TimerClock.Simulation)
            {
                throw new ArgumentOutOfRangeException(nameof(clock), clock, "TimerClock 非法。");
            }
        }

        private static void ValidateSchedulerOptions(TimerSchedulerOptions options)
        {
            if (options.InitialCapacity <= 0 ||
                options.MaxCapacity < options.InitialCapacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Timer 容量必须满足 0 < InitialCapacity <= MaxCapacity。");
            }

            if (options.InitialOwnerCapacity <= 0 ||
                options.MaxOwnerCapacity < options.InitialOwnerCapacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Owner 容量必须满足 0 < InitialOwnerCapacity <= MaxOwnerCapacity。");
            }

            if (options.TickResolutionMs <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "TickResolutionMs 必须大于 0。");
            }

            if (options.FastForwardThresholdTicks <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "FastForwardThresholdTicks 必须大于 0。");
            }

            ValidateBudget(options.RuntimeBudget, false);
            ValidateBudget(options.SimulationBudget, true);
        }

        private static void ValidateBudget(TimerBudget budget, bool simulation)
        {
            if (budget.MaxCallbacksPerTick <= 0 ||
                budget.MaxCatchUpCallbacksPerTimer <= 0 ||
                budget.MaxExecutionMicroseconds < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(budget),
                    "TimerBudget 数量必须大于 0，时间预算不能小于 0。");
            }

            if (simulation && budget.MaxExecutionMicroseconds != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(budget),
                    "SimulationBudget 禁止使用真实执行时间预算。");
            }
        }

        private void EnsureUsable()
        {
            EnsureOwnerThread();
            if (disposed)
            {
                throw new ObjectDisposedException(
                    nameof(TimerScheduler),
                    $"Scheduler 已 Dispose。SchedulerId={schedulerId}。");
            }

            if (shuttingDown || clearing)
            {
                throw new TimerStateException(
                    $"Scheduler 正在清理，拒绝重入调用。SchedulerId={schedulerId}, " +
                    $"ShuttingDown={shuttingDown}, Clearing={clearing}。");
            }
        }

        private void EnsureOwnerThread()
        {
            int current = Thread.CurrentThread.ManagedThreadId;
            if (current != ownerThreadId)
            {
                throw new TimerThreadException(
                    $"TimerScheduler 只能从创建线程调用。SchedulerId={schedulerId}, " +
                    $"ExpectedThread={ownerThreadId}, CurrentThread={current}。");
            }
        }

        private void ResetTickStats()
        {
            scheduledThisTick = scheduledSinceLastTick;
            cancelledThisTick = cancelledSinceLastTick;
            scheduledSinceLastTick = 0;
            cancelledSinceLastTick = 0;
            dueThisTick = 0;
            executedThisTick = 0;
            deferredThisTick = 0;
            oldestOverdueMs = 0;
        }

        private bool HasRuntimeClock()
        {
            return clocks[(int)TimerClock.Scaled] != null ||
                   clocks[(int)TimerClock.Unscaled] != null ||
                   clocks[(int)TimerClock.Realtime] != null;
        }

        private static void IncrementLevelCount(ClockState clock, int bucket)
        {
            if (bucket < Level1Offset)
            {
                clock.Level0Count++;
            }
            else if (bucket < Level2Offset)
            {
                clock.Level1Count++;
            }
            else if (bucket < Level3Offset)
            {
                clock.Level2Count++;
            }
            else
            {
                clock.Level3Count++;
            }
        }

        private static void DecrementLevelCount(ClockState clock, int bucket)
        {
            if (bucket < Level1Offset)
            {
                clock.Level0Count--;
            }
            else if (bucket < Level2Offset)
            {
                clock.Level1Count--;
            }
            else if (bucket < Level3Offset)
            {
                clock.Level2Count--;
            }
            else
            {
                clock.Level3Count--;
            }
        }

        private static uint NextGeneration(uint value)
        {
            value++;
            return value == 0 ? 1u : value;
        }

        private static long SaturatingAdd(long left, long right)
        {
            if (right <= 0)
            {
                return left;
            }

            return left > long.MaxValue - right
                ? long.MaxValue
                : left + right;
        }

        private static bool ExceededTimeBudget(long startTimestamp, long maxMicroseconds)
        {
            if (maxMicroseconds <= 0)
            {
                return false;
            }

            long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
            return ToMicroseconds(elapsed) >= maxMicroseconds;
        }

        private static long ToMicroseconds(long timestampDelta)
        {
            return timestampDelta <= 0
                ? 0
                : timestampDelta * 1000000L / Stopwatch.Frequency;
        }

        private long CheckedAddTime(long left, long right, int slot)
        {
            try
            {
                return checked(left + right);
            }
            catch (OverflowException)
            {
                throw new TimerClockException(
                    $"Timer Deadline 溢出。SchedulerId={schedulerId}, Slot={slot}, " +
                    $"Left={left}, Right={right}。");
            }
        }

        private long CheckedMultiply(long left, long right, int slot)
        {
            try
            {
                return checked(left * right);
            }
            catch (OverflowException)
            {
                throw new TimerClockException(
                    $"Timer Interval 计算溢出。SchedulerId={schedulerId}, Slot={slot}, " +
                    $"Left={left}, Right={right}。");
            }
        }

        private void FailTimerTimeRange(int slot, string reason)
        {
            ref TimerNode node = ref nodes[slot];
            var exception = new TimerClockException(
                $"Timer 时间范围耗尽，任务已取消。SchedulerId={schedulerId}, " +
                $"Slot={slot}, Generation={node.Generation}, DueTimeMs={node.DueTimeMs}, " +
                $"IntervalMs={node.IntervalMs}, Reason={reason}");
            UnityEngine.Debug.LogException(exception);
            node.PauseRequested = false;
            FreeNode(slot, false);
        }

        private TimerOwnershipException OwnershipError(
            string api,
            TimerHandle handle,
            string reason)
        {
            return new TimerOwnershipException(
                $"{api} 拒绝 TimerHandle。SchedulerId={schedulerId}, " +
                $"HandleScheduler={handle.SchedulerId}, Slot={handle.Slot}, " +
                $"Generation={handle.Generation}, Reason={reason}。");
        }

        private TimerOwnershipException OwnerError(
            string api,
            TimerOwner owner,
            string reason)
        {
            return new TimerOwnershipException(
                $"{api} 拒绝 TimerOwner。SchedulerId={schedulerId}, " +
                $"OwnerScheduler={owner.SchedulerId}, Slot={owner.Slot}, " +
                $"Generation={owner.Generation}, Reason={reason}。");
        }

        private TimerStateException StateError(
            string api,
            TimerHandle handle,
            string expected,
            TimerNodeStatus actual)
        {
            return new TimerStateException(
                $"{api} 状态错误。SchedulerId={schedulerId}, Slot={handle.Slot}, " +
                $"Generation={handle.Generation}, Expected={expected}, Actual={actual}。");
        }

        private sealed class ClockState
        {
            public readonly ITimeSource Source;
            public readonly int[] BucketHeads = new int[BucketCount];
            public readonly int[] BucketTails = new int[BucketCount];
            public int[] OverflowHeap;
            public int[] OverdueHeap;
            public int OverflowCount;
            public int OverdueCount;
            public int Level0Count;
            public int Level1Count;
            public int Level2Count;
            public int Level3Count;
            public int ConsecutiveOverloadTicks;
            public bool OverloadWarningWritten;
            public bool Initialized;
            public long LastNowMs;
            public long CurrentTick;

            public ClockState(ITimeSource source, int capacity)
            {
                Source = source;
                OverflowHeap = new int[capacity];
                OverdueHeap = new int[capacity];
                for (int i = 0; i < BucketCount; i++)
                {
                    BucketHeads[i] = -1;
                    BucketTails[i] = -1;
                }
            }

            public void GrowHeaps(int target)
            {
                Array.Resize(ref OverflowHeap, target);
                Array.Resize(ref OverdueHeap, target);
            }
        }

        private struct TickBudgetState
        {
            public readonly TimerBudget Budget;
            public int ExecutedCallbacks;
            public long StartTimestamp;
            public bool Started;

            public TickBudgetState(TimerBudget budget)
            {
                Budget = budget;
                ExecutedCallbacks = 0;
                StartTimestamp = 0;
                Started = false;
            }
        }

        private struct OwnerSlot
        {
            public uint Generation;
            public int Head;
            public int TimerCount;
            public bool Active;
        }

        private struct TimerNode
        {
            public long DueTimeMs;
            public long IntervalMs;
            public long PausedRemainingMs;
            public long Sequence;
            public TimerCallback Callback;
            public object State;
            public int RemainingCount;
            public int NextInBucket;
            public int PreviousInBucket;
            public int NextInOwner;
            public int PreviousInOwner;
            public int OwnerSlot;
            public int BucketIndex;
            public int HeapIndex;
            public uint Generation;
            public TimerNodeStatus Status;
            public TimerContainer Container;
            public TimerClock Clock;
            public TimerRepeatMode RepeatMode;
            public TimerCatchUpPolicy CatchUpPolicy;
            public TimerCatchUpOverflowPolicy CatchUpOverflowPolicy;
            public TimerExceptionPolicy ExceptionPolicy;
            public byte MaxCatchUpPerTick;
            public bool PauseRequested;
            public bool CanPauseExecuting;
        }

        private enum TimerNodeStatus : byte
        {
            Free = 0,
            Scheduled = 1,
            Executing = 2,
            Paused = 3,
            Cancelled = 4
        }

        private enum TimerContainer : byte
        {
            None = 0,
            Wheel = 1,
            Overflow = 2,
            Overdue = 3,
            Pending = 4
        }
    }

    internal interface ITimerShutdownSink
    {
        void OnSchedulerShutdown();
    }
}
