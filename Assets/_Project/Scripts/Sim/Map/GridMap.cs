using System;
using RTS.Data;
using RTS.Sim.Core;

namespace RTS.Sim.Model
{
    public enum TerrainType : byte { Grass = 0, Dirt = 1, Sand = 2, Water = 3, Cliff = 4 }

    public static class TerrainTypes
    {
        public static bool TryParse(string name, out TerrainType t)
        {
            switch (name)
            {
                case "grass": t = TerrainType.Grass; return true;
                case "dirt": t = TerrainType.Dirt; return true;
                case "sand": t = TerrainType.Sand; return true;
                case "water": t = TerrainType.Water; return true;
                case "cliff": t = TerrainType.Cliff; return true;
                default: t = TerrainType.Grass; return false;
            }
        }

        public static bool IsWalkable(TerrainType t) => t != TerrainType.Water && t != TerrainType.Cliff;

        public static string Name(TerrainType t) => t switch
        {
            TerrainType.Dirt => "dirt", TerrainType.Sand => "sand", TerrainType.Water => "water", TerrainType.Cliff => "cliff", _ => "grass",
        };
    }

    public struct Cell
    {
        public TerrainType Terrain;
        public byte Elevation;
        public int Occupant;     // building or node entity id, 0 = free
    }

    public enum PlacementResult : byte
    {
        Ok = 0,
        OutOfBounds,
        BadTerrain,
        Occupied,
        UnevenGround,
        LimitReached,
        NotAffordable,
        WrongAge,
    }

    /// <summary>
    /// The playfield: width × height cells of 1 m. Holds static terrain and which building /
    /// resource node occupies each cell. Units do not occupy cells (Phase 3 adds a separate
    /// dynamic occupancy layer for avoidance).
    /// </summary>
    public sealed class GridMap : IHashable
    {
        public readonly int Width;
        public readonly int Height;
        private readonly Cell[] _cells;

        public GridMap(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException("map size");
            Width = width;
            Height = height;
            _cells = new Cell[width * height];
        }

        public static GridMap FromDef(MapDef def)
        {
            var map = new GridMap(def.Width, def.Height);
            foreach (TerrainPatchDef p in def.patches)
            {
                if (!TerrainTypes.TryParse(p.terrain, out TerrainType t)) continue;
                for (int y = p.y; y < p.y + p.h; y++)
                    for (int x = p.x; x < p.x + p.w; x++)
                        if (map.InBounds(x, y))
                        {
                            ref Cell c = ref map._cells[y * map.Width + x];
                            c.Terrain = t;
                            c.Elevation = (byte)p.elevation;
                        }
            }
            return map;
        }

        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        public ref Cell CellAt(int x, int y) => ref _cells[y * Width + x];

        public TerrainType TerrainAt(int x, int y) => _cells[y * Width + x].Terrain;

        /// <summary>Static passability: walkable terrain and not covered by a building or node.</summary>
        public bool IsPassable(int x, int y)
        {
            if (!InBounds(x, y)) return false;
            ref Cell c = ref _cells[y * Width + x];
            return TerrainTypes.IsWalkable(c.Terrain) && c.Occupant == 0;
        }

        public bool IsPassable(FixVec2 p) => IsPassable(p.CellX, p.CellY);

        /// <summary>Validates a w×h footprint with its bottom-left corner at (x, y).</summary>
        public PlacementResult Validate(int x, int y, int w, int h, byte terrainMask)
        {
            if (x < 0 || y < 0 || x + w > Width || y + h > Height) return PlacementResult.OutOfBounds;
            byte elevation = _cells[y * Width + x].Elevation;
            for (int cy = y; cy < y + h; cy++)
                for (int cx = x; cx < x + w; cx++)
                {
                    ref Cell c = ref _cells[cy * Width + cx];
                    if ((terrainMask & (1 << (int)c.Terrain)) == 0) return PlacementResult.BadTerrain;
                    if (c.Occupant != 0) return PlacementResult.Occupied;
                    if (c.Elevation != elevation) return PlacementResult.UnevenGround;
                }
            return PlacementResult.Ok;
        }

        public void Occupy(in Footprint fp, int entity)
        {
            for (int cy = fp.Y; cy < fp.Y + fp.H; cy++)
                for (int cx = fp.X; cx < fp.X + fp.W; cx++)
                    if (InBounds(cx, cy)) _cells[cy * Width + cx].Occupant = entity;
        }

        public void Release(in Footprint fp, int entity)
        {
            for (int cy = fp.Y; cy < fp.Y + fp.H; cy++)
                for (int cx = fp.X; cx < fp.X + fp.W; cx++)
                    if (InBounds(cx, cy) && _cells[cy * Width + cx].Occupant == entity) _cells[cy * Width + cx].Occupant = 0;
        }

        /// <summary>Entity occupying a cell, or 0.</summary>
        public int OccupantAt(int x, int y) => InBounds(x, y) ? _cells[y * Width + x].Occupant : 0;

        /// <summary>
        /// Finds the nearest passable cell centre around a footprint (ring search outward),
        /// used to spawn trained units and to place builders. Returns false if none within maxRing.
        /// </summary>
        public bool FindFreeCellAround(in Footprint fp, int maxRing, out FixVec2 result)
        {
            for (int ring = 1; ring <= maxRing; ring++)
            {
                int x0 = fp.X - ring, y0 = fp.Y - ring, x1 = fp.X + fp.W - 1 + ring, y1 = fp.Y + fp.H - 1 + ring;
                // Walk the ring perimeter in a fixed order (bottom, right, top, left).
                for (int x = x0; x <= x1; x++) if (IsPassable(x, y0)) { result = FixVec2.CellCenter(x, y0); return true; }
                for (int y = y0 + 1; y <= y1; y++) if (IsPassable(x1, y)) { result = FixVec2.CellCenter(x1, y); return true; }
                for (int x = x1 - 1; x >= x0; x--) if (IsPassable(x, y1)) { result = FixVec2.CellCenter(x, y1); return true; }
                for (int y = y1 - 1; y > y0; y--) if (IsPassable(x0, y)) { result = FixVec2.CellCenter(x0, y); return true; }
            }
            result = FixVec2.Zero;
            return false;
        }

        /// <summary>Clamps a point to the map interior (keeps a small margin inside the edge).</summary>
        public FixVec2 ClampInside(FixVec2 p)
        {
            Fix64 margin = Fix64.Ratio(1, 10);
            Fix64 x = FixMath.Clamp(p.X, margin, Fix64.FromInt(Width) - margin);
            Fix64 y = FixMath.Clamp(p.Y, margin, Fix64.FromInt(Height) - margin);
            return new FixVec2(x, y);
        }

        public void Hash(ref Hasher h)
        {
            h.Add(Width); h.Add(Height);
            // Terrain is static and known from the map def; only occupancy changes during play.
            for (int i = 0; i < _cells.Length; i++) h.Add(_cells[i].Occupant);
        }
    }
}
