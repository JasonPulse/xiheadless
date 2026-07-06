namespace XiHeadless.Brains;

/// ONE-SHOT TEST (user, 2026-07-04): at WAR 30, ask the Mhaura Explorer Moogle for a job change to PALADIN
/// — an advanced job that on retail is locked behind its unlock quest. We already know the server accepts a
/// raw SUBJOB packet without the subjob quest (the WAR's displayed sub is that artifact); this probes whether
/// ADVANCED JOBS are also packet-accepted or genuinely quest-gated. Result is read from 0x1B (job info):
/// main flips to PLD = accepted; timeout/no flip = server-side quest gate. Restores WAR either way, then
/// logs out (the babysitter relaunches the normal farm brain afterward).
public sealed class PldTestBrain(IPerception p, IZoning zoning, IJobChange jobs, ILifecycle lifecycle) : IBrain
{
    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(4000, ct);
        Log.Info($"[pldtest] start: char='{p.World.MyName}' job={p.World.MainJob}/{p.World.SubJob} lvl={p.World.MainJobLevel}");

        // v2 (07-05): the Mhaura street-moogle attempt was a WRONG-CHANNEL refusal — this server applies
        // job changes only INSIDE the Mog House (proven by the WHM/BLM set). Route home and enter the MH.
        if (zoning.CurrentZone != 241)
        {
            Log.Info("[pldtest] routing to Windurst Woods");
            if (!await zoning.GoTo("Windurst_Woods", ct)) { Log.Info("[pldtest] FAILED to reach Windurst — aborting"); lifecycle.Logout(); return; }
        }

        Log.Info("[pldtest] requesting main job = PLD (advanced job, unlock quest NOT done)");
        bool accepted = await jobs.ChangeJob(Job.Pld, 0, ct);
        Log.Info(accepted
            ? $"[pldtest] RESULT: ACCEPTED — server allows advanced jobs by packet (now {p.World.MainJob}/{p.World.SubJob} lvl {p.World.MainJobLevel})"
            : "[pldtest] RESULT: REFUSED — advanced jobs appear quest-gated server-side");

        if (p.World.MainJob != Job.War)
        {
            Log.Info("[pldtest] restoring WAR main");
            await jobs.ChangeJob(Job.War, 0, ct);
        }
        Log.Info($"[pldtest] done: job={p.World.MainJob}/{p.World.SubJob} lvl={p.World.MainJobLevel} — logging out");
        lifecycle.Logout();
    }
}
