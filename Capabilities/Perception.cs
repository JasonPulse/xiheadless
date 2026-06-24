namespace XiHeadless.Capabilities;

/// Read-only view of the world for decision-making.
public interface IPerception
{
    WorldState World { get; }
    Entity? Nearest(Func<Entity, bool> match);
    float DistanceTo(float x, float z);
}

public sealed class Perception(WorldState world) : IPerception
{
    public WorldState World => world;
    public float DistanceTo(float x, float z) { float dx = world.X - x, dz = world.Z - z; return MathF.Sqrt(dx * dx + dz * dz); }
    public Entity? Nearest(Func<Entity, bool> match)
    {
        Entity? best = null; float bestD = float.MaxValue;
        foreach (var e in world.Entities.Values)
        {
            if (e.Id == world.MyId || !match(e)) continue;
            float d = DistanceTo(e.X, e.Z);
            if (d < bestD) { bestD = d; best = e; }
        }
        return best;
    }
}
