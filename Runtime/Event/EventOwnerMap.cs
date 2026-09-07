using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Game
{
    /// <summary>
    /// Owner → 句柄列表。面板/系统销毁时一次退订该 Owner 下全部监听。
    /// </summary>
    static class EventOwnerMap
    {
        static readonly Dictionary<object, List<EventHandle>> Owners =
            new(RefComparer.Instance);

        internal static void Add(object owner, EventHandle handle)
        {
            if (!Owners.TryGetValue(owner, out List<EventHandle> list))
            {
                list = new List<EventHandle>(4);
                Owners.Add(owner, list);
            }

            list.Add(handle);
        }

        internal static void Remove(object owner, EventHandle handle)
        {
            if (owner == null || !Owners.TryGetValue(owner, out List<EventHandle> list))
            {
                return;
            }

            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!list[i].Equals(handle))
                {
                    continue;
                }

                list.RemoveAt(i);
                break;
            }

            if (list.Count == 0)
            {
                Owners.Remove(owner);
            }
        }

        internal static bool TryTake(object owner, out List<EventHandle> list)
        {
            if (owner == null || !Owners.TryGetValue(owner, out list))
            {
                list = null;
                return false;
            }

            Owners.Remove(owner);
            return true;
        }

        internal static void Clear()
        {
            Owners.Clear();
        }

        sealed class RefComparer : IEqualityComparer<object>
        {
            public static readonly RefComparer Instance = new();

            bool IEqualityComparer<object>.Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            int IEqualityComparer<object>.GetHashCode(object obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
