using RTS.Sim.Core;
using UnityEngine;

namespace RTS.Presentation
{
    /// <summary>
    /// The one place fixed point becomes float. Sim X → Unity X, Sim Y → Unity Z, ground is y = 0.
    /// </summary>
    public static class SimToUnity
    {
        public static Vector3 ToWorld(FixVec2 p) => new Vector3(p.X.ToFloat(), 0f, p.Y.ToFloat());
        public static Vector3 ToWorld(FixVec2 p, float height) => new Vector3(p.X.ToFloat(), height, p.Y.ToFloat());
        public static FixVec2 ToSim(Vector3 v) => new FixVec2(Fix64.FromDecimal((decimal)v.x), Fix64.FromDecimal((decimal)v.z));

        /// <summary>Snaps a ground point to the bottom-left cell of a w×h footprint centred on it.</summary>
        public static void FootprintOrigin(Vector3 ground, int w, int h, out int x, out int y)
        {
            x = Mathf.FloorToInt(ground.x - w * 0.5f + 0.5f);
            y = Mathf.FloorToInt(ground.z - h * 0.5f + 0.5f);
        }

        public static Vector3 FootprintCenter(int x, int y, int w, int h) => new Vector3(x + w * 0.5f, 0f, y + h * 0.5f);
    }
}
