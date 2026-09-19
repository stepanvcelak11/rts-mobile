namespace RTS.Sim.Core
{
    /// <summary>
    /// xorshift128 PRNG. Part of the world state (hashed), so every peer draws the same
    /// sequence. Never use System.Random inside the simulation.
    /// </summary>
    public sealed class DetRandom
    {
        private uint _x, _y, _z, _w;

        public DetRandom(uint seed)
        {
            Reseed(seed);
        }

        public void Reseed(uint seed)
        {
            // SplitMix-style scramble so seeds 0,1,2… do not produce correlated streams.
            _x = seed ^ 0x9E3779B9u;
            _y = Scramble(_x);
            _z = Scramble(_y);
            _w = Scramble(_z);
            if ((_x | _y | _z | _w) == 0) _w = 1;
        }

        private static uint Scramble(uint v)
        {
            v ^= v >> 16; v *= 0x7FEB352Du;
            v ^= v >> 15; v *= 0x846CA68Bu;
            v ^= v >> 16;
            return v;
        }

        public uint NextUInt()
        {
            uint t = _x ^ (_x << 11);
            _x = _y; _y = _z; _z = _w;
            _w = _w ^ (_w >> 19) ^ (t ^ (t >> 8));
            return _w;
        }

        /// <summary>Uniform integer in [minInclusive, maxExclusive).</summary>
        public int Range(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            uint span = (uint)(maxExclusive - minInclusive);
            return minInclusive + (int)(NextUInt() % span);
        }

        /// <summary>Uniform fixed value in [0, 1).</summary>
        public Fix64 NextFix01() => Fix64.FromRaw((long)NextUInt());

        public bool Chance(Fix64 probability) => NextFix01() < probability;

        public void Hash(ref Hasher h)
        {
            h.Add(_x); h.Add(_y); h.Add(_z); h.Add(_w);
        }
    }
}
