namespace XiHeadless.Brains;

/// Smoke-tests the job-change capability. ChangeJob handles Moogle access itself (Explorer Moogle zone,
/// else Mog House), so the brain just asks for the job. Fresh chars have WAR/MNK/WHM/BLM/RDM/THF
/// unlocked, so the target must be one of those. Reuses IJobChange + IPerception + ILifecycle.
public sealed class JobChangeBrain(IPerception p, IJobChange jobs, ILifecycle lifecycle, IZoning zoning, IDelivery delivery) : IBrain
{
    const byte TargetMain = Job.Whm;   // main WHM (the clean char's own job)
    const byte TargetSub = Job.Blm;    // SUCCESS CRITERION (user): the WHM setting a subjob after its quest
                                       // completion proves the whole chain — quest engine + server unlock.

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(4000, ct);   // let job/zone state stream in
        Log.Info($"[jobchange] char='{p.World.MyName}' job={p.World.MainJob}/{p.World.SubJob} zone={p.World.ZoneId}");

        // EMPIRICAL server behavior: street-side moogle-menu job changes are silently dropped (Mhaura
        // attempts failed despite MISC_MOGMENU) — every confirmed change happened INSIDE the Mog House.
        // Route home and enter the MH explicitly before asking.
        if (zoning.CurrentZone != 241)
        {
            Log.Info("[jobchange] routing to Windurst Woods for the Mog House");
            await zoning.GoTo("Windurst_Woods", ct);
        }
        if (!await delivery.EnterMogHouse(ct)) { Log.Info("[jobchange] couldn't enter the Mog House — aborting"); lifecycle.Logout(); return; }

        bool ok = await jobs.ChangeJob(TargetMain, TargetSub, ct);
        await delivery.ExitMogHouse(ct);
        Log.Info($"[jobchange] {(ok ? "OK" : "FAILED")} -> now {p.World.MainJob}/{p.World.SubJob}");

        lifecycle.Logout();
    }
}
