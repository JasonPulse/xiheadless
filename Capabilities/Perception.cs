namespace XiHeadless.Capabilities;

public sealed class Perception(WorldState world) : IPerception
{
    public WorldState World => world;
    public float DistanceTo(float x, float z) { float dx = world.X - x, dz = world.Z - z; return MathF.Sqrt(dx * dx + dz * dz); }
    public float DistanceTo3D(float x, float y, float z) { float dx = world.X - x, dy = world.Y - y, dz = world.Z - z; return MathF.Sqrt(dx * dx + dy * dy + dz * dz); }

    // Snapshot a dictionary the parse thread mutates concurrently. A bare .ToArray() is NOT enough: it sizes
    // the destination from Count and then copies, so an insert in between throws "Destination array is not
    // long enough" (overnight WHM crash). Retry until a consistent copy lands (contention is micro-seconds).
    static T[] Snapshot<T>(ICollection<T> live)
    {
        for (int i = 0; ; i++)
        {
            try { return live.ToArray(); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException && i < 5) { }
        }
    }

    public Entity? Nearest(Func<Entity, bool> match)
    {
        Entity? best = null; float bestD = float.MaxValue;
        foreach (var e in Snapshot(world.Entities.Values))
        {
            // null slots: a dict SHRINK between ToArray's size-read and copy yields trailing nulls with NO
            // exception (the grow case throws and retries; this one is silent) — crashed the WHM brain live.
            if (e is null || e.Id == world.MyId || !match(e)) continue;
            float d = DistanceTo(e.X, e.Z);
            if (d < bestD) { bestD = d; best = e; }
        }
        return best;
    }

    public int AttackersOn(uint id, long withinMs = 6000)
    {
        int n = 0;
        foreach (var (_, (target, ms)) in Snapshot(world.Attackers))
            if (target == id && world.NowMs - ms <= withinMs) n++;
        return n;
    }

    public IReadOnlyList<string> AttackerNames(uint id, long withinMs = 6000)
    {
        var names = new List<string>();
        foreach (var (actor, (target, ms)) in Snapshot(world.Attackers))
            if (target == id && world.NowMs - ms <= withinMs)
                names.Add(world.Entities.TryGetValue(actor, out var e) && e.Name.Length > 0 ? e.Name : $"0x{actor:X}");
        return names;
    }
}
