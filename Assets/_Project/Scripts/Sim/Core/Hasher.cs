namespace RTS.Sim.Core
{
    /// <summary>
    /// FNV-1a 64-bit accumulator. Every hashable piece of world state feeds its fields in
    /// a fixed order; peers compare the resulting <see cref="Value"/> to detect desyncs.
    /// </summary>
    public struct Hasher
    {
        private const ulong Offset = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        public ulong Value;

        public static Hasher Create() => new Hasher { Value = Offset };

        public void Add(byte b)
        {
            Value ^= b;
            Value *= Prime;
        }

        public void Add(int v)
        {
            Add((byte)v); Add((byte)(v >> 8)); Add((byte)(v >> 16)); Add((byte)(v >> 24));
        }

        public void Add(uint v) => Add((int)v);

        public void Add(long v)
        {
            Add((int)v);
            Add((int)(v >> 32));
        }

        public void Add(bool v) => Add((byte)(v ? 1 : 0));
        public void Add(Fix64 v) => Add(v.Raw);
        public void Add(FixVec2 v) { Add(v.X); Add(v.Y); }
    }

    /// <summary>Implemented by every component/state object that contributes to the state hash.</summary>
    public interface IHashable
    {
        void Hash(ref Hasher h);
    }
}
