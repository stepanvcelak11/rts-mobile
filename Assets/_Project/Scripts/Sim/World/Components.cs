using RTS.Sim.Core;

// All components are plain structs. They hold indices and fixed-point numbers only —
// never references — so the world can be hashed, copied and serialised trivially.
namespace RTS.Sim.Model
{
    public enum EntityKind : byte { Unit = 1, Building = 2, ResourceNode = 3 }

    /// <summary>Every live entity has one of these.</summary>
    public struct Identity : IHashable
    {
        public EntityKind Kind;
        public int DefIndex;     // index into the baked unit/building/node table for Kind
        public int Player;       // SimConstants.NeutralPlayer for nodes

        public void Hash(ref Hasher h) { h.Add((byte)Kind); h.Add(DefIndex); h.Add(Player); }
    }

    /// <summary>World position and collision radius of a mobile entity.</summary>
    public struct Position : IHashable
    {
        public FixVec2 Value;
        public Fix64 Radius;
        public FixVec2 Facing;   // unit vector the entity is looking along

        public void Hash(ref Hasher h) { h.Add(Value); h.Add(Radius); h.Add(Facing); }
    }

    /// <summary>Grid rectangle occupied by a building or resource node (cells).</summary>
    public struct Footprint : IHashable
    {
        public int X, Y, W, H;

        public bool Contains(int cx, int cy) => cx >= X && cy >= Y && cx < X + W && cy < Y + H;
        public FixVec2 Center => new FixVec2(Fix64.FromInt(X) + Fix64.FromInt(W) / 2, Fix64.FromInt(Y) + Fix64.FromInt(H) / 2);

        /// <summary>Squared distance from a point to the rectangle (0 when inside).</summary>
        public Fix64 DistanceSqTo(FixVec2 p)
        {
            Fix64 minX = Fix64.FromInt(X), maxX = Fix64.FromInt(X + W);
            Fix64 minY = Fix64.FromInt(Y), maxY = Fix64.FromInt(Y + H);
            Fix64 dx = p.X < minX ? minX - p.X : p.X > maxX ? p.X - maxX : Fix64.Zero;
            Fix64 dy = p.Y < minY ? minY - p.Y : p.Y > maxY ? p.Y - maxY : Fix64.Zero;
            return dx * dx + dy * dy;
        }

        public void Hash(ref Hasher h) { h.Add(X); h.Add(Y); h.Add(W); h.Add(H); }
    }

    public struct Health : IHashable
    {
        public Fix64 Hp;
        public Fix64 MaxHp;

        public void Hash(ref Hasher h) { h.Add(Hp); h.Add(MaxHp); }
    }

    /// <summary>Straight-line steering target. Phase 3 replaces the target with a flow-field lookup.</summary>
    public struct Mover : IHashable
    {
        public Fix64 Speed;          // cells per second
        public FixVec2 Target;
        public bool Moving;

        public void Hash(ref Hasher h) { h.Add(Speed); h.Add(Target); h.Add(Moving); }
    }

    public enum UnitState : byte
    {
        Idle = 0,
        Move = 1,
        Gather = 2,       // walking to / working at a resource node
        ReturnCargo = 3,  // walking to a drop-off
        Build = 4,        // walking to / working at a construction site
        Attack = 5,       // phase 3
        Dead = 6,
    }

    /// <summary>Finite-state-machine state of a unit. Allocation-free; transitions live in BehaviorSystem.</summary>
    public struct UnitBehaviour : IHashable
    {
        public UnitState State;
        public int TargetEntity;     // node / construction site / enemy
        public FixVec2 TargetPos;    // for Move
        public int LastNode;         // node to return to after depositing cargo
        public int Timer;            // generic countdown in ticks

        public void Hash(ref Hasher h) { h.Add((byte)State); h.Add(TargetEntity); h.Add(TargetPos); h.Add(LastNode); h.Add(Timer); }
    }

    /// <summary>Cargo carried by a gatherer.</summary>
    public struct Cargo : IHashable
    {
        public int Resource;         // resource index, -1 when empty
        public Fix64 Amount;
        public Fix64 Capacity;
        public Fix64 Accumulator;    // sub-unit gathering progress

        public bool IsFull => Amount >= Capacity;
        public bool IsEmpty => Amount.IsZero;

        public void Hash(ref Hasher h) { h.Add(Resource); h.Add(Amount); h.Add(Capacity); h.Add(Accumulator); }
    }

    /// <summary>A gatherable node (tree, mine, berries…).</summary>
    public struct ResourceNode : IHashable
    {
        public int Resource;         // resource index
        public Fix64 Amount;         // remaining; negative = infinite
        public Fix64 RatePerTick;    // units gathered per tick by one villager
        public bool Depletes;

        public bool IsDepleted => Depletes && Amount <= Fix64.Zero;

        public void Hash(ref Hasher h) { h.Add(Resource); h.Add(Amount); h.Add(RatePerTick); h.Add(Depletes); }
    }

    /// <summary>A building that is still being built.</summary>
    public struct Construction : IHashable
    {
        public Fix64 Progress;       // 0..1
        public int TotalTicks;       // build time with a single builder
        public int BuildersThisTick; // recomputed every tick by BehaviorSystem

        public void Hash(ref Hasher h) { h.Add(Progress); h.Add(TotalTicks); h.Add(BuildersThisTick); }
    }

    /// <summary>Fixed-capacity training queue (5 slots) — no heap allocation per building.</summary>
    public struct ProductionQueue : IHashable
    {
        public const int Slots = 5;

        public int Count;
        public int D0, D1, D2, D3, D4;   // unit def indices, head first
        public int HeadRemaining;        // ticks left on the head item

        public int Get(int i)
        {
            switch (i) { case 0: return D0; case 1: return D1; case 2: return D2; case 3: return D3; default: return D4; }
        }

        private void SetSlot(int i, int v)
        {
            switch (i) { case 0: D0 = v; break; case 1: D1 = v; break; case 2: D2 = v; break; case 3: D3 = v; break; default: D4 = v; break; }
        }

        public bool TryEnqueue(int defIndex, int ticks)
        {
            if (Count >= Slots) return false;
            SetSlot(Count, defIndex);
            if (Count == 0) HeadRemaining = ticks;
            Count++;
            return true;
        }

        /// <summary>Removes the head and shifts the rest up. Returns the removed def index.</summary>
        public int Dequeue(int nextTicks)
        {
            int head = D0;
            for (int i = 0; i < Count - 1; i++) SetSlot(i, Get(i + 1));
            Count--;
            SetSlot(Count, 0);
            HeadRemaining = Count > 0 ? nextTicks : 0;
            return head;
        }

        public void Hash(ref Hasher h)
        {
            h.Add(Count); h.Add(D0); h.Add(D1); h.Add(D2); h.Add(D3); h.Add(D4); h.Add(HeadRemaining);
        }
    }
}
