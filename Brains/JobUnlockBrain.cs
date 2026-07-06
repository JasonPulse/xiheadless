namespace XiHeadless.Brains;

/// Runs an advanced-job unlock quest from QuestDefs as a sequence of typed steps, via the shared QuestRunner.
/// The SERVER enforces quest state, key items, level, prereqs and fame — this only performs the physical
/// actions in order, so a flow only completes on a suitably progressed char. Target job is a const.
public sealed class JobUnlockBrain(
    IPerception p, INavigation nav, IZoning zoning, IQuests quests, ITradeNpc trade, ICombat combat, IGear gear, ILifecycle lifecycle, IEvents events, IInventory inv) : IBrain
{
    const byte Target = Capabilities.Job.Pld;   // which advanced job to unlock (see QuestDefs)

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(4000, ct);
        if (!QuestDefs.Unlock.TryGetValue(Target, out var steps))
        {
            Log.Info($"[jobunlock] no quest data for job {Target}");
            lifecycle.Logout();
            return;
        }
        // Run the prerequisite quest chain (if any) first — the unlock quest won't start otherwise.
        var prereq = QuestDefs.Prereqs.TryGetValue(Target, out var pre) ? pre : System.Array.Empty<QuestStep>();
        var all = prereq.Concat(steps).ToList();
        Log.Info($"[jobunlock] job {Target}: {prereq.Length} prereq + {steps.Length} unlock step(s); char job={p.World.MainJob} lvl={p.World.MainJobLevel}");

        await new QuestRunner(p, nav, zoning, quests, trade, combat, gear, events, inv).Run(all, "jobunlock", ct);

        Log.Info($"[jobunlock] done with job {Target} flow — confirm via a JobChange (server gates unlock)");
        lifecycle.Logout();
    }
}
