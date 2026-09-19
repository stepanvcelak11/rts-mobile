using System;
using System.Collections.Generic;
using RTS.Sim.Core;

namespace RTS.Sim.Model
{
    /// <summary>
    /// Direction-to-goal for every cell of the map, computed once per goal and shared by every
    /// unit heading there. Integer Dijkstra over the grid (8-neighbourhood, no corner cutting),
    /// so it is deterministic and cheap: a 64×64 map is ~4k cells.
    /// </summary>
    public sealed class FlowField
    {
        public const int Unreachable = int.MaxValue;
        public const byte NoDir = 255;

        public static readonly int[] DX = { 1, 1, 0, -1, -1, -1, 0, 1 };
        public static readonly int[] DY = { 0, 1, 1, 1, 0, -1, -1, -1 };
        private static readonly int[] StepCost = { 10, 14, 10, 14, 10, 14, 10, 14 };

        public readonly long Key;
        public readonly int Width, Height;
        /// <summary>Integrated travel cost ×10 from each cell to the goal; 0 on goal cells.</summary>
        public readonly int[] Cost;
        /// <summary>Neighbour index (0..7) to step to, NoDir on goal/unreachable cells.</summary>
        public readonly byte[] Dir;

        public int LastUsedTick;

        private FlowField(long key, int w, int h)
        {
            Key = key; Width = w; Height = h;
            Cost = new int[w * h];
            Dir = new byte[w * h];
        }

        public bool IsGoal(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && Cost[y * Width + x] == 0;
        public bool IsReachable(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && Cost[y * Width + x] != Unreachable;

        /// <summary>Centre of the next cell on the way to the goal, or false when the cell is a goal / unreachable.</summary>
        public bool TryNext(int x, int y, out FixVec2 next)
        {
            next = default;
            if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
            byte d = Dir[y * Width + x];
            if (d == NoDir) return false;
            next = FixVec2.CellCenter(x + DX[d], y + DY[d]);
            return true;
        }

        private static int TerrainCost(TerrainType t) => t == TerrainType.Sand ? 13 : 10;

        /// <summary>Builds the field toward a set of goal cells (impassable goal cells are allowed: they get cost 0 but are never entered).</summary>
        public static FlowField Build(GridMap map, List<int> goalCells, long key)
        {
            var f = new FlowField(key, map.Width, map.Height);
            int n = f.Cost.Length;
            for (int i = 0; i < n; i++) { f.Cost[i] = Unreachable; f.Dir[i] = NoDir; }

            var heap = new MinHeap(Math.Max(64, goalCells.Count * 4));
            foreach (int g in goalCells)
            {
                if (g < 0 || g >= n || f.Cost[g] == 0) continue;
                f.Cost[g] = 0;
                heap.Push(0, g);
            }

            int w = map.Width, h = map.Height;
            while (heap.Count > 0)
            {
                heap.Pop(out int cost, out int cell);
                if (cost > f.Cost[cell]) continue;   // stale entry
                int cx = cell % w, cy = cell / w;
                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + DX[d], ny = cy + DY[d];
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    if (!map.IsPassable(nx, ny)) continue;
                    // No corner cutting: a diagonal step needs both orthogonal neighbours free.
                    if ((d & 1) == 1 && (!map.IsPassable(cx + DX[d], cy) || !map.IsPassable(cx, cy + DY[d]))) continue;
                    int step = StepCost[d] * TerrainCost(map.TerrainAt(nx, ny)) / 10;
                    int nc = cost + step;
                    int ni = ny * w + nx;
                    if (nc < f.Cost[ni])
                    {
                        f.Cost[ni] = nc;
                        // Direction from the neighbour back toward `cell` = opposite of d.
                        f.Dir[ni] = (byte)((d + 4) & 7);
                        heap.Push(nc, ni);
                    }
                }
            }
            return f;
        }

        /// <summary>Binary min-heap of (cost, cell) with deterministic tie-breaking by cell index.</summary>
        private sealed class MinHeap
        {
            private long[] _items;   // (cost << 32) | cell → ordering by cost then cell
            public int Count;

            public MinHeap(int capacity) { _items = new long[capacity]; }

            public void Push(int cost, int cell)
            {
                if (Count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
                long v = ((long)cost << 32) | (uint)cell;
                int i = Count++;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (_items[p] <= v) break;
                    _items[i] = _items[p];
                    i = p;
                }
                _items[i] = v;
            }

            public void Pop(out int cost, out int cell)
            {
                long top = _items[0];
                cost = (int)(top >> 32);
                cell = (int)(top & 0xFFFFFFFF);
                long last = _items[--Count];
                if (Count == 0) return;
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    long mv = last;
                    if (l < Count && _items[l] < mv) { m = l; mv = _items[l]; }
                    if (r < Count && _items[r] < mv) { m = r; mv = _items[r]; }
                    if (m == i) break;
                    _items[i] = _items[m];
                    i = m;
                }
                _items[i] = last;
            }
        }
    }

