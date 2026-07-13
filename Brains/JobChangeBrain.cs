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

        // The shared routine owns Moogle access + routing to the nearest Mog House city (this brain
        // used to inline the route/Enter/change/Exit sequence ChangeJobViaMogHouse already does).
        bool ok = await JobRoutines.ChangeJobViaMogHouse(jobs, zoning, TargetMain, TargetSub, "Windurst_Woods", ct);
        Log.Info($"[jobchange] {(ok ? "OK" : "FAILED")} -> now {p.World.MainJob}/{p.World.SubJob}");

        lifecycle.Logout();
    }
}
