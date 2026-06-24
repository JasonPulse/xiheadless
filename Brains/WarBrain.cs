namespace XiHeadless.Brains;

/// WAR: find a wild mob, navigate into melee range, engage, build TP (shared routine),
/// and weaponskill when ready — fighting until the mob (or the bot) dies.
public sealed class WarBrain(IPerception p, INavigation nav, ICombat combat, IZoning zoning, IGear gear) : IBrain
{
    public async Task RunAsync(CancellationToken ct)
    {
        const ushort huntZone = 107;   // South Gustaberg

        // Equip our weapon up front so swings build Sword skill (a bare-fisted WAR builds H2H instead,
        // and hits for almost nothing). The inventory (0x01F/0x020) streams in over the first ~1-2s
        // after zone-in, so poll for the item before giving up — checking immediately races the stream.
        const uint weapon = 16534;     // Onion Sword
        for (int i = 0; i < 20 && !gear.HasItem(weapon) && !ct.IsCancellationRequested; i++)
            await Task.Delay(250, ct);
        if (gear.HasItem(weapon))
        {
            bool eq = await gear.EquipItem(weapon, 0, ct);   // 0 = SLOT_MAIN
            Console.WriteLine($"[war] equip weapon {weapon}: {eq}");
            await Task.Delay(1000, ct);   // let the server apply the equip before we start swinging
        }
        else Console.WriteLine($"[war] weapon {weapon} not in inventory — fighting unarmed");
        // The skills packet (0x062) often lands just after the brain starts; wait briefly so this log
        // reflects the real (persisted) skill instead of racing it to 0. A genuinely fresh char stays 0.
        for (int i = 0; i < 16 && gear.SkillLevel(SwordSkill) == 0 && !ct.IsCancellationRequested; i++)
            await Task.Delay(250, ct);
        Console.WriteLine($"[war] start skills: Sword={gear.SkillLevel(SwordSkill)} H2H={gear.SkillLevel(1)} Axe={gear.SkillLevel(5)}");
        Console.WriteLine($"[war] hunting for a mob ({_patrol.Count} patrol pts)");

        while (!ct.IsCancellationRequested)
        {
            // Dead (KO'd): stop moving (no corpse-teleporting) and return to home point to revive.
            // Bots will die — recovery must be autonomous. Homepoint warps + revives (a zone change).
            if (combat.Dead)
            {
                nav.Stop();
                Console.WriteLine("[war] KO'd — returning to home point to revive");
                await combat.Homepoint(ct);
                await Task.Delay(5000, ct);   // wait for the warp/revive (zone change) to land
                continue;
            }

            // Not in the hunt zone (e.g. just revived at the home point) — travel back to it.
            if (zoning.CurrentZone != huntZone && zoning.CurrentZone != 0)
            {
                Console.WriteLine($"[war] in zone {zoning.CurrentZone}, returning to hunt zone {huntZone}");
                await zoning.ToZone(huntZone, ct);
                continue;
            }

            // A real, reachable mob: wild allegiance, alive, on the ground (exclude the (0,200,0)
            // sentinel), FRESH (updated recently — don't chase a stale ghost that wandered off),
            // and within pull range.
            // Skip known-aggressive families (Quadavs aggro and kill a lvl1 instantly) — at low level
            // the bees/crabs/lizards near the Bastok-Gustaberg line are passive and safe to pull.
            var mob = p.Nearest(e => e.IsMob && e.Hpp > 0 && e.Y < 100
                && !e.Name.Contains("Quadav", StringComparison.OrdinalIgnoreCase)
                && !_skip.Contains(e.Id)
                && (p.World.NowMs - e.LastSeenMs) < 20000 && p.DistanceTo(e.X, e.Z) <= 50f);
            if (mob is null)
            {
                _skip.Clear();   // re-evaluate everything next sweep (mobs respawn / we roam)
                if (_patrol.Count > 0)
                {
                    // Advance to the next leg whenever movement stops (arrived, or the path ran to a
                    // barrier short of the point). This guarantees the sweep progresses and never wedges.
                    if (!nav.IsMoving)
                    {
                        var wp = _patrol[_patrolIdx++ % _patrol.Count];
                        nav.MoveTo(wp.x, wp.z);
                        Console.WriteLine(nav.IsMoving
                            ? $"[war] patrol -> ({wp.x:F0},{wp.z:F0}) dist={p.DistanceTo(wp.x, wp.z):F0}"
                            : $"[war] patrol pt ({wp.x:F0},{wp.z:F0}) unreachable; next");
                    }
                }
                else if (!nav.IsMoving) RoamStep();
                await Task.Delay(1000, ct);
                continue;
            }
            nav.Stop();

            // Consider (/check) the mob: only fight winnable ones that give EXP. TooWeak(0) gives no
            // EXP; Tough+(>=5) will kill a lvl1. Default sweet spot EasyPrey(2)..EvenMatch(4).
            int con = await combat.Consider(mob.Id, ct);
            if (con < _conMin || con > _conMax)
            {
                Console.WriteLine($"[war] skip 0x{mob.Id:X} '{mob.Name}' con={con} lvl={p.World.ConMobLevel} (want {_conMin}-{_conMax})");
                _skip.Add(mob.Id);
                continue;
            }
            Console.WriteLine($"[war] target 0x{mob.Id:X} '{mob.Name}' con={con} lvl={p.World.ConMobLevel} hpp={mob.Hpp} dist={p.DistanceTo(mob.X, mob.Z):F0}");

            // Walk into true melee range (no teleport — navmesh follow). dist 3 was too far to engage.
            nav.Follow(mob.Id);
            for (int i = 0; i < 80 && !ct.IsCancellationRequested; i++)
            {
                if (mob.Hpp == 0 || (p.World.NowMs - mob.LastSeenMs) > 20000 || p.DistanceTo(mob.X, mob.Z) <= 2.0f) break;
                await Task.Delay(250, ct);
            }
            nav.Stop();
            if (mob.Hpp == 0 || (p.World.NowMs - mob.LastSeenMs) > 20000) { Console.WriteLine("[war] target lost during approach"); continue; }

            nav.Face(mob.Id);   // server rejects attacks ("unable to see target") unless we face it
            Console.WriteLine($"[war] sending engage on 0x{mob.Id:X} (idx {mob.Index}) at dist {p.DistanceTo(mob.X, mob.Z):F1}");
            await combat.Engage(mob.Id, ct);   // engage ONCE; the server auto-attacks on the weapon timer.

            // Fight until the mob dies, we die, or it wanders off. DON'T re-spam engage (that resets
            // the attack timer -> "wait longer"); just hold melee range and keep facing it.
            while (!ct.IsCancellationRequested && mob.Hpp > 0 && p.World.Hpp > 0 && (p.World.NowMs - mob.LastSeenMs) < 20000)
            {
                float d = p.DistanceTo(mob.X, mob.Z);
                if (d > 2.5f) nav.Follow(mob.Id);      // chase if it moves
                else { nav.Stop(); nav.Face(mob.Id); } // hold + face, let the server swing
                // Weaponskill is DRIVEN BY TRACKED SKILL: pick the best sword WS our current Sword
                // skill has unlocked (Fast Blade unlocks at Sword skill 5) — automatic, no override.
                // Gate WS on the authoritative engaged flag PLUS mob.Hpp in (10,100): combat.Engaged
                // self-clears the instant the mob dies / we disengage (no firing outside a fight),
                // <100 confirms the engage actually connected (we've landed a hit), and >10 avoids the
                // death-tick race that produced "Character is not engaged" rejections.
                var ws = CombatRoutines.BestWeaponSkill(SwordSkill, gear.SkillLevel(SwordSkill));
                if (ws is not null && combat.Engaged && combat.CanWeaponSkill && mob.Hpp is > 10 and < 100)
                {
                    Console.WriteLine($"[war] {ws} (tp={combat.Tp} sword={gear.SkillLevel(SwordSkill)}) on mob hpp={mob.Hpp}");
                    await combat.WeaponSkill(ws.Value, mob.Id, ct);
                }
                await Task.Delay(2000, ct);
                Console.WriteLine($"[war] fighting myHP%={p.World.Hpp} tp={combat.Tp} sword={gear.SkillLevel(3)} | mob 0x{mob.Id:X} hpp={mob.Hpp} dist={p.DistanceTo(mob.X, mob.Z):F0}");
            }
            Console.WriteLine($"[war] fight ended: mob hpp={mob.Hpp} myHP%={p.World.Hpp}");
            // Only disengage if we're walking away from a LIVE mob we actually connected with. A dead
            // mob already auto-disengaged us server-side, and a mob still at 100% was never engaged —
            // sending AttackOff in either case is what the server rejected as "Character is not engaged".
            if (combat.Engaged && mob.Hpp < 100) combat.Disengage();
            await Task.Delay(1000, ct);
        }
    }

