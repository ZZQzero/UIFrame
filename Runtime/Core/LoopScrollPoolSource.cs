using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Pooling;
using UnityEngine;
using UnityEngine.UI;

namespace UIFrame
{
    /// <summary>
    /// 默认 LoopScroll Prefab 源：GetObject 走 TrySpawn，ReturnObject 走 DespawnImmediate。
    /// </summary>
    public sealed class LoopScrollPoolSource : LoopScrollPrefabSource
    {
        Func<int, string> _locationForIndex;
        string _singleLocation;
        GameObjectPoolService _pool;
        Transform _parent;

        public GameObjectPoolService Pool => _pool;

        public void SetPool(GameObjectPoolService pool)
        {
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        }

        public void SetParent(Transform parent)
        {
            _parent = parent;
        }

        public void SetLocation(string location)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                throw new ArgumentException(
                    "Cell location cannot be empty.",
                    nameof(location));
            }

            _singleLocation = location.Trim();
            _locationForIndex = null;
        }

        public void SetLocation(Func<int, string> locationForIndex)
        {
            _locationForIndex = locationForIndex ??
                throw new ArgumentNullException(nameof(locationForIndex));
            _singleLocation = null;
        }

        public void Bind(LoopScrollRectBase scroll, Func<int, string> locationForIndex)
        {
            if (scroll == null)
            {
                throw new ArgumentNullException(nameof(scroll));
            }

            SetLocation(locationForIndex);
            SetParent(scroll.content);
            scroll.prefabSource = this;
        }

        public void EnsurePool()
        {
            if (_pool == null)
            {
                throw new InvalidOperationException(
                    "LoopScrollPoolSource has no pool. Call SetPool before the list creates cells.");
            }
        }

        public async UniTask PrepareLocationsAsync(
            IReadOnlyList<string> locations,
            GameObjectPoolOptions options = null,
            CancellationToken cancellationToken = default)
        {
            EnsurePool();
            if (locations == null)
            {
                throw new ArgumentNullException(nameof(locations));
            }

            for (int i = 0; i < locations.Count; i++)
            {
                await _pool.PrepareAsync(locations[i], options, cancellationToken);
            }
        }

        public async UniTask PrewarmLocationsAsync(
            IReadOnlyList<string> locations,
            int count,
            GameObjectPoolOptions options = null,
            CancellationToken cancellationToken = default)
        {
            EnsurePool();
            if (locations == null)
            {
                throw new ArgumentNullException(nameof(locations));
            }

            for (int i = 0; i < locations.Count; i++)
            {
                await _pool.PrewarmAsync(locations[i], count, options, cancellationToken);
            }
        }

        public static void Clear(LoopScrollRectBase scroll)
        {
            if (scroll == null ||
                scroll.content == null ||
                scroll.prefabSource == null)
            {
                return;
            }

            scroll.ClearCells();
        }

        public GameObject GetObject(int index)
        {
            EnsurePool();
            string location = ResolveLocation(index);
            if (_pool.TrySpawn(location, _parent, out GameObject instance))
            {
                return instance;
            }

            throw new InvalidOperationException(
                $"LoopScroll cell '{location}' is not prepared. Call PrepareAsync or PrewarmAsync before refill.");
        }

        public void ReturnObject(Transform trans)
        {
            if (trans == null)
            {
                return;
            }

            if (_pool == null || !_pool.DespawnImmediate(trans.gameObject))
            {
                Debug.LogError(
                    $"[UIFrame] Failed to despawn LoopScroll cell '{trans.name}'. " +
                    "Call SetPool and return only active pooled instances.",
                    trans);
            }
        }

        string ResolveLocation(int index)
        {
            string location = _locationForIndex != null
                ? _locationForIndex(index)
                : _singleLocation;

            if (string.IsNullOrWhiteSpace(location))
            {
                throw new InvalidOperationException(
                    $"LoopScroll cell location for index {index} is empty.");
            }

            return location.Trim();
        }
    }
}
