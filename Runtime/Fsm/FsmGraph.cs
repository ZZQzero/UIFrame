using System;
using System.Collections.Generic;

namespace Game.Fsm
{
    public sealed class FsmGraph<TId, TOwner>
        where TId : struct, Enum
        where TOwner : class
    {
        readonly List<Entry> entries = new List<Entry>(8);
        bool frozen;

        public FsmGraph<TId, TOwner> Add(TId id, IState<TId, TOwner> state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            return Add(id, state, state as ITickState<TId, TOwner>);
        }

        public FsmGraph<TId, TOwner> Add(
            TId id,
            Action<Fsm<TId, TOwner>, TOwner> onEnter = null,
            Action<Fsm<TId, TOwner>, TOwner> onExit = null,
            Action<Fsm<TId, TOwner>, TOwner, float> onTick = null)
        {
            var state = new DelegateState(onEnter, onExit, onTick);
            ITickState<TId, TOwner> tick = onTick != null ? state : null;
            return Add(id, state, tick);
        }

        internal void Freeze()
        {
            frozen = true;
        }

        internal Entry Require(TId id)
        {
            int index = IndexOf(id);
            if (index < 0)
            {
                throw new FsmException($"状态 {id} 未注册。");
            }

            return entries[index];
        }

        FsmGraph<TId, TOwner> Add(
            TId id,
            IState<TId, TOwner> state,
            ITickState<TId, TOwner> tick)
        {
            EnsureMutable();
            if (IndexOf(id) >= 0)
            {
                throw new FsmException($"状态 {id} 已注册。");
            }

            entries.Add(new Entry(id, state, tick));
            return this;
        }

        int IndexOf(TId id)
        {
            EqualityComparer<TId> comparer = EqualityComparer<TId>.Default;
            for (int i = 0; i < entries.Count; i++)
            {
                if (comparer.Equals(entries[i].Id, id))
                {
                    return i;
                }
            }

            return -1;
        }

        void EnsureMutable()
        {
            if (frozen)
            {
                throw new FsmException("图已冻结，不能再注册状态。");
            }
        }

        internal readonly struct Entry
        {
            public readonly TId Id;
            public readonly IState<TId, TOwner> State;
            public readonly ITickState<TId, TOwner> Tick;

            public Entry(TId id, IState<TId, TOwner> state, ITickState<TId, TOwner> tick)
            {
                Id = id;
                State = state;
                Tick = tick;
            }
        }

        sealed class DelegateState : ITickState<TId, TOwner>
        {
            readonly Action<Fsm<TId, TOwner>, TOwner> onEnter;
            readonly Action<Fsm<TId, TOwner>, TOwner> onExit;
            readonly Action<Fsm<TId, TOwner>, TOwner, float> onTick;

            public DelegateState(
                Action<Fsm<TId, TOwner>, TOwner> onEnter,
                Action<Fsm<TId, TOwner>, TOwner> onExit,
                Action<Fsm<TId, TOwner>, TOwner, float> onTick)
            {
                this.onEnter = onEnter;
                this.onExit = onExit;
                this.onTick = onTick;
            }

            public void OnEnter(Fsm<TId, TOwner> fsm, TOwner owner)
            {
                onEnter?.Invoke(fsm, owner);
            }

            public void OnExit(Fsm<TId, TOwner> fsm, TOwner owner)
            {
                onExit?.Invoke(fsm, owner);
            }

            public void OnTick(Fsm<TId, TOwner> fsm, TOwner owner, float deltaTime)
            {
                onTick.Invoke(fsm, owner, deltaTime);
            }
        }
    }
}
