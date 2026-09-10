using System;
using System.Collections.Generic;

namespace Game.Fsm
{
    public sealed class Fsm<TId, TOwner>
        where TId : struct, Enum
        where TOwner : class
    {
        const int MaxChain = 16;

        enum Gate
        {
            Idle,
            Busy,
            Stopping
        }

        static readonly EqualityComparer<TId> Comparer = EqualityComparer<TId>.Default;

        readonly FsmGraph<TId, TOwner> graph;
        readonly TOwner owner;

        TId current;
        IState<TId, TOwner> currentState;
        ITickState<TId, TOwner> currentTick;
        bool started;
        Gate gate;
        bool inCallback;
        bool hasPending;
        TId pending;

        public Fsm(FsmGraph<TId, TOwner> graph, TOwner owner)
        {
            this.graph = graph ?? throw new ArgumentNullException(nameof(graph));
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public TId Current => current;

        public bool IsStarted => started;

        public bool WantsTick => started && currentTick != null;

        public void Start(TId initial)
        {
            if (started)
            {
                throw new FsmException("Fsm 已经 Start。");
            }

            EnsureIdle();
            FsmGraph<TId, TOwner>.Entry entry = graph.Require(initial);
            graph.Freeze();
            Bind(initial, entry);
            started = true;
            gate = Gate.Busy;
            try
            {
                InvokeEnter();
                PumpQueued();
            }
            catch
            {
                hasPending = false;
                throw;
            }
            finally
            {
                gate = Gate.Idle;
            }
        }

        public void Change(TId next)
        {
            if (!started)
            {
                throw new FsmException("Fsm 尚未 Start。");
            }

            if (gate == Gate.Stopping)
            {
                throw new FsmException("Fsm 正在 Stop，不能 Change。");
            }

            if (Comparer.Equals(next, current))
            {
                if (inCallback)
                {
                    hasPending = false;
                }

                return;
            }

            graph.Require(next);
            if (inCallback)
            {
                pending = next;
                hasPending = true;
                return;
            }

            EnsureIdle();
            pending = next;
            hasPending = true;
            gate = Gate.Busy;
            try
            {
                PumpQueued();
            }
            catch
            {
                hasPending = false;
                throw;
            }
            finally
            {
                gate = Gate.Idle;
            }
        }

        public void Tick(float deltaTime)
        {
            EnsureIdle();
            if (!WantsTick)
            {
                return;
            }

            gate = Gate.Busy;
            try
            {
                InvokeTick(deltaTime);
                PumpQueued();
            }
            catch
            {
                hasPending = false;
                throw;
            }
            finally
            {
                gate = Gate.Idle;
            }
        }

        public void Stop()
        {
            if (!started)
            {
                return;
            }

            if (gate == Gate.Stopping)
            {
                return;
            }

            EnsureIdle();
            gate = Gate.Stopping;
            try
            {
                InvokeExit();
            }
            finally
            {
                hasPending = false;
                started = false;
                current = default;
                currentTick = null;
                currentState = null;
                gate = Gate.Idle;
            }
        }

        void PumpQueued()
        {
            int chain = 0;
            while (hasPending)
            {
                TId next = pending;
                hasPending = false;
                if (Comparer.Equals(next, current))
                {
                    continue;
                }

                chain++;
                if (chain > MaxChain)
                {
                    throw new FsmException($"单次切换超过 {MaxChain} 次，可能形成循环。");
                }

                Apply(next);
            }
        }

        void Apply(TId next)
        {
            FsmGraph<TId, TOwner>.Entry entry = graph.Require(next);
            InvokeExit();
            Bind(next, entry);
            InvokeEnter();
        }

        void Bind(TId id, FsmGraph<TId, TOwner>.Entry entry)
        {
            current = id;
            currentState = entry.State;
            currentTick = entry.Tick;
        }

        void InvokeEnter()
        {
            inCallback = true;
            try
            {
                currentState.OnEnter(this, owner);
            }
            finally
            {
                inCallback = false;
            }
        }

        void InvokeExit()
        {
            inCallback = true;
            try
            {
                currentState.OnExit(this, owner);
            }
            finally
            {
                inCallback = false;
            }
        }

        void InvokeTick(float deltaTime)
        {
            inCallback = true;
            try
            {
                currentTick.OnTick(this, owner, deltaTime);
            }
            finally
            {
                inCallback = false;
            }
        }

        void EnsureIdle()
        {
            if (gate != Gate.Idle)
            {
                throw new FsmException("Fsm 正在处理回调或切换，不能重入。");
            }
        }
    }
}
