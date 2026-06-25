namespace XiHeadless.Brains;

/// Smoke-tests the job-change capability. ChangeJob handles Moogle access itself (Explorer Moogle zone,
/// else Mog House), so the brain just asks for the job. Fresh chars have WAR/MNK/WHM/BLM/RDM/THF
/// unlocked, so the target must be one of those. Reuses IJobChange + IPerception + ILifecycle.
public sealed class JobChangeBrain(IPerception p, IJobChange jobs, ILifecycle lifecycle) : IBrain
{
    const byte TargetMain = Job.War;   // main WAR
    const byte TargetSub = Job.Mnk;    // TEST: try to set MNK as support job (is the subjob quest even gated here?)

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(4000, ct);   // let job/zone state stream in
        Console.WriteLine($"[jobchange] char='{p.World.MyName}' job={p.World.MainJob}/{p.World.SubJob} zone={p.World.ZoneId}");

        bool ok = await jobs.ChangeJob(TargetMain, TargetSub, ct);
        Console.WriteLine($"[jobchange] {(ok ? "OK" : "FAILED")} -> now {p.World.MainJob}/{p.World.SubJob}");

        lifecycle.Logout();
    }
}
