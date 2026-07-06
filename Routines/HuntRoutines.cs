namespace XiHeadless.Routines;

/// Reusable solo-hunting movement + zone progression. The brain owns target selection and combat; the
/// Hunter decides WHERE to hunt: it follows the nation's HuntZones path (advancing as the character levels),
/// roams DEEPER within a zone (travels in a committed heading instead of circling one camp), and — when the
/// brain reports the local mobs have become too weak — pushes one zone further along the path. State (current
/// path leg, roam heading) lives here so the brain's loop stays clean. Shared by any solo melee brain.
public sealed class Hunter(INavigation nav, IPerception p, Nation nation)
{
    int _leg = -1;          // furthest path leg reached (monotonic — never backtracks)

    HuntLeg[] Path => HuntZones.Paths[nation];

    int LegForLevel(int lvl)
    {
        var path = Path; int idx = 0;
        for (int i = 0; i < path.Length; i++) if (path[i].Min <= lvl) idx = i;
        return idx;
    }

    /// The zone id to hunt in right now: the furthest leg reached, never below the level-appropriate leg
    /// (so the bot advances as it levels but never wanders back to a too-low zone).
    public ushort TargetZone()
    {
        int byLevel = LegForLevel(p.World.MainJobLevel);
        if (byLevel > _leg || _leg < 0) _leg = byLevel;
        return Zonelines.Resolve(Path[_leg].Zone) ?? (ushort)0;
    }

    public string TargetZoneName() => Path[System.Math.Max(_leg, 0)].Zone;

    /// Push one zone further along the path. Call when the brain finds the local mobs have gone too weak
    /// (the character out-levelled this zone before the next band's level). Capped at the path's end;
    /// returns true if it actually advanced.
    public bool ForceAdvance()
    {
        // Only advance if we actually meet the next leg's Min level. Out-levelling a zone (mobs gone too weak)
        // does NOT mean we can survive the next one: a solo melee that out-grows Tahrongi at ~17 still can't
        // handle Buburimu's aggressive lv17-24 mobs and just dies on arrival. Hold in the current zone until
        // the level (or a party) makes the next zone viable.
        if (_leg + 1 < Path.Length && Path[_leg + 1].Min <= p.World.MainJobLevel) { _leg++; return true; }
        return false;
    }

    HuntLeg CurrentLeg => Path[System.Math.Max(_leg, 0)];

    /// The current leg's dense-spawn camp anchor, or null (then roam wide).
    (float x, float y, float z)? Camp()
    {
        var leg = CurrentLeg;
        return leg.CampX == 0 && leg.CampZ == 0 ? null : (leg.CampX, leg.CampY, leg.CampZ);
    }

    /// Walk to this leg's camp anchor and stop near it. Call on zone ARRIVAL: the zone-line often drops the
    /// bot on a ledge ~300y from the spawns where it can con mobs but never close to melee — so go to the
    /// dense ground-level cluster first. No-op if the leg has no camp or we're already there.
    public async Task GoToCamp(CancellationToken ct)
    {
        if (Camp() is not { } c || p.DistanceTo(c.x, c.z) <= 15f) return;
        nav.MoveTo(c.x, c.y, c.z);
        // Bail if we die en route (e.g. aggro while walking) — otherwise we'd keep walking the corpse the full
        // ~90s to the anchor before the brain's top-of-loop Dead check ever runs, wasting the whole revive.
        for (int t = 0; t < 90000 && p.DistanceTo(c.x, c.z) > 15f && nav.IsMoving && !ct.IsCancellationRequested
             && !(p.World.MaxHp > 0 && p.World.Hpp == 0); t += 200)
            await Task.Delay(200, ct);
        nav.Stop();
    }

    // (In-zone roaming used to live here — camp-anchored sweeps and the Roam.Far committed march. Both are
    // replaced by RoamController: free roam steered by con, no camp anchors, no keep-out geometry. The Hunter
    // now only owns zone-path progression + the one-time arrival walk off the sparse zone-in.)
}
