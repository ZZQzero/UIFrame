namespace Game.Fsm
{
    public interface IState<TId, TOwner>
        where TId : struct, System.Enum
        where TOwner : class
    {
        void OnEnter(Fsm<TId, TOwner> fsm, TOwner owner);
        void OnExit(Fsm<TId, TOwner> fsm, TOwner owner);
    }

    public interface ITickState<TId, TOwner> : IState<TId, TOwner>
        where TId : struct, System.Enum
        where TOwner : class
    {
        void OnTick(Fsm<TId, TOwner> fsm, TOwner owner, float deltaTime);
    }
}
