namespace XiHeadless.Routines;

/// Job changes apply ONLY inside the Mog House on this server (street/Nomad-Moogle 0x100 packets are
/// silently dropped — proven 07-05: WHM/BLM set succeeded in the MH after identical street attempts
/// failed). This helper owns the routing: try in place (JobChange enters the MH itself when the zone has
/// an entrance), and on failure route to the home city and retry once.
public static class JobRoutines
{
    public static async Task<bool> ChangeJobViaMogHouse(
        IJobChange jobs, IZoning zoning, byte main, byte sub, string homeCity, CancellationToken ct)
    {
        if (await jobs.ChangeJob(main, sub, ct)) return true;
        Log.Info($"[job] change failed here (zone {zoning.CurrentZone}) — routing to {homeCity} for the Mog House");
        if (!await zoning.GoTo(homeCity, ct)) { Log.Info("[job] couldn't reach the home city"); return false; }
        return await jobs.ChangeJob(main, sub, ct);
    }
}
