using RTS.Sim.Core;

// All components are plain structs. They hold indices and fixed-point numbers only —
// never references — so the world can be hashed, copied and serialised trivially.
namespace RTS.Sim.Model
{
    public enum EntityKind : byte { Unit = 1, Building = 2, ResourceNode = 3, Projectile = 4 }

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

    /// <summary>Steering state. With a flow field the unit follows the field; without one it walks a straight line.</summary>
    public struct Mover : IHashable
    {
        public Fix64 Speed;          // cells per second
        public FixVec2 Target;       // point goal (or approach point when GoalEntity is set)
        public int GoalEntity;       // building / node whose footprint is the goal, 0 = point goal
        public bool Moving;
        public int StuckTicks;       // consecutive ticks with almost no displacement
        public FixVec2 Velocity;     // last applied displacement (for separation priority + views)

        public void Hash(ref Hasher h) { h.Add(Speed); h.Add(Target); h.Add(GoalEntity); h.Add(Moving); h.Add(StuckTicks); h.Add(Velocity); }
    }

    public enum UnitState : byte
    {
        Idle = 0,
        Move = 1,
        Gather = 2,       // walking to / working at a resource node
        ReturnCargo = 3,  // walking to a drop-off
        Build = 4,        // walking to / working at a construction site
        Attack = 5,       // chasing / hitting TargetEntity
        Dead = 6,
        AttackMove = 7,   // walking to TargetPos, engaging anything met on the way
        Flee = 8,         // villager running to the nearest town center
    }

    /// <summary>Finite-state-machine state of a unit. Allocation-free; transitions live in BehaviorSystem.</summary>
    public struct UnitBehaviour : IHashable
    {
        public UnitState State;
        public int TargetEntity;     // node / construction site / enemy
        public FixVec2 TargetPos;    // for Move
        public int LastNode;         // node to return to after depositing cargo
        public int Timer;            // generic countdown in ticks
        public int Cooldown;         // ticks until the next attack
        public int LastAttacker;     // who hit us last (defensive retaliation, fleeing)
        public FixVec2 LeashOrigin;  // where the unit stood when it started chasing
        public FixVec2 ResumePos;    // attack-move destination to resume after a fight
        public UnitState ResumeState;
        public Stance Stance;        // player override of the definition aggro
        public int Stalled;          // ticks without getting closer to StallTarget (crowd / walled-in node)
        public int StallTarget;      // entity the stall counter refers to
        public Fix64 StallBest;      // closest squared distance reached so far to StallTarget

        public void Hash(ref Hasher h)
        {
            h.Add((byte)State); h.Add(TargetEntity); h.Add(TargetPos); h.Add(LastNode); h.Add(Timer);
            h.Add(Cooldown); h.Add(LastAttacker); h.Add(LeashOrigin); h.Add(ResumePos); h.Add((byte)ResumeState); h.Add((byte)Stance);
            h.Add(Stalled); h.Add(StallTarget); h.Add(StallBest);
        }
    }

    public enum Stance : byte { Default = 0, Passive = 1, Defensive = 2, Aggressive = 3, StandGround = 4 }

    /// <summary>Where units trained at a building walk after spawning.</summary>
    public struct Rally : IHashable
    {
        public FixVec2 Point;

        public void Hash(ref Hasher h) { h.Add(Point); }
    }

    /// <summary>A building that shoots (town center, tower).</summary>
    public struct Turret : IHashable
    {
        public int Target;
        public int Cooldown;

        public void Hash(ref Hasher h) { h.Add(Target); h.Add(Cooldown); }
    }

    /// <summary>An in-flight ranged attack. Homing: it hits its target when TicksLeft reaches 0.</summary>
    public struct Projectile : IHashable
    {
        public int Source;           // entity that fired (may be dead by impact time)
        public int SourcePlayer;
        public EntityKind SourceKind; // Unit or Building
        public int SourceDef;        // unit / building def index
        public int AttackIndex;      // which attack of the source def (multipliers, damage type)
        public int Target;
        public int TicksLeft;
        public int TotalTicks;
        public FixVec2 Start;

        public void Hash(ref Hasher h)
        {
            h.Add(Source); h.Add(SourcePlayer); h.Add((byte)SourceKind); h.Add(SourceDef); h.Add(AttackIndex); h.Add(Target); h.Add(TicksLeft); h.Add(TotalTicks); h.Add(Start);
        }
    }

    /// <summary>A technology being researched at a building (one at a time).</summary>
    public struct Research : IHashable
    {
        public int Tech;
        public int Remaining;

        public void Hash(ref Hasher h) { h.Add(Tech); h.Add(Remaining); }
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
        public int Def;              // node def index (rates, gather multipliers)
        public int Resource;         // resource index
        public Fix64 Amount;         // remaining; negative = infinite
        public Fix64 RatePerTick;    // units gathered per tick by one villager
        public bool Depletes;

        public bool IsDepleted => Depletes && Amount <= Fix64.Zero;

        public void Hash(ref Hasher h) { h.Add(Def); h.Add(Resource); h.Add(Amount); h.Add(RatePerTick); h.Add(Depletes); }
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

        /// <summary>Removes the last queued item (cancel). Returns its def index.</summary>
        public int RemoveLast()
        {
            int last = Get(Count - 1);
            Count--;
            SetSlot(Count, 0);
            if (Count == 0) HeadRemaining = 0;
            return last;
        }

        public void Hash(ref Hasher h)
        {
            h.Add(Count); h.Add(D0); h.Add(D1); h.Add(D2); h.Add(D3); h.Add(D4); h.Add(HeadRemaining);
        }
    }
}
