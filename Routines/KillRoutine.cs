namespace XiHeadless.Routines;

/// THE fight loop — extracted from LevelGrind so every combat path (solo grind, party farm, aggro defense,
/// healer-rescue peel) runs ONE implementation instead of each brain forking its own (the SubjobBrain
/// KillTarget fork never used weapon skills — that class of bug is what this kills). Job-specific behavior
/// comes in through Hooks; the loop itself is job-agnostic.
///
/// Folds in the hard-won fixes from the Buburimu farm sessions:
/// - TRUE 3D melee distance: the server's melee check includes height, so a mob on a ledge/slope is out of
///   reach even when 2D-close — 2D-only checks made the WAR stand in fake melee dealing zero damage.
/// - KITE-STEP: zero damage while genuinely 3D-in-melee = the mob is on ground the server counts as out of
///   reach. Step ~7y away onto reachable ground — the claimed mob follows down, and melee connects.
/// - LEDGE-PULL hook: 2D-close but 3D-far (it's above/below us) — a ranged hate tool (WAR Provoke, ~16y)
///   yanks it to us instead of chasing a spot the navmesh can't reach.
/// - Hate is unshakeable: once we're toe-to-toe with a mob it has hate, and walking off just stacks a second
///   attacker (the add-cascade death). In-melee no-progress NEVER abandons; only a mob we can't even reach
///   (fled to unpathable ground, no hate on us) is abandoned.
public static class KillRoutine
{
    public sealed class Hooks
    {
        public Func<uint, int, CancellationToken, Task> UseAbilities = (_, _, _) => Task.CompletedTask;   // (mob, con) — job buffs/JAs
        public Func<CancellationToken, Task<bool>> EmergencyHeal = _ => Task.FromResult(false);            // mage self-cure
        public Func<byte, byte> WepSkillForLevel = _ => (byte)1;                                           // level -> equipped weapon's skill id
        public Func<uint, CancellationToken, Task<bool>>? LedgePull;                                       // ranged hate yank for 2D-close/3D-far mobs
        public bool NoWeaponSkills;                                                                         // suppress WS use — the killing blow must be plain melee (RNG/DRK unlock quests)
        public string Tag = "kill";
    }

