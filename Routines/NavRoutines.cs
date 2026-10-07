namespace XiHeadless.Routines;

/// The ONE walk-to-a-point-and-wait helper. Nine routines carried their own copy of the same
/// MoveTo + poll-IsMoving + Stop loop (divergent timeouts/tolerances/retry counts grown independently).
/// Destinations are still chosen by callers (per the movement rule); this only owns the wait/retry shape.
public static class NavRoutines
{
    /// Walk to (x, y?, z) via INavigation and wait for arrival. Retries up to `legs` times (mesh stalls
    /// mid-path — the old single-leg quest walk stopped 150y short and talked to a goblin). Aborts on
    /// death (never walk a corpse to the destination) or cancellation. True = within `within` yards.
    public static async Task<bool> WalkTo(INavigation nav, IPerception p, float x, float z, float within,
        CancellationToken ct, float? y = null, int legs = 1, int legTimeoutMs = 90_000,
        Func<CancellationToken, Task<bool>>? defend = null)
    {
        // `defend` (optional): polled while walking; returns true after it fought off an attacker, and the walk
        // resumes. A zone change (death warp, zone line) ends the walk: the target is this zone's coordinates.
        ushort zone = p.World.ZoneId;
        for (int leg = 0; leg < legs && p.DistanceTo(x, z) > within && !ct.IsCancellationRequested
                          && p.World.ZoneId == zone; leg++)
        {
            if (y is { } y3) nav.MoveTo(x, y3, z); else nav.MoveTo(x, z);
            for (int t = 0; t < legTimeoutMs && p.DistanceTo(x, z) > within && nav.IsMoving
                 && !ct.IsCancellationRequested && !(p.World.MaxHp > 0 && p.World.Hpp == 0) && p.World.ZoneId == zone; t += 200)
            {
                await Task.Delay(200, ct);
                if (defend is not null && await defend(ct))
                {
                    if (p.World.Hpp == 0 || p.World.ZoneId != zone) break;
                    if (y is { } y4) nav.MoveTo(x, y4, z); else nav.MoveTo(x, z);   // resume the leg
                }
            }
            nav.Stop();
            if (leg + 1 < legs) await Task.Delay(500, ct);   // settle before the retry leg
        }
        return p.DistanceTo(x, z) <= within;
    }
}
