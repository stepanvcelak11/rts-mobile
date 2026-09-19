using System;

namespace RTS.Sim.Core
{
    /// <summary>
    /// Deterministic Q32.32 fixed-point number stored in a <see cref="long"/>.
    /// Range ±2^31 (~2.1 billion), resolution 2^-32 (~2.3e-10).
    /// Every gameplay quantity (positions, speeds, hp, timers) lives in this type so that
    /// all peers in a lockstep match compute bit-identical results. No float/double ever
    /// enters the simulation; the conversions at the bottom exist for presentation only.
    /// </summary>
    public readonly struct Fix64 : IEquatable<Fix64>, IComparable<Fix64>
    {
        public const int FractionalBits = 32;
        public const long OneRaw = 1L << FractionalBits;
        private const long HalfRaw = OneRaw >> 1;
        private const ulong FractionMask = 0xFFFFFFFFUL;

        public readonly long Raw;

        public static readonly Fix64 Zero = new Fix64(0);
        public static readonly Fix64 One = new Fix64(OneRaw);
        public static readonly Fix64 Half = new Fix64(HalfRaw);
        public static readonly Fix64 Two = new Fix64(OneRaw * 2);
        public static readonly Fix64 MaxValue = new Fix64(long.MaxValue);
        public static readonly Fix64 MinValue = new Fix64(long.MinValue);
        /// <summary>Smallest positive value, useful as an epsilon.</summary>
        public static readonly Fix64 Precision = new Fix64(1);

        private Fix64(long raw) { Raw = raw; }

        public static Fix64 FromRaw(long raw) => new Fix64(raw);
        public static Fix64 FromInt(int value) => new Fix64((long)value << FractionalBits);
        public static Fix64 FromInt(long value) => new Fix64(value << FractionalBits);

        /// <summary>
        /// Exact conversion from a decimal literal (used once at data-load time). Rounds
        /// half away from zero to the nearest representable value.
        /// </summary>
        public static Fix64 FromDecimal(decimal value)
        {
            decimal scaled = value * OneRaw;
            return new Fix64((long)decimal.Round(scaled, MidpointRounding.AwayFromZero));
        }

        /// <summary>Parses "12", "-3.25", "0.5" etc. without touching floating point.</summary>
        public static Fix64 Parse(string text) =>
            FromDecimal(decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture));

        /// <summary>value / divisor as an exact rational (e.g. Ratio(1, 3)).</summary>
        public static Fix64 Ratio(long numerator, long denominator) => FromInt(numerator) / FromInt(denominator);

        // ---- arithmetic ---------------------------------------------------------------

        public static Fix64 operator +(Fix64 a, Fix64 b) => new Fix64(a.Raw + b.Raw);
        public static Fix64 operator -(Fix64 a, Fix64 b) => new Fix64(a.Raw - b.Raw);
        public static Fix64 operator -(Fix64 a) => new Fix64(-a.Raw);

        public static Fix64 operator *(Fix64 a, Fix64 b)
        {
            // 64x64 -> 128-bit multiply done in four 32-bit partial products, then shifted
            // back by 32. Wrap-around on overflow (same on every platform).
            long xl = a.Raw, yl = b.Raw;
            ulong xlo = (ulong)(xl & (long)FractionMask);
            long xhi = xl >> FractionalBits;
            ulong ylo = (ulong)(yl & (long)FractionMask);
            long yhi = yl >> FractionalBits;

            ulong lolo = xlo * ylo;
            long lohi = (long)xlo * yhi;
            long hilo = xhi * (long)ylo;
            long hihi = xhi * yhi;

            ulong loResult = lolo >> FractionalBits;
            long hiResult = hihi << FractionalBits;
            return new Fix64((long)loResult + lohi + hilo + hiResult);
        }

        public static Fix64 operator *(Fix64 a, int b) => new Fix64(a.Raw * b);
        public static Fix64 operator *(int a, Fix64 b) => new Fix64(b.Raw * a);

