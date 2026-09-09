using System;
using System.Collections.Generic;

namespace Game.Input
{
    public readonly struct InputLayerHandle : IEquatable<InputLayerHandle>
    {
        readonly uint generation;

        internal InputLayerHandle(uint generation)
        {
            this.generation = generation;
        }

        internal uint Generation => generation;
        public bool IsValid => generation != 0;

        public bool Equals(InputLayerHandle other) =>
            generation == other.generation;

        public override bool Equals(object obj) =>
            obj is InputLayerHandle other && Equals(other);

        public override int GetHashCode() => generation.GetHashCode();

        public override string ToString() =>
            IsValid ? $"InputLayerHandle({generation})" : "InputLayerHandle(invalid)";

        public static bool operator ==(InputLayerHandle left, InputLayerHandle right) =>
            left.Equals(right);

        public static bool operator !=(InputLayerHandle left, InputLayerHandle right) =>
            !left.Equals(right);

        public static InputLayerHandle Invalid => default;
    }

    public sealed class InputStateException : InvalidOperationException
    {
        public InputStateException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// UI 独占锁栈。栈非空时玩法 Map 关闭。Handle 为单调代次，必须弹栈顶。
    /// </summary>
    internal sealed class InputUiLockStack
    {
        readonly List<uint> layers = new();
        uint nextGeneration = 1;

        public int Count => layers.Count;

        public InputLayerHandle TopHandle =>
            layers.Count == 0
                ? InputLayerHandle.Invalid
                : new InputLayerHandle(layers[layers.Count - 1]);

        public void Clear()
        {
            layers.Clear();
        }

        public InputLayerHandle Push()
        {
            uint generation = nextGeneration++;
            if (nextGeneration == 0)
            {
                nextGeneration = 1;
            }

            layers.Add(generation);
            return new InputLayerHandle(generation);
        }

        public void Pop(InputLayerHandle handle)
        {
            if (!TryPop(handle))
            {
                throw new InputStateException(
                    "只能弹出当前栈顶 UI 锁，且不能弹出空栈。");
            }
        }

        public bool TryPop(InputLayerHandle handle)
        {
            if (!handle.IsValid || layers.Count == 0)
            {
                return false;
            }

            if (layers[layers.Count - 1] != handle.Generation)
            {
                return false;
            }

            layers.RemoveAt(layers.Count - 1);
            return true;
        }
    }
}
