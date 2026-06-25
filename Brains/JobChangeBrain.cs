namespace XiHeadless.Brains;

/// Smoke-tests the job-change capability: enter the Mog House, change main job, confirm, leave.
/// Behavior is CODE (consts). Fresh chars have WAR/MNK/WHM/BLM/RDM/THF unlocked, so the target must be
/// one of those. Reuses IDelivery (Mog House) + IJobChange + IPerception + ILifecycle.
public sealed class JobChangeBrain(IPerception p, IDelivery delivery, IJobChange jobs, ILifecycle lifecycle) : IBrain
{
    const byte TargetMain = Job.Mnk;   // change main job to MNK
    const byte TargetSub = Job.None;   // leave the support slot unchanged

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(4000, ct);   // let job/zone state stream in
        Console.WriteLine($"[jobchange] char='{p.World.MyName}' job={p.World.MainJob}/{p.World.SubJob} zone={p.World.ZoneId}");

        // Job change must be done inside the Mog House (or a Nomad-Moogle zone). Enter from this city.
        if (!await delivery.EnterMogHouse(ct))
        {
            Console.WriteLine("[jobchange] couldn't enter Mog House — be in a home city first");
            lifecycle.Logout();
            return;
        }

        bool ok = await jobs.ChangeJob(TargetMain, TargetSub, ct);
        Console.WriteLine($"[jobchange] {(ok ? "OK" : "FAILED")} -> now {p.World.MainJob}/{p.World.SubJob}");

        await delivery.ExitMogHouse(ct);
        lifecycle.Logout();
    }
}
