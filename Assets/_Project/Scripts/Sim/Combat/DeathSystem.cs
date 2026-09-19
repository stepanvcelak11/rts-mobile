using RTS.Sim.Model;

namespace RTS.Sim.Systems
{
    /// <summary>Removes units that died this tick. Buildings are despawned by the kill itself.</summary>
    public sealed class DeathSystem : ISystem
    {
        public void Step(World w)
        {
            for (int i = 0; i < w.Behaviours.Count; i++)
            {
                ref UnitBehaviour b = ref w.Behaviours.At(i);
                if (b.State != UnitState.Dead) continue;
                w.Despawn(w.Behaviours.EntityAt(i));
            }
        }
    }
}
