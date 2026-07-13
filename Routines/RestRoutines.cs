namespace XiHeadless.Routines;

/// Shared rest-safety primitive. Both the solo grind (LevelGrind.RestSafely) and the party healer
/// (PartySupport) must never sit where an aggressive wanderer can catch them — they step DIRECTLY AWAY
/// from the nearest threat first. This is the destination-choosing bit both had copy-pasted (the walk
/// itself still goes through INavigation, per the movement rule).
public static class RestRoutines
{
    /// Choose a point `dist` yards directly away from a threat at (threatX,threatZ) and, if the navmesh can
    /// reach it, move there. Returns true if a reachable step was issued; false if the away-point is off-mesh
    /// (caller decides the fallback — roam elsewhere, or sit in place rather than freeze).
    public static bool StepAway(INavigation nav, IPerception p, float threatX, float threatZ, float dist)
    {
        float dx = p.World.X - threatX, dz = p.World.Z - threatZ;
        float len = MathF.Max(1f, Geometry.Dist2D(p.World.X, p.World.Z, threatX, threatZ));
        float tx = p.World.X + dx / len * dist, tz = p.World.Z + dz / len * dist;
        if (!nav.CanReach(tx, p.World.Y, tz)) return false;
        nav.MoveTo(tx, tz);
        return true;
    }
}
