namespace XiHeadless;

/// Shared 2D geometry. The horizontal (X/Z) distance was re-implemented in RoamController, Navigator,
/// KillRoutine, PartySupport and LevelGrind — one canonical version now, reachable everywhere via the
/// global usings.
public static class Geometry
{
    /// Horizontal (X/Z) distance between two points.
    public static float Dist2D(float ax, float az, float bx, float bz)
    { float dx = ax - bx, dz = az - bz; return MathF.Sqrt(dx * dx + dz * dz); }
}
