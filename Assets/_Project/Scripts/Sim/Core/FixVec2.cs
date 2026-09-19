using System;

namespace RTS.Sim.Core
{
    /// <summary>2D fixed-point vector on the ground plane. X = east, Y = north (map cells).</summary>
    public readonly struct FixVec2 : IEquatable<FixVec2>
    {
        public readonly Fix64 X;
        public readonly Fix64 Y;

        public static readonly FixVec2 Zero = new FixVec2(Fix64.Zero, Fix64.Zero);

        public FixVec2(Fix64 x, Fix64 y) { X = x; Y = y; }
        public static FixVec2 FromInts(int x, int y) => new FixVec2(Fix64.FromInt(x), Fix64.FromInt(y));
        /// <summary>Centre of a grid cell.</summary>
        public static FixVec2 CellCenter(int cx, int cy) => new FixVec2(Fix64.FromInt(cx) + Fix64.Half, Fix64.FromInt(cy) + Fix64.Half);

        public static FixVec2 operator +(FixVec2 a, FixVec2 b) => new FixVec2(a.X + b.X, a.Y + b.Y);
        public static FixVec2 operator -(FixVec2 a, FixVec2 b) => new FixVec2(a.X - b.X, a.Y - b.Y);
        public static FixVec2 operator -(FixVec2 a) => new FixVec2(-a.X, -a.Y);
        public static FixVec2 operator *(FixVec2 a, Fix64 s) => new FixVec2(a.X * s, a.Y * s);
        public static FixVec2 operator *(Fix64 s, FixVec2 a) => new FixVec2(a.X * s, a.Y * s);
        public static FixVec2 operator /(FixVec2 a, Fix64 s) => new FixVec2(a.X / s, a.Y / s);
        public static bool operator ==(FixVec2 a, FixVec2 b) => a.X == b.X && a.Y == b.Y;
        public static bool operator !=(FixVec2 a, FixVec2 b) => !(a == b);

        public Fix64 Dot(FixVec2 o) => X * o.X + Y * o.Y;
        public Fix64 LengthSq => X * X + Y * Y;
        public Fix64 Length => FixMath.Sqrt(LengthSq);

        /// <summary>Unit-length vector, or Zero when the length is zero.</summary>
        public FixVec2 Normalized
        {
            get
            {
                Fix64 len = Length;
                return len.IsZero ? Zero : new FixVec2(X / len, Y / len);
            }
        }

        /// <summary>Vector clamped to at most <paramref name="maxLength"/>.</summary>
        public FixVec2 ClampLength(Fix64 maxLength)
        {
            Fix64 lsq = LengthSq;
            if (lsq <= maxLength * maxLength) return this;
            return Normalized * maxLength;
        }

        public static FixVec2 Lerp(FixVec2 a, FixVec2 b, Fix64 t) => a + (b - a) * t;
        public static Fix64 Distance(FixVec2 a, FixVec2 b) => (a - b).Length;
        public static Fix64 DistanceSq(FixVec2 a, FixVec2 b) => (a - b).LengthSq;

        public int CellX => X.FloorToInt();
        public int CellY => Y.FloorToInt();

        public bool Equals(FixVec2 other) => this == other;
        public override bool Equals(object obj) => obj is FixVec2 v && v == this;
        public override int GetHashCode() => unchecked(X.GetHashCode() * 397 ^ Y.GetHashCode());
        public override string ToString() => "(" + X + ", " + Y + ")";
    }
}
