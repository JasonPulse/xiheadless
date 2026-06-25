namespace XiHeadless.Brains;

/// Runs an advanced-job unlock quest as data: an ordered list of steps, each = go to a zone, walk to an
/// NPC's position, talk, and answer with an option. The SERVER tracks quest state + key items; the bot
/// just visits the right NPCs in the right order (no client-side quest state needed). Behavior is CODE:
/// the target job is a const and the quest tables live in QuestDefs. Reuses IZoning (GoTo) + INavigation
/// + IPerception (find the NPC) + IQuests (talk/answer) + ILifecycle.
///
/// Hard prerequisites (server-enforced, NOT handled here): the char must be >= ADVANCED_JOB_LEVEL (30),
/// and many jobs gate behind a PRIOR quest chain (PLD needs "A Squire's Test II", etc.). Expansion jobs
/// (BLU/COR/PUP/SAM/SCH) also need expansion-zone access. So this is verifiable only on a leveled,
/// progressed character — it scripts the unlock flow, it doesn't bypass the gates.
public sealed class JobUnlockBrain(IPerception p, INavigation nav, IZoning zoning, IQuests quests, ILifecycle lifecycle) : IBrain
{
    const byte Target = Capabilities.Job.Pld;   // which advanced job to unlock (see QuestDefs)
    const float NpcReach = 6f;                  // how close the nearest non-mob entity must be to count as "the NPC"

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(4000, ct);
        if (!QuestDefs.Unlock.TryGetValue(Target, out var steps))
        {
            Console.WriteLine($"[jobunlock] no quest data for job {Target}");
            lifecycle.Logout();
            return;
        }
        Console.WriteLine($"[jobunlock] job {Target}: {steps.Length} step(s); char job={p.World.MainJob} lvl={p.World.MainJobLevel}");

        foreach (var (i, step) in steps.Select((s, i) => (i, s)))
        {
            if (ct.IsCancellationRequested) break;
            Console.WriteLine($"[jobunlock] step {i + 1}/{steps.Length}: {step.Label} ({step.Zone} @ {step.X:F0},{step.Y:F0},{step.Z:F0} opt={step.Option})");

            if (!Game.Zonelines.Resolve(step.Zone).HasValue) { Console.WriteLine($"[jobunlock] unknown zone '{step.Zone}' — stop"); break; }
            if (zoning.CurrentZone != Game.Zonelines.Resolve(step.Zone)!.Value)
            {
                if (!await zoning.GoTo(step.Zone, ct)) { Console.WriteLine($"[jobunlock] couldn't reach {step.Zone} — stop"); break; }
            }

            // Walk to the NPC's position, then talk to the nearest non-mob entity there.
            nav.MoveTo(step.X, step.Y, step.Z);
            for (int t = 0; t < 60000 && nav.IsMoving && !ct.IsCancellationRequested; t += 100) await Task.Delay(100, ct);
            nav.Stop();

            var npc = p.Nearest(e => !e.IsMob && p.DistanceTo(e.X, e.Z) <= NpcReach);
            if (npc is null) { Console.WriteLine("[jobunlock] no NPC in reach — stop"); break; }
            if (!await quests.TalkTo(npc.Id, step.Option, ct)) Console.WriteLine($"[jobunlock] '{step.Label}' opened no event (wrong quest state / level / prereq?)");
            await Task.Delay(1500, ct);
        }

        Console.WriteLine($"[jobunlock] done — job {Target} {(IsUnlocked() ? "UNLOCKED" : "not unlocked (check prereqs/level)")}");
        lifecycle.Logout();
    }

    // We can't read jobs.unlocked directly, but after a successful unlock the char can change to it; the
    // operator confirms via a follow-up JobChange. Here we just report completion of the step sequence.
    bool IsUnlocked() => false;
}
