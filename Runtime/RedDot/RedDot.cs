using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace UIFrame
{
    /// <summary>
    /// 基于路径树的红点聚合器。只有叶子可写，父节点的值等于所有子节点之和。
    /// 所有公开 API 只能在 Unity 主线程调用。
    /// </summary>
    public static class RedDot
    {
        private const string RunnerObjectName = "[RedDotRunner]";
        private static readonly StringComparer PathComparer = StringComparer.Ordinal;
        private static readonly Node Root = new(string.Empty, string.Empty, null);
        private static readonly Dictionary<string, Node> Nodes = new(PathComparer);
        private static readonly Dictionary<string, ListenerBucket> Listeners =
            new(PathComparer);
        private static readonly List<DispatchEntry> DispatchEntries = new();

        private static HashSet<string> dirtyWrite = new(PathComparer);
        private static HashSet<string> dirtyRead = new(PathComparer);
        private static bool isFlushing;
        private static int mainThreadId;
        private static RedDotRunner runner;

        /// <summary>
        /// 设置叶子的当前真实数量。数量必须大于等于 0。
        /// </summary>
        public static void Set(string path, int count)
        {
            EnsureMainThread();
            ValidatePath(path);

            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    count,
                    "红点数量不能小于 0。");
            }

            if (!Nodes.TryGetValue(path, out Node node))
            {
                if (count == 0)
                {
                    return;
                }

                node = GetOrCreateLeaf(path);
            }

            if (node.Children.Count != 0)
            {
                throw new InvalidOperationException(
                    $"红点路径 \"{path}\" 已有子节点，父节点不允许直接设置数量。");
            }

            long delta = count - node.Total;
            if (delta == 0)
            {
                return;
            }

            ApplyDelta(node, delta);

            if (count == 0)
            {
                PruneEmptyAncestors(node);
            }
        }

        /// <summary>
        /// 获取路径的聚合数量。路径不存在时返回 0。
        /// </summary>
        public static int Get(string path)
        {
            EnsureMainThread();
            ValidatePath(path);
            return GetUnchecked(path);
        }

        /// <summary>
        /// 删除路径及其整个子树，并同步回扣所有祖先的聚合数量。
        /// </summary>
        public static void Remove(string path)
        {
            EnsureMainThread();
            ValidatePath(path);

            if (!Nodes.TryGetValue(path, out Node node))
            {
                return;
            }

            Node parent = node.Parent;
            long removedTotal = node.Total;

            parent.Children.Remove(node.Segment);
            RemoveSubtree(node);

            if (removedTotal != 0)
            {
                ApplyDelta(parent, -removedTotal);
            }

            PruneEmptyAncestors(parent);
        }

        /// <summary>
        /// 监听路径变化。首次绑定会立即同步回调当前值，重复绑定为空操作。
        /// </summary>
        public static void Bind(string path, Action<int> callback)
        {
            EnsureMainThread();
            ValidatePath(path);

            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            if (!Listeners.TryGetValue(path, out ListenerBucket bucket))
            {
                bucket = new ListenerBucket();
                Listeners.Add(path, bucket);
            }

            if (!bucket.Add(callback))
            {
                return;
            }

            InvokeSafely(callback, GetUnchecked(path));
        }

        /// <summary>
        /// 解除路径监听。未绑定时为空操作。
        /// </summary>
        public static void Unbind(string path, Action<int> callback)
        {
            EnsureMainThread();
            ValidatePath(path);

            if (callback == null ||
                !Listeners.TryGetValue(path, out ListenerBucket bucket))
            {
                return;
            }

            if (bucket.Remove(callback) && bucket.Count == 0)
            {
                Listeners.Remove(path);
                dirtyWrite.Remove(path);
            }
        }

        /// <summary>
        /// 清除全部红点数据但保留监听。非零路径会在下一次 Flush 时通知为 0。
        /// </summary>
        public static void Clear()
        {
            EnsureMainThread();

            foreach (string path in Listeners.Keys)
            {
                if (GetUnchecked(path) != 0)
                {
                    dirtyWrite.Add(path);
                }
            }

            Root.Children.Clear();
            Nodes.Clear();
        }

        /// <summary>
        /// 派发本批次变化。Play 模式会在 LateUpdate 自动调用。
        /// 回调中产生的变化留到下一批派发。
        /// </summary>
        public static void Flush()
        {
            EnsureMainThread();

            if (isFlushing || dirtyWrite.Count == 0)
            {
                return;
            }

            HashSet<string> swap = dirtyRead;
            dirtyRead = dirtyWrite;
            dirtyWrite = swap;
            dirtyWrite.Clear();
            isFlushing = true;

            try
            {
                DispatchEntries.Clear();
                foreach (string path in dirtyRead)
                {
                    if (!Listeners.TryGetValue(path, out ListenerBucket bucket))
                    {
                        continue;
                    }

                    DispatchEntries.Add(
                        new DispatchEntry(GetUnchecked(path), bucket.Callbacks));
                }

                for (int entryIndex = 0;
                     entryIndex < DispatchEntries.Count;
                     entryIndex++)
                {
                    DispatchEntry entry = DispatchEntries[entryIndex];
                    Action<int>[] callbacks = entry.Callbacks;
                    for (int i = 0; i < callbacks.Length; i++)
                    {
                        InvokeSafely(callbacks[i], entry.Value);
                    }
                }
            }
            finally
            {
                DispatchEntries.Clear();
                dirtyRead.Clear();
                isFlushing = false;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntimeState()
        {
            Root.Children.Clear();
            Nodes.Clear();
            Listeners.Clear();
            DispatchEntries.Clear();
            dirtyWrite.Clear();
            dirtyRead.Clear();
            isFlushing = false;
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void CreateRunner()
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            EnsureRunner();
        }

        private static void EnsureRunner()
        {
            if (!Application.isPlaying || runner != null)
            {
                return;
            }

            var gameObject = new GameObject(RunnerObjectName)
            {
                hideFlags = HideFlags.HideInHierarchy
            };
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            runner = gameObject.AddComponent<RedDotRunner>();
        }

        internal static void NotifyRunnerDestroyed(RedDotRunner destroyedRunner)
        {
            if (runner == destroyedRunner)
            {
                runner = null;
            }
        }

        private static Node GetOrCreateLeaf(string path)
        {
            if (Nodes.TryGetValue(path, out Node existing))
            {
                return existing;
            }

            Node parent = Root;
            int segmentStart = 0;

            for (int i = 0; i <= path.Length; i++)
            {
                if (i != path.Length && path[i] != '/')
                {
                    continue;
                }

                string segment = path.Substring(segmentStart, i - segmentStart);
                string currentPath = i == path.Length
                    ? path
                    : path.Substring(0, i);

                if (!parent.Children.TryGetValue(segment, out Node child))
                {
                    if (parent != Root &&
                        parent.Children.Count == 0 &&
                        parent.Total != 0)
                    {
                        throw new InvalidOperationException(
                            $"红点叶子 \"{parent.Path}\" 的数量不为 0，" +
                            $"不能在其下创建子节点 \"{currentPath}\"。");
                    }

                    child = new Node(segment, currentPath, parent);
                    parent.Children.Add(segment, child);
                    Nodes.Add(currentPath, child);
                }

                parent = child;
                segmentStart = i + 1;
            }

            return parent;
        }

        private static void ApplyDelta(Node node, long delta)
        {
            for (Node current = node; current != Root; current = current.Parent)
            {
                int oldValue = ToPublicCount(current.Total);
                current.Total += delta;

                if (current.Total < 0)
                {
                    throw new InvalidOperationException(
                        $"红点节点 \"{current.Path}\" 的聚合数量小于 0。");
                }

                if (oldValue != ToPublicCount(current.Total))
                {
                    MarkDirty(current.Path);
                }
            }
        }

        private static void RemoveSubtree(Node node)
        {
            if (ToPublicCount(node.Total) != 0)
            {
                MarkDirty(node.Path);
            }

            foreach (Node child in node.Children.Values)
            {
                RemoveSubtree(child);
            }

            Nodes.Remove(node.Path);
        }

        private static void MarkDirty(string path)
        {
            if (Listeners.ContainsKey(path))
            {
                dirtyWrite.Add(path);
            }
        }

        private static void PruneEmptyAncestors(Node node)
        {
            while (node != Root &&
                   node.Total == 0 &&
                   node.Children.Count == 0)
            {
                Node parent = node.Parent;
                parent.Children.Remove(node.Segment);
                Nodes.Remove(node.Path);
                node = parent;
            }
        }

        private static int GetUnchecked(string path)
        {
            return Nodes.TryGetValue(path, out Node node)
                ? ToPublicCount(node.Total)
                : 0;
        }

        private static int ToPublicCount(long value)
        {
            return value >= int.MaxValue ? int.MaxValue : (int)value;
        }

        private static void ValidatePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("红点路径不能为空。", nameof(path));
            }

            if (path[0] == '/' || path[path.Length - 1] == '/')
            {
                throw new ArgumentException(
                    $"红点路径不能以 '/' 开头或结尾：\"{path}\"。",
                    nameof(path));
            }

            int segmentStart = 0;
            bool hasNonWhitespace = false;
            for (int i = 0; i <= path.Length; i++)
            {
                if (i != path.Length && path[i] != '/')
                {
                    hasNonWhitespace |= !char.IsWhiteSpace(path[i]);
                    continue;
                }

                if (i == segmentStart || !hasNonWhitespace)
                {
                    throw new ArgumentException(
                        $"红点路径包含空段：\"{path}\"。",
                        nameof(path));
                }

                segmentStart = i + 1;
                hasNonWhitespace = false;
            }
        }

        private static void InvokeSafely(Action<int> callback, int value)
        {
            try
            {
                callback(value);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogException(exception);
            }
        }

        private static void EnsureMainThread()
        {
            if (!UIFrameSafety.ThreadChecks)
            {
                return;
            }

            int currentThreadId = Thread.CurrentThread.ManagedThreadId;
            if (mainThreadId == 0)
            {
                mainThreadId = currentThreadId;
                return;
            }

            if (currentThreadId != mainThreadId)
            {
                throw new InvalidOperationException(
                    "RedDot 只能在 Unity 主线程调用。请先切回主线程。");
            }
        }

        private sealed class Node
        {
            public readonly string Segment;
            public readonly string Path;
            public readonly Node Parent;
            public readonly Dictionary<string, Node> Children =
                new(PathComparer);

            public long Total;

            public Node(string segment, string path, Node parent)
            {
                Segment = segment;
                Path = path;
                Parent = parent;
            }
        }

        private sealed class ListenerBucket
        {
            public Action<int>[] Callbacks { get; private set; } =
                Array.Empty<Action<int>>();

            public int Count => Callbacks.Length;

            public bool Add(Action<int> callback)
            {
                Action<int>[] current = Callbacks;
                if (Array.IndexOf(current, callback) >= 0)
                {
                    return false;
                }

                var next = new Action<int>[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[current.Length] = callback;
                Callbacks = next;
                return true;
            }

            public bool Remove(Action<int> callback)
            {
                Action<int>[] current = Callbacks;
                int index = Array.IndexOf(current, callback);
                if (index < 0)
                {
                    return false;
                }

                if (current.Length == 1)
                {
                    Callbacks = Array.Empty<Action<int>>();
                    return true;
                }

                var next = new Action<int>[current.Length - 1];
                if (index > 0)
                {
                    Array.Copy(current, 0, next, 0, index);
                }

                if (index < current.Length - 1)
                {
                    Array.Copy(
                        current,
                        index + 1,
                        next,
                        index,
                        current.Length - index - 1);
                }

                Callbacks = next;
                return true;
            }
        }

        private readonly struct DispatchEntry
        {
            public readonly int Value;
            public readonly Action<int>[] Callbacks;

            public DispatchEntry(int value, Action<int>[] callbacks)
            {
                Value = value;
                Callbacks = callbacks;
            }
        }
    }

    [DefaultExecutionOrder(32000)]
    internal sealed class RedDotRunner : MonoBehaviour
    {
        private void LateUpdate()
        {
            RedDot.Flush();
        }

        private void OnDestroy()
        {
            RedDot.NotifyRunnerDestroyed(this);
        }
    }
}
