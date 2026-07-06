namespace XiHeadless.Routines;

/// Job changes apply ONLY inside the Mog House on this server (street/Nomad-Moogle 0x100 packets are
/// silently dropped — proven 07-05: WHM/BLM set succeeded in the MH after identical street attempts
/// failed). This helper owns the routing: try in place (JobChange enters the MH itself when the zone has
/// an entrance), and on failure route to the NEAREST Mog House city (ANY city has one — never a hardcoded
/// far home city, which is what sent the San d'Oria-quest PLD trekking cross-continent to Windurst).
public static class JobRoutines
{
    // Every city with a Mog House (job change works at ANY of them): the 3 nations' sub-zones + Jeuno. A char
    // changes jobs at the CLOSEST one, not a fixed home city. (Explorer/Nomad-Moogle-only zones are excluded —
    // their street change packets are dropped on this server.)
    static readonly ushort[] MogHouseCities =
        { 230, 231, 232,   234, 235, 236,   238, 239, 240, 241,   243, 244, 245, 246 };

    public static async Task<bool> ChangeJobViaMogHouse(
        IJobChange jobs, IZoning zoning, byte main, byte sub, string fallbackCity, CancellationToken ct)
    {
        if (await jobs.ChangeJob(main, sub, ct)) return true;
        ushort cur = zoning.CurrentZone;
        // Already IN a Mog House city and it still failed → the job is locked (or a transient) — a trek to any
        // OTHER city can't unlock it, so don't burn a cross-map walk (this is the locked-probe case).
        if (System.Array.IndexOf(MogHouseCities, cur) >= 0)
        { Log.Info($"[job] change failed in Mog House city {cur} (job locked or transient) — not trekking elsewhere"); return false; }

        // Otherwise route to the NEAREST Mog House city (by zone-graph hops), falling back to the caller's hint.
        ushort dest = NearestMogHouseCity(cur);
        if (dest == 0 && Game.Zonelines.Resolve(fallbackCity) is { } h) dest = h;
        if (dest == 0) { Log.Info("[job] no reachable Mog House city"); return false; }
        Log.Info($"[job] change failed here (zone {cur}) — routing to nearest Mog House city {Game.Zonelines.Name(dest)} ({dest})");
        if (!await zoning.GoTo(Game.Zonelines.Name(dest), ct)) { Log.Info("[job] couldn't reach a Mog House city"); return false; }
        return await jobs.ChangeJob(main, sub, ct);
    }

    // Closest Mog House city to `from` by BFS hop count over the zone graph (0 if none reachable).
    static ushort NearestMogHouseCity(ushort from)
    {
        ushort best = 0; int bestHops = int.MaxValue;
        foreach (var c in MogHouseCities)
        {
            if (c == from) return c;
            var route = Game.Zonelines.Route(from, c);
            if (route is null) continue;
            if (route.Count < bestHops) { bestHops = route.Count; best = c; }
        }
        return best;
    }
}
