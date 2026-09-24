using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using Game;
using Game.Timer;

namespace UIFrame
{
    /// <summary>
    /// 面板生命周期作用域。作用域结束时取消令牌并释放已登记的资源。
    /// </summary>
    public sealed class UIFrameScope : IDisposable
    {
        readonly CancellationTokenSource _cts;
        List<Action> _cleanups;
        TimerOwner _timerOwner;
        bool _hasEventSubscriptions;
        bool _disposed;

        internal UIFrameScope(CancellationToken linkedToken)
        {
            _cts = linkedToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(linkedToken)
                : new CancellationTokenSource();
        }

        public CancellationToken Token => _cts.Token;

        public bool IsDisposed => _disposed;

        /// <summary>登记一个在作用域结束时执行的清理动作。</summary>
        public void Register(Action cleanup)
        {
            if (cleanup == null)
                throw new ArgumentNullException(nameof(cleanup));
            ThrowIfDisposed();
            (_cleanups ??= new List<Action>()).Add(cleanup);
        }

        /// <summary>登记一个在作用域结束时释放的资源。</summary>
        public void Register(IDisposable disposable)
        {
            if (disposable == null)
                throw new ArgumentNullException(nameof(disposable));
            Register(disposable.Dispose);
        }

        /// <summary>订阅事件，并在作用域结束时自动取消订阅。</summary>
        public EventHandle Subscribe<T>(Action<T> handler)
        {
            ThrowIfDisposed();
            EventHandle handle = EventSystem.Subscribe(this, handler);
            _hasEventSubscriptions = true;
            return handle;
        }

        /// <summary>创建归属于作用域的计时器。</summary>
        public TimerHandle Schedule(in TimerOptions options, Game.Timer.TimerCallback callback)
        {
            ThrowIfDisposed();
            if (!_timerOwner.IsValid)
                _timerOwner = GameTimer.CreateOwner();
            return GameTimer.Schedule(options.WithOwner(_timerOwner), callback);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            ExceptionDispatchInfo first = null;
            try
            {
                _cts.Cancel();
            }
            catch (Exception exception)
            {
                first = ExceptionDispatchInfo.Capture(exception);
            }

            if (_hasEventSubscriptions)
            {
                try
                {
                    EventSystem.UnsubscribeAll(this);
                }
                catch (Exception exception)
                {
                    first ??= ExceptionDispatchInfo.Capture(exception);
                }
                _hasEventSubscriptions = false;
            }

            if (_cleanups != null)
            {
                for (int i = _cleanups.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        _cleanups[i]();
                    }
                    catch (Exception exception)
                    {
                        first ??= ExceptionDispatchInfo.Capture(exception);
                    }
                }
                _cleanups.Clear();
            }

            if (_timerOwner.IsValid && GameTimer.IsInited)
            {
                try
                {
                    GameTimer.CancelOwner(_timerOwner);
                    GameTimer.ReleaseOwner(_timerOwner);
                }
                catch (Exception exception)
                {
                    first ??= ExceptionDispatchInfo.Capture(exception);
                }
            }

            _cts.Dispose();
            first?.Throw();
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UIFrameScope));
        }
    }
}