    /// Fight `mob` (already conned + approached by the caller) until it dies, we die, or it's genuinely
    /// unreachable. breakOffHpp > 0 = disengage below that HP% (for peels off the healer we mustn't die to);
    /// 0 = fight to the death (you can't outrun hate). Returns true iff the mob died.
    public static async Task<bool> Fight(
        ICombat combat, IPerception p, INavigation nav, IGear gear,
        Entity mob, int fightCon, Hooks h, int breakOffHpp, CancellationToken ct)
    {
        void Log(string m) => Console.WriteLine($"[{h.Tag}] {m}");

        nav.Face(mob.Id);
        await Task.Delay(250, ct);   // let the facing apply before we start swinging (must face to hit)
        Log($"engage 0x{mob.Id:X} '{mob.Name}' con={fightCon} at dist {p.DistanceTo(mob.X, mob.Z):F1}");
        await combat.Engage(mob.Id, ct);

        byte bestHpp = mob.Hpp;               // lowest mob HP we've driven it to
        long lastProgressMs = p.World.NowMs;  // last time the mob actually took damage
        int ft = 0, kites = 0;
        while (!ct.IsCancellationRequested && mob.Hpp > 0 && p.World.Hpp > 0
               && (breakOffHpp == 0 || p.World.Hpp > breakOffHpp)
               && (p.World.NowMs - mob.LastSeenMs) < 20000)
        {
            float d2 = p.DistanceTo(mob.X, mob.Z);
            float d3 = p.DistanceTo3D(mob.X, mob.Y, mob.Z);
            // DEADBAND on the chase: a single threshold made the tank flap between Follow and Stop every tick
            // as the mob shifted ("running back and forth while in combat" — user, watching live). Chase only
            // when clearly out of reach, stand only when clearly in it, and in between just keep facing.
            // Band capped at 3.0y: the server's melee range is ~3y, and standing at 3.4y "in band" whiffed
            // every swing while a 2%-HP mob landed free hits (user-observed).
            if (d2 > 3.0f) nav.Follow(mob.Id);
            else if (d2 <= 2.0f) { nav.Stop(); nav.Face(mob.Id); }
            else nav.Face(mob.Id);
            // ServerStatus==1 is the REAL engaged state (actually swinging). The local "engaged" can be true
            // while the server never started auto-attack -> status 0 -> zero damage while the mob hits us.
            // Re-sending Attack while already engaged is a server no-op, so re-issuing can't flap.
            if (p.World.ServerStatus != 1 && d2 <= 3.5f) { nav.Face(mob.Id); await combat.Engage(mob.Id, ct); }
            if (p.World.ServerStatus == 1) await h.UseAbilities(mob.Id, fightCon, ct);
            await h.EmergencyHeal(ct);
            byte wep = h.WepSkillForLevel(p.World.MainJobLevel);
            var ws = h.NoWeaponSkills ? null : CombatRoutines.BestWeaponSkill(wep, gear.SkillLevel(wep));
            if (ws is not null && p.World.ServerStatus == 1 && combat.CanWeaponSkill && mob.Hpp is > 10 and < 100)
            {
                Log($"{ws} (tp={combat.Tp} skill{wep}={gear.SkillLevel(wep)}) on mob hpp={mob.Hpp}");
                await combat.WeaponSkill(ws.Value, mob.Id, ct);
            }
            await Task.Delay(600, ct);
            if (++ft % 4 == 0) Log($"fighting hp%={p.World.Hpp} mp%={p.World.Mpp} tp={combat.Tp} | mob '{mob.Name}' hpp={mob.Hpp} d2={d2:F0} d3={d3:F0} attackers={p.AttackersOn(p.World.MyId)}");

            if (mob.Hpp < bestHpp) { bestHpp = mob.Hpp; lastProgressMs = p.World.NowMs; continue; }
            // 25s window: a Great Axe swings every ~8-9s, so two ordinary MISSES are ~18s of "no progress" —
            // the old 15s window kite-stepped through routine miss streaks (user: low swing speed weapon).
            if (p.World.NowMs - lastProgressMs <= 25000) continue;

            // 25s with no damage — diagnose by geometry, not by giving up:
            float dStuck2 = p.DistanceTo(mob.X, mob.Z);
            float dStuck3 = p.DistanceTo3D(mob.X, mob.Y, mob.Z);
            // HATE-LOCK (user rule: do NOT move on until the mob's HP is 0). Once hate exists — we've damaged
            // it or it has hit us — there is no abandoning: low-HP mobs FLEE, which stalls "progress" and grows
            // the distance, and quitting there just means eating its returning swings unarmed (happened twice,
            // live-observed). Chase it to the finish; abandoning is only for mobs we never traded hate with.
            bool hateEstablished = bestHpp < 100
                || (p.World.Attackers.TryGetValue(mob.Id, out var hlAtk) && hlAtk.target == p.World.MyId);
            if (dStuck2 > 3.5f)
            {
                if (!hateEstablished)
                {
                    Log($"STUCK: 0x{mob.Id:X} '{mob.Name}' unreachable ({dStuck2:F0}y), no hate traded — abandoning");
                    break;
                }
                Log($"'{mob.Name}' fleeing/out of reach at {mob.Hpp}% — hate-locked, chasing to the finish");
                nav.Follow(mob.Id);
                lastProgressMs = p.World.NowMs;
                continue;
            }
            if (dStuck3 > 6f && h.LedgePull is not null && await h.LedgePull(mob.Id, ct))
            {
                // 2D-close but 3D-far = it's on a ledge above/below us — the hook (e.g. Provoke) yanks it down.
                Log($"'{mob.Name}' is 2D-close/3D-far (ledge, d3={dStuck3:F0}) — ledge-pulled it toward us");
                lastProgressMs = p.World.NowMs;
                continue;
            }
            if (kites < 3)
            {
                // In TRUE 3D melee yet zero damage = the mob stands on ground the server's (stricter) reach
                // check rejects. Step away; the claimed mob follows onto ground where melee connects.
                kites++;
                var (sx, sz) = KiteStep(p, nav, mob);
                Log($"'{mob.Name}' zero-damage in 3D melee (bad ground) — KITE {kites}/3: step to ({sx:F0},{sz:F0}), mob follows");
                nav.MoveTo(sx, sz);
                for (int t = 0; t < 8 && nav.IsMoving && !ct.IsCancellationRequested; t++) await Task.Delay(400, ct);
                lastProgressMs = p.World.NowMs;
                continue;
            }
            // Kited out and still no damage while toe-to-toe: it HAS hate and hate can't be shaken — stay on
            // it (evasive/slept mobs kill slowly) rather than walking off and stacking an add.
            Log($"slow kill on 0x{mob.Id:X} '{mob.Name}' after {kites} kites — staying on it (hp%={p.World.Hpp})");
            lastProgressMs = p.World.NowMs;
        }
        bool killed = mob.Hpp == 0;
        nav.Stop();
        Log($"fight ended: '{mob.Name}' hpp={mob.Hpp} myHP%={p.World.Hpp} killed={killed}");
        if (combat.Engaged) combat.Disengage();
        return killed;
    }

    // A nearby reachable point ~7y directly away from the mob (fanning the angle when straight-back is
    // blocked) — the kite destination that drags a bad-ground mob onto meshable terrain. Zone-agnostic:
    // no corridor clamps; the navmesh CanReach is the only constraint.
    static (float x, float z) KiteStep(IPerception p, INavigation nav, Entity mob)
    {
        float mx = p.World.X, mz = p.World.Z, my = p.World.Y;
        float dx = mx - mob.X, dz = mz - mob.Z;
        float len = Geometry.Dist2D(mx, mz, mob.X, mob.Z);
        if (len < 0.5f) { dx = 0f; dz = -1f; len = 1f; }
        dx /= len; dz /= len;
        for (int i = 0; i < 6; i++)
        {
            float ang = (i % 2 == 0 ? 1f : -1f) * (i / 2) * 0.6f;
            float cosA = MathF.Cos(ang), sinA = MathF.Sin(ang);
            float rx = dx * cosA - dz * sinA, rz = dx * sinA + dz * cosA;
            float tx = mx + rx * 7f, tz = mz + rz * 7f;
            if (nav.CanReach(tx, my, tz)) return (tx, tz);
        }
        return (mx, mz - 7f);
    }
}
