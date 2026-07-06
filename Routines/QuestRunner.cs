namespace XiHeadless.Routines;

/// Executes a quest as a sequence of typed QuestSteps (goto, talk, examine, zone-in-from, equip,
/// kill-N-with-weapon, trade). The SERVER enforces quest state, key items, level, prereqs and fame — this
/// only performs the physical actions in order. Reused by JobUnlockBrain (advanced-job quests) and
/// SubjobBrain (the subjob unlock quest), so the navigate-to-NPC-and-talk/trade logic lives in ONE place.
public sealed class QuestRunner(
    IPerception p, INavigation nav, IZoning zoning, IQuests quests, ITradeNpc trade, ICombat combat, IGear gear,
    IEvents events, IInventory inv)
{
    const float NpcReach = 8f;

    /// Run all steps in order. Returns true only if every step succeeded; logs progress with [tag].
    public async Task<bool> Run(IReadOnlyList<QuestStep> steps, string tag, CancellationToken ct)
    {
        for (int i = 0; i < steps.Count && !ct.IsCancellationRequested; i++)
        {
            var step = steps[i];
            Console.WriteLine($"[{tag}] step {i + 1}/{steps.Count} [{step.Kind}]: {step.Label}");
            if (!await Do(step, tag, ct)) { Console.WriteLine($"[{tag}] step failed — stopping"); return false; }
            await Task.Delay(1200, ct);
        }
        return true;
    }

    async Task<bool> Do(QuestStep step, string tag, CancellationToken ct) => step.Kind switch
    {
        StepKind.Goto       => await EnsureZone(step.Zone, tag, ct),
        StepKind.Talk       => await TalkStep(step, tag, ct),
        StepKind.Examine    => await TalkStep(step, tag, ct, byPosition: true),          // examine = trigger OBJECT at the step coords
        StepKind.ZoneInFrom => await EnsureZone(step.FromZone, tag, ct) && await EnsureZone(step.Zone, tag, ct),
        StepKind.Equip      => await gear.EquipItem(step.ItemId, step.Slot, ct),
        StepKind.KillWith   => await KillWith(step.ItemId, step.Count, tag, ct),
        StepKind.Trade      => await TradeStep(step, tag, ct),
        _                   => false,
    };

    async Task<bool> EnsureZone(string zone, string tag, CancellationToken ct)
    {
        if (Game.Zonelines.Resolve(zone) is not ushort id) { Console.WriteLine($"[{tag}] unknown zone '{zone}'"); return false; }
        if (zoning.CurrentZone == id) return true;
        return await zoning.GoTo(zone, ct);
    }

    async Task<NpcArrival> WalkToNpc(QuestStep step, string tag, CancellationToken ct, bool byPosition = false)
    {
        if (!await EnsureZone(step.Zone, tag, ct)) return default;
        // ENFORCE arrival: dungeon paths stall mid-way (mesh gaps/timeouts) — the old single-leg walk once
        // stopped 150y short and the nearest-entity search then TALKED TO A GOBLIN. Retry legs; if we still
        // can't get near the step coords, fail the step honestly instead of eventing at whatever's nearby.
        for (int leg = 0; leg < 3 && p.DistanceTo(step.X, step.Z) > 10f && !ct.IsCancellationRequested; leg++)
        {
            nav.MoveTo(step.X, step.Y, step.Z);
            for (int t = 0; t < 90000 && nav.IsMoving && !ct.IsCancellationRequested; t += 100) await Task.Delay(100, ct);
            nav.Stop();
            await Task.Delay(500, ct);
        }
        if (p.DistanceTo(step.X, step.Z) > 12f)
        {
            Console.WriteLine($"[{tag}] couldn't reach step coords ({step.X:F0},{step.Z:F0}) — stopped {p.DistanceTo(step.X, step.Z):F0}y away");
            return default;
        }
        // Just-arrived NPC entities often haven't sent their position update yet (they read as (0,0) until
        // then), so the nearest-NPC lookup can come up empty for a moment. Poll for a few seconds before
        // giving up. A named, non-mob entity within reach is our target NPC.
        Entity? npc = null;
        // EXAMINE targets are trigger OBJECTS pinned at the step coords — match by POSITION, allowing blank
        // names and mob-typed entities (qm points read as mobs), and never fall back to a nearby named
        // entity (that path talked to a goblin once and a Stink Bat the next time).
        if (byPosition)
        {
            for (int t = 0; t < 10000 && npc is null && !ct.IsCancellationRequested; t += 500)
            {
                npc = p.Nearest(e =>
                {
                    if (e.Id == p.World.MyId) return false;
                    float dx = e.X - step.X, dz = e.Z - step.Z;
                    return dx * dx + dz * dz <= 36f;   // within 6y of the step coords
                });
                if (npc is null) await Task.Delay(500, ct);
            }
            if (npc is null)
            {
                Console.WriteLine($"[{tag}] no entity within 6y of ({step.X:F0},{step.Z:F0}); nearest:");
                foreach (var e in p.World.Entities.Values.OrderBy(e => (e.X - step.X) * (e.X - step.X) + (e.Z - step.Z) * (e.Z - step.Z)).Take(6))
                    Console.WriteLine($"[{tag}]   0x{e.Id:X} '{e.Name}' ({e.X:F0},{e.Z:F0}) mob={e.IsMob} typed={e.TypeKnown} alleg={e.Allegiance}");
                return default;
            }
            // VERTICAL-LAYER correction: Ordelle's pool sits UNDER a walkable overhang — the planar walk
            // landed 29y ABOVE the object (my Y=30, qm Y=1) where the server's ~6y interact range fails.
            // Re-approach the object's LIVE 3D position (entity Y beats the step's approximate Y).
            for (int a = 0; a < 3 && (MathF.Abs(p.World.Y - npc.Y) > 5f || p.DistanceTo(npc.X, npc.Z) > 5f) && !ct.IsCancellationRequested; a++)
            {
                nav.MoveTo(npc.X, npc.Y, npc.Z);
                for (int t = 0; t < 60000 && nav.IsMoving && !ct.IsCancellationRequested; t += 200) await Task.Delay(200, ct);
                nav.Stop();
                await Task.Delay(500, ct);
            }
            if (MathF.Abs(p.World.Y - npc.Y) > 5f || p.DistanceTo(npc.X, npc.Z) > 6f)
            {
                Console.WriteLine($"[{tag}] WRONG LEVEL/RANGE: me ({p.World.X:F0},{p.World.Y:F0},{p.World.Z:F0}) vs object ({npc.X:F0},{npc.Y:F0},{npc.Z:F0}) — failing step honestly");
                return default;
            }
            nav.Face(npc.Id);
            await Task.Delay(1500, ct);
            Console.WriteLine($"[{tag}] at object 0x{npc.Id:X} '{npc.Name}' ({p.DistanceTo(npc.X, npc.Z):F1}y, dY={MathF.Abs(p.World.Y - npc.Y):F1}) idx={npc.Index}");
            return new NpcArrival(npc.Id, npc.Index, true);
        }
        for (int t = 0; t < 10000 && npc is null && !ct.IsCancellationRequested; t += 500)
        {
            // Find the nearest NAMED non-self entity (the quest NPC). NOT filtered by !IsMob: town/quest NPCs
            // can have Allegiance 0, which IsMob misclassifies as a monster (Isacio read as mob=True). Since we
            // navigated to the NPC's exact coords, the nearest named non-self entity IS the target NPC.
            npc = p.Nearest(e => e.Id != p.World.MyId && e.Name.Length > 0 && p.DistanceTo(e.X, e.Z) <= NpcReach);
            if (npc is null) await Task.Delay(500, ct);
        }
        // SECOND APPROACH: city nav can stop short (mesh gaps/arrival tolerance) — if a NAMED entity sits
        // just outside reach (Balasiel was visible at 16y), walk to ITS live position and re-check.
        if (npc is null)
        {
            var near = p.Nearest(e => e.Id != p.World.MyId && e.Name.Length > 0 && p.DistanceTo(e.X, e.Z) <= 30f);
            if (near is not null)
            {
                Console.WriteLine($"[{tag}] '{near.Name}' visible at {p.DistanceTo(near.X, near.Z):F0}y — approaching directly");
                nav.MoveTo(near.X, near.Z);
                for (int t = 0; t < 20000 && nav.IsMoving && !ct.IsCancellationRequested; t += 200) await Task.Delay(200, ct);
                nav.Stop();
                await Task.Delay(1000, ct);
                npc = p.Nearest(e => e.Id != p.World.MyId && e.Name.Length > 0 && p.DistanceTo(e.X, e.Z) <= NpcReach);
            }
        }
        if (npc is null)
        {
            Console.WriteLine($"[{tag}] no NPC/object in reach at ({step.X:F0},{step.Z:F0}) after wait. {p.World.Entities.Count} entities; nearest:");
            foreach (var e in p.World.Entities.Values.OrderBy(e => p.DistanceTo(e.X, e.Z)).Take(6))
                Console.WriteLine($"[{tag}]   0x{e.Id:X} '{e.Name}' ({e.X:F0},{e.Z:F0}) d={p.DistanceTo(e.X, e.Z):F0} mob={e.IsMob} typed={e.TypeKnown} alleg={e.Allegiance}");
            return default;
        }
        // Face the NPC and settle a moment: the server triggers Talk by ActIndex with a distance<=6y check
        // against OUR position, so make sure it has our final (arrived) position + heading before we Talk.
        nav.Face(npc.Id);
        await Task.Delay(2500, ct);
        Console.WriteLine($"[{tag}] at NPC 0x{npc.Id:X} '{npc.Name}' ({p.DistanceTo(npc.X, npc.Z):F1}y) idx={npc.Index}");
        return new NpcArrival(npc.Id, npc.Index, true);
    }

    async Task<bool> TalkStep(QuestStep step, string tag, CancellationToken ct, bool byPosition = false)
    {
        var a = await WalkToNpc(step, tag, ct, byPosition);
        if (!a.Ok) return false;

        // messageSpecial qm (Examine with no EventId): the NPC's onTrigger just sets a var / grants a key item
        // and shows a special message — there is NO event start, so nothing to await or EVENTEND. Just fire the
        // 0x1A trigger. qm are picky (the "right" spawn is random) and some are TIMED (Squire II: examine the
        // pool qm, which sets a 30s Timer, then examine the dew qm within it — re-triggering the pool qm RESETS
        // the timer, so retrying is harmless AND keeps the window fresh), so retry a handful of times.
        if (byPosition && step.EventId == 0)
        {
            for (int attempt = 0; attempt < 4 && !ct.IsCancellationRequested; attempt++)
            {
                await events.Trigger(a.Id, ct);
                await Task.Delay(1000, ct);
            }
            Console.WriteLine($"[{tag}] examined qm 0x{a.Id:X} (messageSpecial, trigger-only x4) — verify via effect/key item");
            return true;
        }

        // BLIND-FINISH path (EventId set): the bot never receives the event-start packet (recv gap), so we
        // can't wait for it. Send the Talk to fire the NPC's onTrigger server-side (which starts the known
        // event), then EVENTEND the KNOWN csid + option so the server runs onEventFinish and sets the real
        // quest flags. We CONFIRM success by reading the 0x056 quest-log back (logged as [quest-log]).
        if (step.EventId != 0)
        {
            await events.Examine(a.Id, ct);                                       // 0x1A Talk -> onTrigger -> progressEvent(EventId)
            await events.Finish(a.Id, a.Index, step.EventId, step.Option, ct);    // 0x5B EVENTEND -> onEventFinish(EventId, option)
            await Task.Delay(2000, ct);                                           // let the server push the updated quest-log
            Console.WriteLine($"[{tag}] blind-finished ev{step.EventId} opt={step.Option} at 0x{a.Id:X} — verify via [quest-log]");
            return true;
        }

        bool started = await quests.TalkTo(a.Id, step.Option, ct);
        Console.WriteLine($"[{tag}] talked to 0x{a.Id:X} opt={step.Option} -> event {p.World.EventId} (started={started})");
        return started;
    }

    async Task<bool> TradeStep(QuestStep step, string tag, CancellationToken ct)
    {
        // Item gone = this trade already happened on a previous run (quest materials are Keep-protected, so
        // consumption is the only way they leave the bag). Skip instead of failing the whole chain forever.
        if (!inv.Has(step.ItemId)) { Console.WriteLine($"[{tag}] item {step.ItemId} absent — assuming already traded, skipping"); return true; }
        var a = await WalkToNpc(step, tag, ct);
        if (!a.Ok) return false;
        bool traded = await trade.Trade(a.Id, a.Index, new[] { (step.ItemId, (uint)step.Count) }, ct);
        // Blind-finish the event the trade triggers (onTrade -> progressEvent(EventId)); we never receive the
        // event-start, so EVENTEND the KNOWN csid to run onEventFinish (advances/completes the quest).
        if (traded && step.EventId != 0)
        {
            await Task.Delay(1500, ct);
            await events.Finish(a.Id, a.Index, step.EventId, step.Option, ct);
            await Task.Delay(2000, ct);
            Console.WriteLine($"[{tag}] traded {step.ItemId} + blind-finished ev{step.EventId} — verify via [quest-log]");
        }
        return traded;
    }

    // Equip the weapon, then defeat Count monsters with it (the quest counts kills server-side). Fights run
    // through the shared KillRoutine.Fight (one fight loop, with its 3D-melee/kite/hate-lock hardening) —
    // NoWeaponSkills so the killing blow is plain melee (DRK Chaosbringer / RNG unlock require no-WS kills).
    async Task<bool> KillWith(ushort weaponItem, int count, string tag, CancellationToken ct)
    {
        await gear.EquipItem(weaponItem, Capabilities.EquipSlot.Main, ct);
        var hooks = new KillRoutine.Hooks { NoWeaponSkills = true, Tag = tag };
        int killed = 0;
        while (killed < count && !ct.IsCancellationRequested)
        {
            var mob = p.Nearest(e => e.IsMob && e.Hpp > 0);
            if (mob is null) { await Task.Delay(2000, ct); continue; }
            if (await KillRoutine.Fight(combat, p, nav, gear, mob, fightCon: 4, hooks, breakOffHpp: 0, ct))
            {
                killed++;
                if (killed % 10 == 0) Console.WriteLine($"[{tag}] kill objective {killed}/{count}");
            }
        }
        return killed >= count;
    }

    readonly record struct NpcArrival(uint Id, ushort Index, bool Ok);
}
