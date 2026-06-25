namespace XiHeadless.Brains;

/// Runs an advanced-job unlock quest from QuestDefs as a sequence of typed steps (talk, examine, travel,
/// zone-in-from, equip, kill-N-with-weapon). The SERVER enforces quest state, key items, level, prereqs
/// and fame — this brain only performs the physical actions in order, so a flow only completes on a
/// suitably leveled/progressed char (ADVANCED_JOB_LEVEL=30, prerequisite chains, expansion access).
/// Behavior is CODE: the target job is a const. Reuses IZoning/INavigation/IPerception/IQuests +
/// ICombat/IGear (kill objectives) + ILifecycle.
public sealed class JobUnlockBrain(
    IPerception p, INavigation nav, IZoning zoning, IQuests quests, ICombat combat, IGear gear, ILifecycle lifecycle) : IBrain
{
    const byte Target = Capabilities.Job.Pld;   // which advanced job to unlock (see QuestDefs)
    const float NpcReach = 6f;

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

        for (int i = 0; i < steps.Length && !ct.IsCancellationRequested; i++)
        {
            var step = steps[i];
            Console.WriteLine($"[jobunlock] step {i + 1}/{steps.Length} [{step.Kind}]: {step.Label}");
            if (!await Do(step, ct)) { Console.WriteLine("[jobunlock] step failed — stopping"); break; }
            await Task.Delay(1200, ct);
        }

        Console.WriteLine($"[jobunlock] done with job {Target} flow — confirm via a JobChange (server gates unlock)");
        lifecycle.Logout();
    }

    async Task<bool> Do(QuestStep step, CancellationToken ct) => step.Kind switch
    {
        StepKind.Goto       => await EnsureZone(step.Zone, ct),
        StepKind.Talk       => await TalkStep(step, ct),
        StepKind.Examine    => await TalkStep(step, ct),                     // examine = Talk to the nearest entity
        StepKind.ZoneInFrom => await EnsureZone(step.FromZone, ct) && await EnsureZone(step.Zone, ct), // enter Zone from FromZone
        StepKind.Equip      => await gear.EquipItem(step.ItemId, step.Slot, ct),
        StepKind.KillWith   => await KillWith(step.ItemId, step.Count, ct),
        _                   => false,
    };

    async Task<bool> EnsureZone(string zone, CancellationToken ct)
    {
        if (Game.Zonelines.Resolve(zone) is not ushort id) { Console.WriteLine($"[jobunlock] unknown zone '{zone}'"); return false; }
        if (zoning.CurrentZone == id) return true;
        return await zoning.GoTo(zone, ct);
    }

    async Task<bool> TalkStep(QuestStep step, CancellationToken ct)
    {
        if (!await EnsureZone(step.Zone, ct)) return false;
        nav.MoveTo(step.X, step.Y, step.Z);
        for (int t = 0; t < 60000 && nav.IsMoving && !ct.IsCancellationRequested; t += 100) await Task.Delay(100, ct);
        nav.Stop();
        var npc = p.Nearest(e => !e.IsMob && p.DistanceTo(e.X, e.Z) <= NpcReach);
        if (npc is null) { Console.WriteLine("[jobunlock] no NPC/object in reach"); return false; }
        return await quests.TalkTo(npc.Id, step.Option, ct);
    }

    // Equip the weapon, then defeat Count monsters with it (the quest counts kills server-side).
    async Task<bool> KillWith(ushort weaponItem, int count, CancellationToken ct)
    {
        await gear.EquipItem(weaponItem, Capabilities.EquipSlot.Main, ct);
        int killed = 0;
        while (killed < count && !ct.IsCancellationRequested)
        {
            var mob = p.Nearest(e => e.IsMob && e.Hpp > 0);
            if (mob is null) { await Task.Delay(2000, ct); continue; }   // wait for a mob to wander in
            await combat.Engage(mob.Id, ct);
            for (int t = 0; t < 60000 && !ct.IsCancellationRequested; t += 500)
            {
                await Task.Delay(500, ct);
                var e = p.World.Entities.TryGetValue(mob.Id, out var ent) ? ent : null;
                if (e is null || e.Hpp == 0) break;   // dead/despawned
            }
            killed++;
            if (killed % 10 == 0) Console.WriteLine($"[jobunlock] kill objective {killed}/{count}");
        }
        return killed >= count;
    }
}
