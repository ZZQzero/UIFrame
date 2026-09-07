namespace Game
{
    interface IEventBucket
    {
        string TypeName { get; }
        int ListenerCount { get; }
        bool IsPublishing { get; }
        bool Unsubscribe(int slot, int generation, bool updateOwner = true);
        void Clear();
        int DrainPosted(int max);
    }
}
