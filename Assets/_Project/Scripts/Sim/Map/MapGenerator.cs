using System;
using System.Collections.Generic;
using RTS.Data;
using RTS.Sim.Core;

namespace RTS.Sim.Model
{
    /// <summary>
    /// Deterministic random maps (same seed → same map on every peer). Each type places two
    /// mirrored start areas with a fair set of nearby resources, then paints its own features:
    /// rivers with fords, forests, lakes, cliff plateaus, neutral mines in the middle.
    /// Connectivity between the starts is verified with a flow field and carved if needed.
    /// </summary>
    public static class MapGenerator
    {
        public static readonly string[] Types = { "twoRivers", "greatPlains", "highlands", "lakeland" };

        public static MapDef Generate(string type, uint seed, int size = 80)
        {
            size = Math.Max(48, Math.Min(160, size));
            var rng = new DetRandom(seed ^ 0xA5A5A5A5u);
            var g = new Grid(size, size);
            var def = new MapDef { id = "map.gen", name = Name(type), size = new List<int> { size, size }, players = 2 };

            // Start positions on a diagonal, mirrored.
            int inset = size / 5;
            var starts = new[] { (inset, inset), (size - 1 - inset, size - 1 - inset) };
            foreach (var s in starts) def.starts.Add(new StartPositionDef { x = s.Item1, y = s.Item2 });

            switch (type)
            {
                case "greatPlains": Plains(g, rng); break;
                case "highlands": Highlands(g, rng, starts); break;
                case "lakeland": Lakeland(g, rng, starts); break;
                default: TwoRivers(g, rng, size); break;
            }

            // Keep start areas buildable.
            foreach (var s in starts) g.Fill(s.Item1 - 8, s.Item2 - 8, 17, 17, TerrainType.Grass, 0);
            Sand(g, rng);
            EnsureConnected(g, starts[0], starts[1]);

            // Resources: mirrored around each start, then neutral wealth in the middle band.
            var occ = new Occupancy(g);
            foreach (var s in starts) { occ.Take(s.Item1 - 3, s.Item2 - 3, 7, 7); occ.Take(s.Item1 - 4, s.Item2 - 7, 9, 4); }   // town center + spawn rows
            foreach (var s in starts) StartResources(g, rng, occ, s.Item1, s.Item2, def);
            MiddleResources(g, rng, occ, starts, def);
            Forests(g, rng, occ, def, starts);
            Treasures(g, rng, occ, def, starts);

            def.patches.AddRange(g.ToPatches());
            return def;
        }

        private static string Name(string type) => type switch
        {
            "greatPlains" => "Great Plains", "highlands" => "Highlands", "lakeland" => "Lakeland", _ => "Two Rivers",
        };

        // ---- terrain features ----------------------------------------------------------------

        private static void TwoRivers(Grid g, DetRandom rng, int size)
        {
            // Two rivers cross the map (one horizontal-ish, one vertical-ish), each with two fords.
            River(g, rng, horizontal: true, size);
            River(g, rng, horizontal: false, size);
        }

        private static void River(Grid g, DetRandom rng, bool horizontal, int size)
        {
            int pos = size / 2 + rng.Range(-4, 5);
            int width = 3;
            var fords = new List<int> { size / 4 + rng.Range(-3, 4), 3 * size / 4 + rng.Range(-3, 4) };
            for (int t = 0; t < size; t++)
            {
                if (t % 9 == 0) pos += rng.Range(-1, 2);
                bool ford = false;
                foreach (int f in fords) if (Math.Abs(t - f) <= 2) ford = true;
                for (int w = -width / 2; w <= width / 2; w++)
                {
                    int x = horizontal ? t : pos + w, y = horizontal ? pos + w : t;
                    if (!ford) g.Set(x, y, TerrainType.Water);
                    else g.Set(x, y, TerrainType.Sand);
                }
            }
        }

        private static void Plains(Grid g, DetRandom rng)
        {
            // Dirt patches and a few ponds.
            for (int i = 0; i < 10; i++) Blob(g, rng, rng.Range(4, g.W - 4), rng.Range(4, g.H - 4), rng.Range(3, 7), TerrainType.Dirt);
            for (int i = 0; i < 4; i++) Blob(g, rng, rng.Range(10, g.W - 10), rng.Range(10, g.H - 10), rng.Range(2, 4), TerrainType.Water);
        }