    /// <summary>LRU cache of flow fields keyed by goal. Cleared whenever static occupancy changes.</summary>
    public sealed class FlowFieldCache
    {
        public const int Capacity = 64;
        public const int BuildsPerTick = 8;

        private readonly Dictionary<long, FlowField> _fields = new Dictionary<long, FlowField>();
        private readonly List<int> _goalBuffer = new List<int>(64);
        private int _builtThisTick;
        private int _lastTick = -1;

        public int Count => _fields.Count;
        public int TotalBuilds { get; private set; }

        public static long PointKey(int cell) => (1L << 40) | (uint)cell;
        public static long EntityKey(int entity) => (2L << 40) | (uint)entity;

        public void Invalidate() => _fields.Clear();

        /// <summary>
        /// Field for a point goal (its cell, or the nearest passable cell). Returns null when the
        /// per-tick build budget is exhausted — callers fall back to straight-line steering.
        /// </summary>
        public FlowField ForPoint(World w, FixVec2 point)
        {
            int cx = point.CellX, cy = point.CellY;
            if (!w.Map.IsPassable(cx, cy) && !FindNearestPassable(w.Map, ref cx, ref cy, 6)) return null;
            int cell = cy * w.Map.Width + cx;
            long key = PointKey(cell);
            if (_fields.TryGetValue(key, out FlowField f)) { f.LastUsedTick = w.Tick; return f; }
            if (!Budget(w.Tick)) return null;
            _goalBuffer.Clear();
            _goalBuffer.Add(cell);
            return Insert(w, FlowField.Build(w.Map, _goalBuffer, key));
        }

        /// <summary>Field whose goal is the ring of passable cells around an entity footprint.</summary>
        public FlowField ForEntity(World w, int entity)
        {
            long key = EntityKey(entity);
            if (_fields.TryGetValue(key, out FlowField f)) { f.LastUsedTick = w.Tick; return f; }
            if (!w.Footprints.TryGet(entity, out Footprint fp)) return null;
            if (!Budget(w.Tick)) return null;
            _goalBuffer.Clear();
            for (int ring = 1; ring <= 2 && _goalBuffer.Count == 0; ring++)
            {
                int x0 = fp.X - ring, y0 = fp.Y - ring, x1 = fp.X + fp.W - 1 + ring, y1 = fp.Y + fp.H - 1 + ring;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        bool onRing = x == x0 || x == x1 || y == y0 || y == y1;
                        if (onRing && w.Map.IsPassable(x, y)) _goalBuffer.Add(y * w.Map.Width + x);
                    }
            }
            if (_goalBuffer.Count == 0) return null;
            return Insert(w, FlowField.Build(w.Map, _goalBuffer, key));
        }

        private bool Budget(int tick)
        {
            if (tick != _lastTick) { _lastTick = tick; _builtThisTick = 0; }
            if (_builtThisTick >= BuildsPerTick) return false;
            _builtThisTick++;
            TotalBuilds++;
            return true;
        }

        private FlowField Insert(World w, FlowField f)
        {
            if (_fields.Count >= Capacity)
            {
                long oldestKey = 0; int oldestTick = int.MaxValue;
                foreach (var kv in _fields)
                    if (kv.Value.LastUsedTick < oldestTick || (kv.Value.LastUsedTick == oldestTick && kv.Key < oldestKey))
                    { oldestTick = kv.Value.LastUsedTick; oldestKey = kv.Key; }
                _fields.Remove(oldestKey);
            }
            f.LastUsedTick = w.Tick;
            _fields[f.Key] = f;
            return f;
        }

        public static bool FindNearestPassable(GridMap map, ref int cx, ref int cy, int maxRing)
        {
            for (int ring = 1; ring <= maxRing; ring++)
                for (int dy = -ring; dy <= ring; dy++)
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        if (Math.Abs(dx) != ring && Math.Abs(dy) != ring) continue;
                        if (map.IsPassable(cx + dx, cy + dy)) { cx += dx; cy += dy; return true; }
                    }
            return false;
        }
    }
}
