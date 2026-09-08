using System;
using UnityEngine;

namespace Game.Timer
{
    public interface ITimeSource
    {
        long NowMs { get; }
    }

    public sealed class SimulationClock : ITimeSource
    {
        public long NowMs { get; private set; }

        public SimulationClock(long initialTimeMs = 0)
        {
            if (initialTimeMs < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(initialTimeMs),
                    initialTimeMs,
                    "SimulationClock 初始时间不能小于 0。");
            }

            NowMs = initialTimeMs;
        }

        public void AdvanceBy(long deltaMs)
        {
            if (deltaMs < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(deltaMs),
                    deltaMs,
                    "SimulationClock 不能倒退。");
            }

            try
            {
                NowMs = checked(NowMs + deltaMs);
            }
            catch (OverflowException exception)
            {
                throw new TimerClockException(
                    $"SimulationClock.AdvanceBy 溢出。NowMs={NowMs}, DeltaMs={deltaMs}。")
                {
                    Source = exception.Source
                };
            }
        }

        public void AdvanceTo(long targetMs)
        {
            if (targetMs < NowMs)
            {
                throw new TimerClockException(
                    $"SimulationClock.AdvanceTo 禁止倒退。NowMs={NowMs}, TargetMs={targetMs}。");
            }

            NowMs = targetMs;
        }
    }

    internal enum UnityTimeKind : byte
    {
        Scaled,
        Unscaled,
        Realtime
    }

    internal sealed class UnityTimeSource : ITimeSource
    {
        private readonly UnityTimeKind kind;
        private long lastTimeMs;
        private bool hasValue;
        private bool rollbackWarningWritten;

        public UnityTimeSource(UnityTimeKind kind)
        {
            this.kind = kind;
        }

        public long NowMs
        {
            get
            {
                double seconds;
                switch (kind)
                {
                    case UnityTimeKind.Scaled:
                        seconds = Time.timeAsDouble;
                        break;
                    case UnityTimeKind.Unscaled:
                        seconds = Time.unscaledTimeAsDouble;
                        break;
                    case UnityTimeKind.Realtime:
                        seconds = Time.realtimeSinceStartupAsDouble;
                        break;
                    default:
                        throw new TimerClockException(
                            $"UnityTimeSource 包含未知时间类型 {(byte)kind}。");
                }

                if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                {
                    throw new TimerClockException(
                        $"UnityTimeSource 读取到非法时间。Kind={kind}, Seconds={seconds}。");
                }

                long current = seconds >= long.MaxValue / 1000d
                    ? long.MaxValue
                    : (long)(seconds * 1000d);

                if (hasValue && current < lastTimeMs)
                {
                    if (!rollbackWarningWritten)
                    {
                        rollbackWarningWritten = true;
                        Debug.LogWarning(
                            $"[GameTimer] Unity {kind} 时间发生回退，已钳制。" +
                            $" PreviousMs={lastTimeMs}, CurrentMs={current}。");
                    }

                    return lastTimeMs;
                }

                hasValue = true;
                lastTimeMs = current;
                return current;
            }
        }
    }
}
