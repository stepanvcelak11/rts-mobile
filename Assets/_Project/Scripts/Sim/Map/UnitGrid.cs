using System;
using System.Collections.Generic;
using RTS.Sim.Core;

namespace RTS.Sim.Model
{
    /// <summary>
    /// Cell-bucketed index of every live unit, rebuilt once per tick. Neighbour queries for
    /// separation, target acquisition and picking are O(cells in range) instead of O(units).
    /// Iteration order inside a bucket is insertion order (= dense store order), so it is
    /// identical on every peer.
    /// </summary>
    public sealed class UnitGrid
    {
        private readonly int _width, _height;
        private int[] _head;      // cell → first slot + 1 (0 = empty)
        private int[] _next;      // slot → next slot + 1
        private int[] _entity;    // slot → entity id
        private int _count;

        public UnitGrid(int width, int height)
        {
            _width = width; _height = height;
            _head = new int[width * height];
            _next = new int[256];
            _entity = new int[256];
        }

        public void Rebuild(World w)
        {
            Array.Clear(_head, 0, _head.Length);
            _count = 0;
            for (int i = 0; i < w.Movers.Count; i++)
            {
                int e = w.Movers.EntityAt(i);
                if (!w.Positions.TryGet(e, out Position p)) continue;
                if (w.Behaviours.TryGet(e, out UnitBehaviour b) && b.State == UnitState.Dead) continue;
                Insert(e, p.Value);
            }
        }

        private void Insert(int entity, FixVec2 pos)
        {
            int cx = Math.Min(Math.Max(pos.CellX, 0), _width - 1);
            int cy = Math.Min(Math.Max(pos.CellY, 0), _height - 1);
            int cell = cy * _width + cx;
            if (_count == _entity.Length)
            {
                Array.Resize(ref _next, _next.Length * 2);
                Array.Resize(ref _entity, _entity.Length * 2);
            }
            _entity[_count] = entity;
            _next[_count] = _head[cell];
            _head[cell] = _count + 1;
            _count++;
        }

        /// <summary>Appends every unit whose cell lies within <paramref name="radius"/> cells of the point (coarse; caller refines by distance).</summary>
        public void Query(FixVec2 center, Fix64 radius, List<int> result)
        {
            int r = radius.CeilToInt();
            int cx = center.CellX, cy = center.CellY;
            int x0 = Math.Max(0, cx - r), x1 = Math.Min(_width - 1, cx + r);
            int y0 = Math.Max(0, cy - r), y1 = Math.Min(_height - 1, cy + r);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int slot = _head[y * _width + x];
                    while (slot != 0)
                    {
                        result.Add(_entity[slot - 1]);
                        slot = _next[slot - 1];
                    }
                }
        }

        /// <summary>Units whose cell lies within the rectangle inflated by <paramref name="radius"/> cells.</summary>
        public void QueryRect(int x0, int y0, int x1, int y1, int radius, List<int> result)
        {
            x0 = Math.Max(0, x0 - radius); y0 = Math.Max(0, y0 - radius);
            x1 = Math.Min(_width - 1, x1 + radius); y1 = Math.Min(_height - 1, y1 + radius);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int slot = _head[y * _width + x];
                    while (slot != 0)
                    {
                        result.Add(_entity[slot - 1]);
                        slot = _next[slot - 1];
                    }
                }
        }
    }
}
