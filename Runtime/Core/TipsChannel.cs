using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Cysharp.Threading.Tasks;

[assembly: InternalsVisibleTo("Tips.EditMode.Tests")]

namespace UIFrame
{
    /// <summary>Tips 层默认配置。业务只通过 <see cref="UI.ConfigureTips"/> 修改。</summary>
    public readonly struct TipsSettings
    {
        public const int DefaultMaxVisible = 3;
        public const int DefaultMaxQueued = 8;
        public const float DefaultDurationSeconds = 2f;

        public readonly int MaxVisible;
        public readonly int MaxQueued;
        public readonly float DefaultDuration;

        public static TipsSettings Default => new TipsSettings(
            DefaultMaxVisible,
            DefaultMaxQueued,
            DefaultDurationSeconds);

        public TipsSettings(int maxVisible, int maxQueued, float defaultDuration)
        {
            MaxVisible = maxVisible < 0 ? 0 : maxVisible;
            MaxQueued = maxQueued < 0 ? 0 : maxQueued;
            DefaultDuration = IsFiniteDuration(defaultDuration)
                ? defaultDuration
                : DefaultDurationSeconds;
        }

        internal static bool IsFiniteDuration(float duration)
        {
            return !float.IsNaN(duration) && !float.IsInfinity(duration);
        }
    }

    /// <summary>尚未加载的 Toast。出队后才 Load / 复用闲置实例。</summary>
    sealed class TipsWaitItem
    {
        public Type PanelType;
        public object Args;
        public float Duration;
        public readonly UniTaskCompletionSource<UIPanel> Completion = new UniTaskCompletionSource<UIPanel>();
    }

    /// <summary>Tips 通道：占槽、排队、丢最旧等待项。不含 GameObject。</summary>
    sealed class TipsChannel
    {
        readonly List<TipsWaitItem> _queue = new List<TipsWaitItem>();
        int _inFlight;

        public TipsSettings Settings { get; private set; } = TipsSettings.Default;

        public int InFlight => _inFlight;

        public int Queued => _queue.Count;

        public void Configure(
            int maxVisible,
            int maxQueued,
            float defaultDuration,
            ICollection<TipsWaitItem> dropped)
        {
            Settings = new TipsSettings(maxVisible, maxQueued, defaultDuration);
            if (Settings.MaxVisible <= 0)
            {
                Drain(dropped);
                return;
            }

            Trim(dropped);
        }

        public float ResolveDuration(float? duration)
        {
            var value = duration ?? Settings.DefaultDuration;
            return TipsSettings.IsFiniteDuration(value) ? value : Settings.DefaultDuration;
        }

        public static bool IsSticky(float duration)
        {
            return duration <= 0f;
        }

        public bool HasFreeSlot(int visibleCount)
        {
            return visibleCount + _inFlight < Settings.MaxVisible;
        }

        public void BeginInFlight()
        {
            _inFlight++;
        }

        public void EndInFlight()
        {
            if (_inFlight > 0)
            {
                _inFlight--;
            }
        }

        /// <summary>
        /// 可见已满时入队。队列满则丢掉最旧等待项。MaxQueued 为 0 时无法入队。
        /// </summary>
        public bool TryEnqueue(TipsWaitItem item, out TipsWaitItem droppedOldest)
        {
            droppedOldest = null;
            if (item == null || Settings.MaxQueued <= 0)
            {
                return false;
            }

            if (_queue.Count >= Settings.MaxQueued)
            {
                droppedOldest = _queue[0];
                _queue.RemoveAt(0);
            }

            _queue.Add(item);
            return true;
        }

        public bool TryDequeue(out TipsWaitItem item)
        {
            if (_queue.Count == 0)
            {
                item = null;
                return false;
            }

            item = _queue[0];
            _queue.RemoveAt(0);
            return true;
        }

        public void Drain(ICollection<TipsWaitItem> dest)
        {
            if (dest != null)
            {
                for (var i = 0; i < _queue.Count; i++)
                {
                    dest.Add(_queue[i]);
                }
            }

            _queue.Clear();
        }

        public void DrainWhere(Predicate<TipsWaitItem> match, ICollection<TipsWaitItem> dest)
        {
            if (match == null)
            {
                return;
            }

            for (var i = _queue.Count - 1; i >= 0; i--)
            {
                var item = _queue[i];
                if (item == null || !match(item))
                {
                    continue;
                }

                dest?.Add(item);
                _queue.RemoveAt(i);
            }
        }

        public void ResetRuntime(ICollection<TipsWaitItem> drained)
        {
            Drain(drained);
            _inFlight = 0;
        }

        void Trim(ICollection<TipsWaitItem> dropped)
        {
            while (_queue.Count > Settings.MaxQueued)
            {
                dropped?.Add(_queue[0]);
                _queue.RemoveAt(0);
            }
        }
    }
}
