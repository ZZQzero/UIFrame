using System;

namespace Game
{
    /// <summary>
    /// 槽位 + 世代句柄。默认值无效；Unsubscribe 后世代递增，旧句柄失效。
    /// </summary>
    public readonly struct EventHandle : IEquatable<EventHandle>
    {
        internal readonly int TypeId;
        internal readonly int Slot;
        internal readonly int Generation;

        internal EventHandle(int typeId, int slot, int generation)
        {
            TypeId = typeId;
            Slot = slot;
            Generation = generation;
        }

        public bool IsValid => Generation != 0;

        public bool Equals(EventHandle other)
        {
            return TypeId == other.TypeId
                && Slot == other.Slot
                && Generation == other.Generation;
        }

        public override bool Equals(object obj)
        {
            return obj is EventHandle other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (TypeId * 397) ^ (Slot * 31) ^ Generation;
            }
        }

        public static bool operator ==(EventHandle left, EventHandle right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(EventHandle left, EventHandle right)
        {
            return !left.Equals(right);
        }
    }
}