        private static void Highlands(Grid g, DetRandom rng, (int, int)[] starts)
        {
            // Cliff-ringed plateaus with two ramps each; the middle is a big plateau.
            int cx = g.W / 2, cy = g.H / 2;
            Plateau(g, rng, cx, cy, g.W / 5, 1);
            for (int i = 0; i < 6; i++)
            {
                int px = rng.Range(8, g.W - 8), py = rng.Range(8, g.H - 8);
                bool nearStart = false;
                foreach (var s in starts) if (Math.Abs(px - s.Item1) < 16 && Math.Abs(py - s.Item2) < 16) nearStart = true;
                if (!nearStart) Plateau(g, rng, px, py, rng.Range(4, 8), 1);
            }
            for (int i = 0; i < 8; i++) Blob(g, rng, rng.Range(4, g.W - 4), rng.Range(4, g.H - 4), rng.Range(2, 5), TerrainType.Dirt);
        }

        private static void Plateau(Grid g, DetRandom rng, int cx, int cy, int r, int elevation)
        {
            // Filled disc at +elevation, ring of cliff cells except at two ramp openings.
            int rampA = rng.Range(0, 8), rampB = (rampA + 4) % 8;
            for (int y = cy - r - 1; y <= cy + r + 1; y++)
                for (int x = cx - r - 1; x <= cx + r + 1; x++)
                {
                    int d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    if (d2 <= r * r) g.Set(x, y, TerrainType.Grass, elevation);
                }
            for (int y = cy - r - 1; y <= cy + r + 1; y++)
                for (int x = cx - r - 1; x <= cx + r + 1; x++)
                {
                    if (!g.In(x, y) || g.Elev(x, y) != elevation) continue;
                    bool edge = false;
                    for (int d = 0; d < 4 && !edge; d++)
                    {
                        int nx = x + FlowField.DX[d * 2], ny = y + FlowField.DY[d * 2];
                        if (g.In(nx, ny) && g.Elev(nx, ny) < elevation) edge = true;
                    }
                    if (!edge) continue;
                    int octant = Octant(x - cx, y - cy);
                    if (octant == rampA || octant == rampB) g.Set(x, y, TerrainType.Dirt, elevation - 1);   // ramp: walkable, lower
                    else g.Set(x, y, TerrainType.Cliff, elevation);
                }
        }

        private static int Octant(int dx, int dy)
        {
            // 0..7 going counter-clockwise from east.
            if (dx == 0 && dy == 0) return 0;
            int adx = Math.Abs(dx), ady = Math.Abs(dy);
            bool steep = ady > adx;
            if (dx >= 0 && dy >= 0) return steep ? 1 : 0;
            if (dx < 0 && dy >= 0) return steep ? 2 : 3;
            if (dx < 0) return steep ? 5 : 4;
            return steep ? 6 : 7;
        }

        private static void Lakeland(Grid g, DetRandom rng, (int, int)[] starts)
        {
            for (int i = 0; i < 9; i++)
            {
                int lx = rng.Range(8, g.W - 8), ly = rng.Range(8, g.H - 8);
                bool nearStart = false;
                foreach (var s in starts) if (Math.Abs(lx - s.Item1) < 14 && Math.Abs(ly - s.Item2) < 14) nearStart = true;
                if (!nearStart) Blob(g, rng, lx, ly, rng.Range(3, 7), TerrainType.Water);
            }
            for (int i = 0; i < 6; i++) Blob(g, rng, rng.Range(4, g.W - 4), rng.Range(4, g.H - 4), rng.Range(2, 5), TerrainType.Dirt);
        }

        private static void Blob(Grid g, DetRandom rng, int cx, int cy, int r, TerrainType t)
        {
            for (int y = cy - r; y <= cy + r; y++)
                for (int x = cx - r; x <= cx + r; x++)
                {
                    int d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    int wobble = rng.Range(0, 3);
                    if (d2 <= (r - wobble) * (r - wobble)) g.Set(x, y, t);
                }
        }

        private static void Sand(Grid g, DetRandom rng)
        {
            // Sandy shores around water.
            for (int y = 0; y < g.H; y++)
                for (int x = 0; x < g.W; x++)
                {
                    if (g.Type(x, y) != TerrainType.Grass && g.Type(x, y) != TerrainType.Dirt) continue;
                    bool shore = false;
                    for (int d = 0; d < 8 && !shore; d++)
                        if (g.In(x + FlowField.DX[d], y + FlowField.DY[d]) && g.Type(x + FlowField.DX[d], y + FlowField.DY[d]) == TerrainType.Water) shore = true;
                    if (shore && rng.Range(0, 4) != 0) g.Set(x, y, TerrainType.Sand, g.Elev(x, y));
                }
        }

        // ---- resources ------------------------------------------------------------------------

