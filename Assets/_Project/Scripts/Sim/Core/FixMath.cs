using System;

namespace RTS.Sim.Core
{
    /// <summary>Deterministic math helpers on <see cref="Fix64"/>. No System.Math on fixed values.</summary>
    public static class FixMath
    {
        public static Fix64 Min(Fix64 a, Fix64 b) => a.Raw < b.Raw ? a : b;
        public static Fix64 Max(Fix64 a, Fix64 b) => a.Raw > b.Raw ? a : b;
        public static Fix64 Clamp(Fix64 v, Fix64 min, Fix64 max) => v.Raw < min.Raw ? min : v.Raw > max.Raw ? max : v;
        public static Fix64 Clamp01(Fix64 v) => Clamp(v, Fix64.Zero, Fix64.One);
        public static Fix64 Lerp(Fix64 a, Fix64 b, Fix64 t) => a + (b - a) * t;

        /// <summary>Integer square root of a fixed value, rounded down. Throws on negative input.</summary>
        public static Fix64 Sqrt(Fix64 x)
        {
            long xl = x.Raw;
            if (xl < 0) throw new ArgumentOutOfRangeException(nameof(x), "Sqrt of a negative Fix64");
            if (xl == 0) return Fix64.Zero;

            ulong num = (ulong)xl;
            ulong result = 0UL;
            // Highest power of 4 <= num.
            ulong bit = 1UL << 62;
            while (bit > num) bit >>= 2;

            // Two passes: integer part on the raw value, then the fractional part by
            // shifting the remainder up 32 bits (16 bits twice to avoid overflow).
            for (int pass = 0; pass < 2; pass++)
            {
                while (bit != 0)
                {
                    if (num >= result + bit)
                    {
                        num -= result + bit;
                        result = (result >> 1) + bit;
                    }
                    else
                    {
                        result >>= 1;
                    }
                    bit >>= 2;
                }

                if (pass == 0)
                {
                    if (num > (1UL << 32) - 1)
                    {
                        // Remainder would overflow when shifted; handle the carry manually.
                        num -= result;
                        num = (num << 32) - 0x80000000UL;
                        result = (result << 32) + 0x80000000UL;
                    }
                    else
                    {
                        num <<= 32;
                        result <<= 32;
                    }
                    bit = 1UL << 30;
                }
            }

            // Round to nearest.
            if (num > result) ++result;
            return Fix64.FromRaw((long)result);
        }

        /// <summary>Fixed-point squared distance helper used by range checks (avoids Sqrt).</summary>
        public static bool WithinDistance(FixVec2 a, FixVec2 b, Fix64 distance)
        {
            FixVec2 d = a - b;
            return d.LengthSq <= distance * distance;
        }
    }
}
