using RTS.Sim.Model;

namespace RTS.Sim.Systems
{
    /// <summary>One stage of the tick. Systems are stateless; all state lives in the World.</summary>
    public interface ISystem
    {
        void Step(World world);
    }
}