        private static void StartResources(Grid g, DetRandom rng, Occupancy occ, int sx, int sy, MapDef def)
        {
            // Berries + hunt on one side, a mine on another, a forest wedge on a third.
            int dirX = sx < g.W / 2 ? 1 : -1, dirY = sy < g.H / 2 ? 1 : -1;
            PlaceNear(g, occ, def, "res.berries", sx + dirX * 8, sy - dirY * 2, 2, 2);
            PlaceNear(g, occ, def, "res.berries", sx + dirX * 8, sy - dirY * 5, 2, 2);
            for (int i = 0; i < 4; i++) PlaceNear(g, occ, def, "res.hunt", sx + dirX * (9 + rng.Range(0, 4)), sy + dirY * (2 + rng.Range(0, 4)), 1, 1);
            PlaceNear(g, occ, def, "res.mine", sx - dirX * 3, sy + dirY * 7, 3, 3);
            // Forest wedge behind the town center.
            for (int i = 0; i < 26; i++)
            {
                int x = sx - dirX * (5 + rng.Range(0, 7)), y = sy - dirY * (4 + rng.Range(0, 8));
                Place(g, occ, def, "res.tree", x, y, 1, 1);
            }
            for (int i = 0; i < 14; i++)
            {
                int x = sx + dirX * rng.Range(-3, 4), y = sy - dirY * (10 + rng.Range(0, 4));
                Place(g, occ, def, "res.tree", x, y, 1, 1);
            }
        }

        private static void MiddleResources(Grid g, DetRandom rng, Occupancy occ, (int, int)[] starts, MapDef def)
        {
            int cx = g.W / 2, cy = g.H / 2;
            Place(g, occ, def, "res.mine", cx - 1, cy - 1, 3, 3, 8000);
            Place(g, occ, def, "res.mine", cx + 12, cy - 12, 3, 3, 5000);
            Place(g, occ, def, "res.mine", cx - 14, cy + 11, 3, 3, 5000);
            for (int i = 0; i < 6; i++) Place(g, occ, def, "res.berries", rng.Range(10, g.W - 12), rng.Range(10, g.H - 12), 2, 2);
            for (int i = 0; i < 10; i++) Place(g, occ, def, "res.hunt", rng.Range(6, g.W - 6), rng.Range(6, g.H - 6), 1, 1);
        }

        /// <summary>Guarded treasures scattered away from the starts: a reason to explore early.</summary>
        private static void Treasures(Grid g, DetRandom rng, Occupancy occ, MapDef def, (int, int)[] starts)
        {
            string[] kinds = { "res.treasure_food", "res.treasure_wood", "res.treasure_gold" };
            int count = 4 + g.W / 20;
            for (int i = 0; i < count; i++)
            {
                int x = rng.Range(6, g.W - 6), y = rng.Range(6, g.H - 6);
                bool nearStart = false;
                foreach (var s in starts) if (Math.Abs(x - s.Item1) < 14 && Math.Abs(y - s.Item2) < 14) nearStart = true;
                if (nearStart || !occ.Free(x, y, 1, 1)) continue;
                occ.Take(x, y, 1, 1);
                def.nodes.Add(new NodePlacementDef { id = kinds[rng.Range(0, kinds.Length)], x = x, y = y, amount = 100 + rng.Range(0, 5) * 50 });
                int wolves = 1 + rng.Range(0, 3);
                for (int k = 0; k < wolves; k++)
                {
                    int wx = x + rng.Range(-2, 3), wy = y + rng.Range(-2, 3);
                    if (g.In(wx, wy) && TerrainTypes.IsWalkable(g.Type(wx, wy))) def.guardians.Add(new GuardianDef { id = "unit.wolf", x = wx, y = wy });
                }
            }
        }

        private static void Forests(Grid g, DetRandom rng, Occupancy occ, MapDef def, (int, int)[] starts)
        {
            int clusters = g.W * g.H / 500;
            for (int c = 0; c < clusters; c++)
            {
                int fx = rng.Range(3, g.W - 3), fy = rng.Range(3, g.H - 3);
                bool nearStart = false;
                foreach (var s in starts) if (Math.Abs(fx - s.Item1) < 12 && Math.Abs(fy - s.Item2) < 12) nearStart = true;
                if (nearStart) continue;
                int n = rng.Range(8, 22);
                for (int i = 0; i < n; i++) Place(g, occ, def, "res.tree", fx + rng.Range(-4, 5), fy + rng.Range(-4, 5), 1, 1);
            }
        }

        private static bool Place(Grid g, Occupancy occ, MapDef def, string id, int x, int y, int w, int h, decimal amount = -1)
        {
            if (!occ.Free(x, y, w, h)) return false;
            occ.Take(x, y, w, h);
            def.nodes.Add(new NodePlacementDef { id = id, x = x, y = y, amount = amount });
            return true;
        }

