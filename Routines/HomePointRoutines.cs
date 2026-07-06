namespace XiHeadless.Routines;

/// Set the home point at a city's Home Point crystal (examine + blind EVENTEND option 1 = SET_HOMEPOINT).
/// Extracted from HomePointBrain so life-goal brains can relocate a char's revive city — a bot whose home
/// point sits across a hostile zone from its grind ground dies in a loop (the lvl-4 BLM re-crossed
/// Buburimu/Tahrongi after EVERY death because it still revived in Mhaura).
public static class HomePointRoutines
{
    // Starting-city zone -> Home Point crystal (npc id + position + event csid; per-crystal in the zone lua).
    public static readonly Dictionary<ushort, (uint npcId, float x, float y, float z, ushort csid)> Crystal = new()
    {
        [241] = (17764538u, -92f,  -5f,   62f,  8702), // Windurst Woods, HomePoint#3
        [240] = (17760398u, 180f, -12f,  226f,  8702), // Port Windurst, HomePoint#3
        [232] = (17727576u, -6f,  -13f, -150f,  8702), // Port San d'Oria, HomePoint#3
        [230] = (17719431u, -85.5f, 1f, -64.5f, 8700), // Southern San d'Oria, HomePoint#1 (near the PLD/DRG/etc. unlock NPCs)
        [245] = (17780873u, -98.588f, 0f, -183.416f, 8700), // Lower Jeuno, HomePoint#1 — THE central hub (lv30 chars home-point here; easy reach to all 3 nations)
        [236] = (17743946u, -127f, -6f,   10f,  8702), // Port Bastok, HomePoint#3
        [249] = (17797161u, -12.75f, -15.79f, 87.29f, 8700), // Mhaura, HomePoint#1
    };

    /// Set the home point at the CURRENT zone's mapped crystal. False if this zone has none mapped.
    /// Idempotent and cheap (~30s) — safe to call on every brain start when in the target city.
    public static async Task<bool> SetHere(IPerception p, INavigation nav, IEvents events, ICombat combat,
                                           ushort zone, CancellationToken ct)
    {
        if (!Crystal.TryGetValue(zone, out var pos)) { Console.WriteLine($"[hp] no crystal mapped for zone {zone}"); return false; }

        // Clear ANY blocking zone-in event first — this is the difference that made the original Mhaura set
        // work while the Windurst set failed: if some other event is the char's currentEvent, the crystal
        // Talk can't become current, so the EVENTEND routes to the ZONE's onEventFinish (which ignores the
        // crystal csid) and setHomePoint() never runs. Sweep the known blockers (ROV 30035, new-char CS,
        // moghouse) so the crystal event is the one that's active when we EVENTEND it.
        foreach (var blocker in new ushort[] { 30035, 368, 367, 305, 531, 0, 1, 535, 503, 500 })
        {
            await events.Finish(p.World.MyId, 0, blocker, 0, ct);
            await Task.Delay(300, ct);
        }
        await Task.Delay(1200, ct);

        Console.WriteLine($"[hp] walking to the Home Point crystal in zone {zone} ({pos.x:F0},{pos.z:F0})");
        // ENFORCE arrival: the crystal's onTrigger has a ~6y server-side range check, and a short-stopped
        // city walk makes the whole set silently no-op (the BLM's first Windurst set never took — it still
        // revived in Mhaura). Retry the approach; refuse to attempt from out of range.
        for (int leg = 0; leg < 3 && p.DistanceTo(pos.x, pos.z) > 4f && !ct.IsCancellationRequested; leg++)
        {
            nav.MoveTo(pos.x, pos.y, pos.z);
            for (int t = 0; t < 150000 && p.DistanceTo(pos.x, pos.z) > 3f && nav.IsMoving && !ct.IsCancellationRequested; t += 200) await Task.Delay(200, ct);
            nav.Stop();
            await Task.Delay(500, ct);
        }
        float dist = p.DistanceTo(pos.x, pos.z);
        Console.WriteLine($"[hp] at ({p.World.X:F0},{p.World.Z:F0}) — {dist:F1}y from the crystal");
        if (dist > 6f) { Console.WriteLine("[hp] too far from the crystal — NOT attempting (the set would silently no-op)"); return false; }

        // setHomePoint() only runs while ALIVE — revive/heal first if we arrived KO'd.
        if (combat.Dead) { Console.WriteLine("[hp] arrived KO'd — reviving before the set"); await combat.Rest(95, 0, null, ct); }
        ushort idx = p.World.TargidOf(pos.npcId);   // shared resolver (tracked-entity index, else id & 0xFFF)
        for (int attempt = 1; attempt <= 3 && !ct.IsCancellationRequested; attempt++)
        {
            await events.Examine(pos.npcId, ct);                  // Talk -> crystal event server-side
            await events.Finish(pos.npcId, idx, pos.csid, 1, ct); // EVENTEND option 1 = SET_HOMEPOINT
            await Task.Delay(1500, ct);
        }
        Console.WriteLine($"[hp] home point set SENT at zone {zone} from {dist:F1}y (true verification = next death revives here)");
        return true;
    }
}
