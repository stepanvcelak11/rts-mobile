using System.Collections.Generic;
using System.IO;
using RTS.Sim.Model;

namespace RTS.Sim.Commands
{
    /// <summary>
    /// A player intent applied at the start of a tick. Commands are the *only* input to the
    /// simulation, which makes them the unit of replay and network traffic. They validate
    /// themselves against the world (ownership, affordability) and emit CommandRejected
    /// events instead of throwing, because a malicious or stale peer must never crash a match.
    /// </summary>
    public interface ICommand
    {
        /// <summary>Stable numeric type tag for serialisation.</summary>
        byte TypeId { get; }
        int Player { get; }
        void Execute(World world);
        void Write(BinaryWriter w);
    }

    /// <summary>All commands scheduled for one tick, in deterministic order.</summary>
    public sealed class TickCommands
    {
        public readonly int Tick;
        public readonly List<ICommand> Commands = new List<ICommand>();

        public TickCommands(int tick) { Tick = tick; }

        /// <summary>Orders by player then by insertion so every peer applies them identically.</summary>
        public void SortDeterministic()
        {
            // Stable insertion sort by player (lists are tiny).
            for (int i = 1; i < Commands.Count; i++)
            {
                ICommand c = Commands[i];
                int j = i - 1;
                while (j >= 0 && Commands[j].Player > c.Player) { Commands[j + 1] = Commands[j]; j--; }
                Commands[j + 1] = c;
            }
        }
    }

    internal static class CommandUtil
    {
        public static void Reject(World w, int player, CommandRejectReason reason) =>
            w.Events.Add(new SimEvent(SimEventKind.CommandRejected, 0, player, (int)reason));

        /// <summary>True when the entity is a live unit owned by the player.</summary>
        public static bool OwnsUnit(World w, int player, int entity) =>
            w.Identities.TryGet(entity, out Identity id) && id.Kind == EntityKind.Unit && id.Player == player;

        public static bool OwnsBuilding(World w, int player, int entity) =>
            w.Identities.TryGet(entity, out Identity id) && id.Kind == EntityKind.Building && id.Player == player;

        public static void WriteInts(BinaryWriter w, int[] values)
        {
            w.Write(values.Length);
            for (int i = 0; i < values.Length; i++) w.Write(values[i]);
        }

        public static int[] ReadInts(BinaryReader r)
        {
            int n = r.ReadInt32();
            var arr = new int[n];
            for (int i = 0; i < n; i++) arr[i] = r.ReadInt32();
            return arr;
        }
    }
}