        /// <summary>Places at the spot or the nearest free spot within a few cells (important start resources).</summary>
        private static void PlaceNear(Grid g, Occupancy occ, MapDef def, string id, int x, int y, int w, int h)
        {
            for (int ring = 0; ring <= 5; ring++)
                for (int dy = -ring; dy <= ring; dy++)
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        if (Math.Abs(dx) != ring && Math.Abs(dy) != ring) continue;
                        if (Place(g, occ, def, id, x + dx, y + dy, w, h)) return;
                    }
        }

        // ---- connectivity ---------------------------------------------------------------------

        private static void EnsureConnected(Grid g, (int, int) a, (int, int) b)
        {
            var map = new GridMap(g.W, g.H);
            for (int y = 0; y < g.H; y++)
                for (int x = 0; x < g.W; x++)
                {
                    ref Cell c = ref map.CellAt(x, y);
                    c.Terrain = g.Type(x, y);
                    c.Elevation = (byte)g.Elev(x, y);
                }
            var goals = new List<int> { b.Item2 * g.W + b.Item1 };
            FlowField f = FlowField.Build(map, goals, 1);
            if (f.IsReachable(a.Item1, a.Item2)) return;
            // Carve a straight 3-wide land bridge between the starts.
            int steps = Math.Max(Math.Abs(b.Item1 - a.Item1), Math.Abs(b.Item2 - a.Item2));
            for (int i = 0; i <= steps; i++)
            {
                int x = a.Item1 + (b.Item1 - a.Item1) * i / steps, y = a.Item2 + (b.Item2 - a.Item2) * i / steps;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (g.In(x + dx, y + dy) && (g.Type(x + dx, y + dy) == TerrainType.Water || g.Type(x + dx, y + dy) == TerrainType.Cliff))
                            g.Set(x + dx, y + dy, TerrainType.Sand, 0);
            }
        }

        // ---- helpers ------------------------------------------------------------------------------

        private sealed class Grid
        {
            public readonly int W, H;
            private readonly TerrainType[] _t;
            private readonly byte[] _e;

            public Grid(int w, int h) { W = w; H = h; _t = new TerrainType[w * h]; _e = new byte[w * h]; }
            public bool In(int x, int y) => x >= 0 && y >= 0 && x < W && y < H;
            public TerrainType Type(int x, int y) => _t[y * W + x];
            public int Elev(int x, int y) => _e[y * W + x];
            public void Set(int x, int y, TerrainType t) { if (In(x, y)) _t[y * W + x] = t; }
            public void Set(int x, int y, TerrainType t, int e) { if (In(x, y)) { _t[y * W + x] = t; _e[y * W + x] = (byte)Math.Max(0, e); } }
            public void Fill(int x0, int y0, int w, int h, TerrainType t, int e)
            {
                for (int y = y0; y < y0 + h; y++) for (int x = x0; x < x0 + w; x++) Set(x, y, t, e);
            }

            /// <summary>Run-length rows → patches (grass at elevation 0 is the default and omitted).</summary>
            public List<TerrainPatchDef> ToPatches()
            {
                var list = new List<TerrainPatchDef>();
                for (int y = 0; y < H; y++)
                {
                    int x = 0;
                    while (x < W)
                    {
                        TerrainType t = Type(x, y); int e = Elev(x, y);
                        int x1 = x;
                        while (x1 < W && Type(x1, y) == t && Elev(x1, y) == e) x1++;
                        if (t != TerrainType.Grass || e != 0)
                            list.Add(new TerrainPatchDef { x = x, y = y, w = x1 - x, h = 1, terrain = TerrainTypes.Name(t), elevation = e });
                        x = x1;
                    }
                }
                return list;
            }
        }

        private sealed class Occupancy
        {
            private readonly Grid _g;
            private readonly bool[] _taken;
            public Occupancy(Grid g) { _g = g; _taken = new bool[g.W * g.H]; }

            public bool Free(int x, int y, int w, int h)
            {
                // One cell of breathing room so nodes never fuse into impassable walls around a start.
                for (int cy = y - 1; cy < y + h + 1; cy++)
                    for (int cx = x - 1; cx < x + w + 1; cx++)
                    {
                        if (!_g.In(cx, cy)) return false;
                        bool inside = cx >= x && cy >= y && cx < x + w && cy < y + h;
                        if (inside && (_taken[cy * _g.W + cx] || !TerrainTypes.IsWalkable(_g.Type(cx, cy)))) return false;
                        if (!inside && _taken[cy * _g.W + cx] && w > 1) return false;
                    }
                return true;
            }

            public void Take(int x, int y, int w, int h)
            {
                for (int cy = y; cy < y + h; cy++) for (int cx = x; cx < x + w; cx++) _taken[cy * _g.W + cx] = true;
            }
        }
    }
}