        public static Fix64 operator /(Fix64 a, Fix64 b)
        {
            long xl = a.Raw, yl = b.Raw;
            if (yl == 0) throw new DivideByZeroException("Fix64 division by zero");

            ulong remainder = (ulong)(xl >= 0 ? xl : -xl);
            ulong divider = (ulong)(yl >= 0 ? yl : -yl);
            ulong quotient = 0UL;
            int bitPos = FractionalBits + 1;

            // Fast path: divider with trailing zeros.
            while ((divider & 0xF) == 0 && bitPos >= 4)
            {
                divider >>= 4;
                bitPos -= 4;
            }

            while (remainder != 0 && bitPos >= 0)
            {
                int shift = CountLeadingZeros(remainder);
                if (shift > bitPos) shift = bitPos;
                remainder <<= shift;
                bitPos -= shift;

                ulong div = remainder / divider;
                remainder %= divider;
                quotient += div << bitPos;

                remainder <<= 1;
                --bitPos;
            }

            // Round half up.
            ++quotient;
            long result = (long)(quotient >> 1);
            if (((xl ^ yl) & long.MinValue) != 0) result = -result;
            return new Fix64(result);
        }

        public static Fix64 operator /(Fix64 a, int b) => new Fix64(a.Raw / b);
        public static Fix64 operator %(Fix64 a, Fix64 b) => new Fix64(a.Raw % b.Raw);

        public static Fix64 operator <<(Fix64 a, int shift) => new Fix64(a.Raw << shift);
        public static Fix64 operator >>(Fix64 a, int shift) => new Fix64(a.Raw >> shift);

        private static int CountLeadingZeros(ulong x)
        {
            int result = 0;
            while ((x & 0xF000000000000000UL) == 0) { result += 4; x <<= 4; }
            while ((x & 0x8000000000000000UL) == 0) { result += 1; x <<= 1; }
            return result;
        }

        // ---- comparison ---------------------------------------------------------------

        public static bool operator ==(Fix64 a, Fix64 b) => a.Raw == b.Raw;
        public static bool operator !=(Fix64 a, Fix64 b) => a.Raw != b.Raw;
        public static bool operator <(Fix64 a, Fix64 b) => a.Raw < b.Raw;
        public static bool operator >(Fix64 a, Fix64 b) => a.Raw > b.Raw;
        public static bool operator <=(Fix64 a, Fix64 b) => a.Raw <= b.Raw;
        public static bool operator >=(Fix64 a, Fix64 b) => a.Raw >= b.Raw;

        public bool Equals(Fix64 other) => Raw == other.Raw;
        public override bool Equals(object obj) => obj is Fix64 f && f.Raw == Raw;
        public override int GetHashCode() => Raw.GetHashCode();
        public int CompareTo(Fix64 other) => Raw.CompareTo(other.Raw);

        // ---- rounding / access --------------------------------------------------------

        public bool IsZero => Raw == 0;
        public int Sign => Raw < 0 ? -1 : Raw > 0 ? 1 : 0;

        /// <summary>Integer part, rounded toward negative infinity.</summary>
        public int FloorToInt() => (int)(Raw >> FractionalBits);
        public Fix64 Floor() => new Fix64(Raw & ~(long)FractionMask);
        public Fix64 Ceiling() => (Raw & (long)FractionMask) == 0 ? this : Floor() + One;
        public int CeilToInt() => Ceiling().FloorToInt();
        public int RoundToInt() => (this + Half).FloorToInt();
        public Fix64 Abs() => Raw == long.MinValue ? MaxValue : new Fix64(Raw < 0 ? -Raw : Raw);

        // ---- presentation-only conversions (never call from the simulation) ----------

        public float ToFloat() => (float)Raw / OneRaw;           // presentation-only
        public double ToDouble() => (double)Raw / OneRaw;        // presentation-only
        public decimal ToDecimal() => (decimal)Raw / OneRaw;
        public override string ToString() => ToDecimal().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}
