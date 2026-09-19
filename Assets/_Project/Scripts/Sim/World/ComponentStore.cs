using System;
using RTS.Sim.Core;

namespace RTS.Sim.Model
{
    /// <summary>
    /// Sparse-set storage for one component type. Dense arrays give cache-friendly iteration;
    /// the sparse array maps entity id → dense index. Iteration order is insertion order with
    /// swap-remove, which is identical on every peer because every peer performs the same
    /// operations in the same order.
    /// </summary>
    public sealed class ComponentStore<T> : IHashable where T : struct, IHashable
    {
        private T[] _dense = new T[64];
        private int[] _entities = new int[64];   // dense index -> entity id
        private int[] _sparse = new int[256];    // entity id -> dense index + 1 (0 = absent)
        private int _count;

        public int Count => _count;

        public bool Has(int entity) => entity > 0 && entity < _sparse.Length && _sparse[entity] != 0;

        /// <summary>Direct reference into the dense array. Do not keep it across Add/Remove calls.</summary>
        public ref T Get(int entity)
        {
            int idx = _sparse[entity] - 1;
            if (idx < 0) throw new InvalidOperationException($"Entity {entity} has no {typeof(T).Name}");
            return ref _dense[idx];
        }

        public bool TryGet(int entity, out T value)
        {
            if (Has(entity)) { value = _dense[_sparse[entity] - 1]; return true; }
            value = default;
            return false;
        }

        public ref T Add(int entity, in T value)
        {
            if (entity <= 0) throw new ArgumentOutOfRangeException(nameof(entity));
            if (entity >= _sparse.Length) Array.Resize(ref _sparse, Math.Max(entity + 1, _sparse.Length * 2));
            if (_sparse[entity] != 0) throw new InvalidOperationException($"Entity {entity} already has {typeof(T).Name}");
            if (_count == _dense.Length)
            {
                Array.Resize(ref _dense, _dense.Length * 2);
                Array.Resize(ref _entities, _entities.Length * 2);
            }
            _dense[_count] = value;
            _entities[_count] = entity;
            _sparse[entity] = _count + 1;
            _count++;
            return ref _dense[_count - 1];
        }

        public void Set(int entity, in T value)
        {
            if (Has(entity)) _dense[_sparse[entity] - 1] = value;
            else Add(entity, value);
        }

        public bool Remove(int entity)
        {
            if (!Has(entity)) return false;
            int idx = _sparse[entity] - 1;
            int last = _count - 1;
            if (idx != last)
            {
                _dense[idx] = _dense[last];
                _entities[idx] = _entities[last];
                _sparse[_entities[idx]] = idx + 1;
            }
            _dense[last] = default;
            _entities[last] = 0;
            _sparse[entity] = 0;
            _count--;
            return true;
        }

        /// <summary>Entity id at dense position i (for iteration by index).</summary>
        public int EntityAt(int i) => _entities[i];

        /// <summary>Component at dense position i, by reference.</summary>
        public ref T At(int i) => ref _dense[i];

        public void Clear()
        {
            Array.Clear(_sparse, 0, _sparse.Length);
            Array.Clear(_dense, 0, _count);
            Array.Clear(_entities, 0, _count);
            _count = 0;
        }

        public void Hash(ref Hasher h)
        {
            h.Add(_count);
            for (int i = 0; i < _count; i++)
            {
                h.Add(_entities[i]);
                _dense[i].Hash(ref h);
            }
        }
    }
}
