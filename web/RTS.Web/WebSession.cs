using RTS.Data;
using RTS.Net;
using RTS.Sim.Core;
using RTS.Sim.Model;

namespace RTS.Web;

/// <summary>Hosts one match in the browser: runner, controller, interpolation state and pending HUD toasts.</summary>
public sealed class WebSession : IMatchSession
{
    public readonly MatchRunner Runner;
    public readonly WebController Controller;
    public readonly Dictionary<int, FixVec2> PrevPositions = new();
    private readonly Dictionary<int, FixVec2> _curPositions = new();
    public readonly List<string> Toasts = new();
    public readonly List<int[]> Effects = new();      // (kind, x*64, y*64) raised this frame
    public FixVec2? AttackPing;
    public double AttackPingUntil;
    public double Clock;

    public World World => Runner.World;
    public ICommandSource Source => Runner.Source;
    public int LocalPlayer { get; }
    public float Alpha => Runner.Alpha;

    public WebSession(GameData data, WorldConfig config, int localPlayer)
    {
        LocalPlayer = localPlayer;
        Runner = new MatchRunner(data, config, new LocalCommandSource());
        Controller = new WebController(this);
        Runner.TickCompleted += OnTick;
        SnapshotPositions(World);
    }

    private void OnTick(World w)
    {
        Controller.Prune();
        foreach (SimEvent ev in w.Events)
        {
            switch (ev.Kind)
            {
                case SimEventKind.CommandRejected when ev.A == LocalPlayer:
                    Toasts.Add(RejectText((CommandRejectReason)ev.B));
                    break;
                case SimEventKind.AgeAdvanced when ev.A == LocalPlayer:
                    Toasts.Add("Welcome to the " + w.Defs.Ages[ev.B].Def.name + " Age");
                    break;
                case SimEventKind.ResearchFinished when ev.A == LocalPlayer:
                    Toasts.Add(w.Defs.Techs[ev.B].Def.name + " researched");
                    break;
                case SimEventKind.ShipmentArrived when ev.A == LocalPlayer:
                    Toasts.Add("Shipment arrived: " + w.Defs.Techs[ev.B].Def.name);
                    break;
                case SimEventKind.UnderAttack when ev.A == LocalPlayer:
                    AttackPing = w.TargetPoint(ev.Entity);
                    AttackPingUntil = Clock + 6.0;
                    break;
                case SimEventKind.Died:
                    Effects.Add(new[] { 1, (w.TargetPoint(ev.Entity).X * 64).RoundToInt(), (w.TargetPoint(ev.Entity).Y * 64).RoundToInt() });
                    break;
                case SimEventKind.ProjectileHit:
                    if (w.Positions.TryGet(ev.Entity, out Position pp))
                        Effects.Add(new[] { 2, (pp.Value.X * 64).RoundToInt(), (pp.Value.Y * 64).RoundToInt() });
                    break;
                case SimEventKind.ConstructionFinished when w.Identities.TryGet(ev.Entity, out Identity cid) && cid.Player == LocalPlayer:
                    Toasts.Add(w.BuildingDefOf(ev.Entity).Def.name + " completed");
                    break;
            }
        }
        SnapshotPositions(w);
    }

    private void SnapshotPositions(World w)
    {
        // The positions from the end of the previous tick become "previous"; new entities snap.
        for (int i = 0; i < w.Positions.Count; i++)
        {
            int e = w.Positions.EntityAt(i);
            FixVec2 now = w.Positions.At(i).Value;
            PrevPositions[e] = _curPositions.TryGetValue(e, out FixVec2 last) ? last : now;
            _curPositions[e] = now;
        }
        if (_curPositions.Count > w.Positions.Count + 64)
        {
            var dead = new List<int>();
            foreach (int e in _curPositions.Keys) if (!w.Positions.Has(e)) dead.Add(e);
            foreach (int e in dead) { _curPositions.Remove(e); PrevPositions.Remove(e); }
        }
    }

    /// <summary>Position to draw this frame: previous tick blended toward the current one.</summary>
    public FixVec2 DrawPosition(int entity, FixVec2 current, Fix64 alpha)
    {
        if (!PrevPositions.TryGetValue(entity, out FixVec2 prev)) return current;
        return FixVec2.Lerp(prev, current, alpha);
    }

    public static string RejectText(CommandRejectReason r) => r switch
    {
        CommandRejectReason.NotAffordable => "Not enough resources",
        CommandRejectReason.BadPlacement => "Can't build there",
        CommandRejectReason.LimitReached => "Limit reached",
        CommandRejectReason.QueueFull => "Queue is full",
        CommandRejectReason.PopulationCap => "Build more houses",
        CommandRejectReason.WrongAge => "Advance to the next Age first",
        CommandRejectReason.AlreadyResearched => "Already done",
        CommandRejectReason.NotEnoughXp => "Not enough experience",
        CommandRejectReason.Busy => "Already in progress",
        _ => "Can't do that",
    };
}