    readonly List<(float x, float z)> _patrol = new();
    int _patrolIdx;

    // Con gate: only fight mobs in [min,max] difficulty (2=EasyPrey .. 4=EvenMatch).
    const int _conMin = 2;
    const int _conMax = 4;
    readonly HashSet<uint> _skip = new();   // mobs rejected by con this sweep
    // The equipped weapon (Onion Sword) is a sword -> combat-skill id 3; WS auto-selects off this.
    const byte SwordSkill = 3;

    int _roam;
    // Explore to find mobs: probe 8 compass directions (~60y) and walk the first one the navmesh
    // can actually reach. Zone geometry varies (the entrance corner is often walled on most sides),
    // so don't assume a fixed heading — let the mesh decide. Rotate the start dir to spread coverage.
    void RoamStep()
    {
        var w = p.World;
        for (int i = 0; i < 8; i++)
        {
            double ang = (_roam + i) * Math.PI / 4;
            float cx = w.X + (float)Math.Cos(ang) * 60f, cz = w.Z + (float)Math.Sin(ang) * 60f;
            nav.MoveTo(cx, cz);
            if (nav.IsMoving) { _roam += i + 1; Console.WriteLine($"[war] roaming -> ({cx:F0},{cz:F0})"); return; }
        }
        _roam++;
        Console.WriteLine("[war] roam: no reachable direction from here");
    }
}
