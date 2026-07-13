namespace XiHeadless.Routines;

/// Reusable party HEALER/support loop — keep a tank ally alive, stay in heal range, self-preserve, rest MP.
/// Extracted from PartyLeechBrain so NO brain holds hardcoded cure/heal logic; a healer brain only supplies a
/// Config (who to heal, which spell, thresholds, the camp to converge on) and calls RunAsync. Reuse this for any
/// duo/party healer. (See CLAUDE.md: reuse routines, don't re-implement in brains.)
public sealed class PartySupport(IParty party, IPerception p, INavigation nav, IZoning zoning, IMagic magic, ICombat combat)
{
    public sealed class Config
    {
        public uint TankId;                  // the ally we heal + follow (the tank that holds hate)
        public string TankName = "";         // for the REFORM tell handshake (partyless tank asks us to re-invite)
        public Spell Heal = Spell.Cure;      // floor when no Cure line tier is castable yet (BestReady picks above it)
        public byte CureTankBelow = 60;       // cure the tank below this HP% (60 per the user — above it, curing is wasted MP)
        public byte CureSelfBelow = 55;       // self-cure below this HP% (when safe)
        public float StayWithin = 13f;        // close the gap before curing if the tank drifts past this
        public float FollowBuffer = 10f;      // otherwise hold ~this far behind the tank (out of its cleave)
        public Action? OnConverged = null;    // one-shot when we first reach the tank (e.g. drop Invis)
        public string Tag = "heal";
        public bool Buff = false;             // keep Protect + Shell up on the tank and self
        public bool Enfeeble = false;         // cast Dia (DoT + Def-down) + Paralyze on the tank's foe
        public int CastHoldMs = 3200;         // STAND STILL this long after starting a cast — moving interrupts it (covers Cure I/II ~2.25-2.5s cast + margin)
        public byte RestMpTo = 50;            // between-fights MP rest target %
        // The shared split/reunite owner — REQUIRED in practice: any split (our death, tank death, tank
        // zoned, RALLY chat) routes through Reunion's rally protocol. The old Reunion-less legacy branches
        // (solo re-cross, homepoint regroup, Raise-the-tank) were dead code duplicating Reunion and were
        // removed; a caller without one gets a loud warning and no split recovery.
        public Reunion? Reunion;
        public bool Inviter = false;          // we own party formation — re-invite the tank after a detected disband
    }

    public async Task RunAsync(Config cfg, CancellationToken ct)
    {
        if (cfg.Reunion is null)
            Log.Always($"[{cfg.Tag}] WARNING: no Reunion configured — PartySupport has NO split recovery without one (the legacy Reunion-less branches were removed as dead duplicates of Reunion)");
        byte lastHpp = p.World.Hpp;
        bool converged = false;
        bool sawTankAlive = false;   // guards the dead-tank regroup so a not-yet-populated tank (Hpp reads 0 at start) doesn't trip it
        float lastX = p.World.X, lastZ = p.World.Z; int stuckTicks = 0;
        var castMs = new Dictionary<(Spell sp, uint who), long>();   // last cast time per (spell,target) — buffs/debuffs don't re-cast every tick

        // Stand still, cast, and HOLD through the cast — the fix for "heal walking" (a Cure cast while following
        // the tank is interrupted by our own movement and never lands; dozens were logged, none healed).
        // For CURES, verify the EFFECT: the "I cast" line is a decision; the landed heal is the 0x028 recover
        // record (user rule — decision logs were misleading: a lv17 WHM "cast" Cure III for a whole fight while
        // the server rejected every one and the tank died). "cure MISSED" in the log = casts are failing again.
        async Task<bool> StandCast(Spell sp, uint tgt, string why)
        {
            nav.Stop();
            await Task.Delay(250, ct);            // settle: make sure we're not still gliding when the cast begins
            Log.Info($"[{cfg.Tag}] {why}");
            long castMs0 = p.World.NowMs;
            magic.Cast(sp, tgt);
            await Task.Delay(cfg.CastHoldMs, ct);  // remain stationary through the cast (+ margin) so it resolves
            if (Spells.Info.TryGetValue(sp, out var si) && si.Line == SpellLine.Cure)   // any Cure tier — verify the landed heal
            {
                var h = p.World.LastHeal;
                if (h.actor == p.World.MyId && h.target == tgt && h.ms >= castMs0)
                    Log.Info($"[{cfg.Tag}] {sp} LANDED on 0x{tgt:X} (+{h.amount}HP)");
                else
                    Log.Info($"[{cfg.Tag}] {sp} MISSED (no recover event — interrupted/rejected?)");
            }
            return true;
        }

        for (int tick = 0; !ct.IsCancellationRequested; tick++)
        {
            // ANY split (our death, tank dead, tank zoned away, partner RALLY) → the ONE shared reunion protocol:
            // both bots rally at the crystal and cross back together. This is the fix for the one-sided-death
            // desync — no more inferring the tank's state from a stale entity view.
            if (cfg.Reunion is { } ru && ru.SplitDetected())
            {
                nav.Stop();
                await ru.RunAsync(ct);
                converged = true;   // reunion ends with the tank visible beside us at the zone-in
                stuckTicks = 0; lastHpp = p.World.Hpp;
                continue;
            }
            // Death is universal/core (BotHost homepoints + the Navigator refuses to move while dead) — just re-converge.
            if (combat.Dead) { converged = false; stuckTicks = 0; await Task.Delay(2000, ct); continue; }
            if (party.InvitePending) party.AcceptInvite();

            bool tankVisible = p.World.Entities.TryGetValue(cfg.TankId, out var tank) && (tank.X != 0 || tank.Z != 0);
            float tankDist = tankVisible ? p.DistanceTo(tank!.X, tank.Z) : 999f;

            // Stuck recovery: trying to reach the tank but our position isn't changing → can't path → regroup via home point.
            float moved = MathF.Abs(p.World.X - lastX) + MathF.Abs(p.World.Z - lastZ);
            lastX = p.World.X; lastZ = p.World.Z;
            if (tankDist > 15f && moved < 2f) stuckTicks++; else stuckTicks = 0;
            if (stuckTicks > 45)
            {
                Log.Info($"[{cfg.Tag}] STUCK reaching tank — regrouping");
                if (cfg.Reunion is { } ru2) { await ru2.RunAsync(ct); converged = true; }
                stuckTicks = 0;
                continue;
            }

            if (!converged)
            {
                if (tankVisible && tankDist < 25f) { converged = true; cfg.OnConverged?.Invoke(); Log.Info($"[{cfg.Tag}] reached tank — supporting"); }
                else if (tankVisible) nav.Follow(cfg.TankId);
                else nav.Stop();   // hold — the tank walks to US (tether) or a split triggers the rally
                if (tick % 8 == 0) Log.Info($"[{cfg.Tag}] converging tankVisible={tankVisible} tankDist={(tankVisible ? tankDist : -1):F0} party={party.MemberCount}");
                await Task.Delay(2000, ct);
                continue;
            }

            // SELF-PRESERVATION: if WE took damage since last tick, a cast would be interrupted — run to the tank
            // so it pulls hate off us, then resume. Never stand still while being beaten.
            bool beingHit = p.World.Hpp > 0 && p.World.Hpp < lastHpp;
            lastHpp = p.World.Hpp;
            if (beingHit && tankVisible) { nav.Follow(cfg.TankId); await Task.Delay(1000, ct); continue; }

            // Shared state for the action cascade. CASTING REQUIRES STANDING STILL (StandCast handles that); spells
            // fizzle past ~20y. tankAttackers (from 0x028) tells us if a fight is ON — we only REST between fights.
            const float CastRange = 20f;
            // "Can cure" = absolute MP for the cheapest Cure (8), not a % floor — a lv17 WHM at 9% has ~10 MP
            // and CAN land Cure I; the 15% floor wrote it off and it sat down mid-fight instead.
            bool haveMp = p.World.Mp >= 10;
            p.World.PartyMembers.TryGetValue(cfg.TankId, out var tankPm);
            // DISBAND DETECTION (event-driven, not a freshness window): the server refreshes the roster with a
            // SELF group-list entry on leave/disband; a refresh that never re-listed the tank means the tank is
            // gone from the party. Without this, a tank relog left a FROZEN roster (party=1, tank 71% at dist 0
            // for 10+ minutes) and no re-invite ever fired — both bots idled until a manual restart.
            if (tankPm is not null && p.World.SelfGroupListMs > tankPm.LastSeenMs + 3000)
            {
                Log.Info($"[{cfg.Tag}] roster refreshed without the tank — party dissolved; re-forming");
                p.World.PartyMembers.Remove(cfg.TankId);
                if (cfg.Inviter) party.Invite(cfg.TankId);
                await Task.Delay(3000, ct);
                continue;
            }
            // REFORM tell: a relogged, PARTYLESS tank can't party-chat and our roster may be a stale phantom
            // (party=1, frozen vitals — idled both bots repeatedly). The tell bypasses the party channel:
            // purge the phantom and re-invite.
            if (cfg.Inviter && cfg.TankName.Length > 0
                && p.World.Tells.TryGetValue(cfg.TankName, out var tell)
                && tell.msg.Contains("REFORM") && p.World.NowMs - tell.ms < 60_000)
            {
                Log.Info($"[{cfg.Tag}] REFORM tell from '{cfg.TankName}' — purging stale roster + re-inviting");
                p.World.Tells.Remove(cfg.TankName);
                p.World.PartyMembers.Remove(cfg.TankId);
                party.Invite(cfg.TankId);
                await Task.Delay(3000, ct);
                continue;
            }
            byte tankHp = tankPm?.Hpp ?? 0;
            // 12s window: the 6s default read "no mobs on tank" in the gap between goblin swings, so the WHM
            // sat down DURING the tank's fight and arrived late/out of cast range when it finally stood.
            int tankAttackers = p.AttackersOn(cfg.TankId, 12000);
            bool tankInRange = tankVisible && tankDist <= CastRange;
            // Heal with the BEST Cure tier we can CAST (the shared selector — Ready is now level-gated via
            // SpellLevels, the fix for the lv17 WHM that spam-failed Cure III while the tank died), capped at
            // Cure III for MP economy (Cure IV/V cost 88-135 MP/cast and would OOM a leveling WHM fast).
            Spell heal = magic.BestReady(SpellLine.Cure, maxTier: 3) ?? cfg.Heal;
            bool canHeal = magic.Ready(heal);

            if (tick % 10 == 0) Log.Info($"[{cfg.Tag}] party={party.MemberCount} tankDist={(tankVisible ? tankDist : -1):F0} tankHP={tankHp}% tankAtk={tankAttackers} myHP={p.World.Hpp}% mp={p.World.Mpp}% lvl={p.World.MainJobLevel} exp={p.World.ExpNow}/{p.World.ExpNext}");

            if (tankHp > 0) sawTankAlive = true;
            // (Dead tank is Reunion's job — it distinguishes dead from zoned-away via 0x0DD ZoneNo. The legacy
            // Reunion-less regroup that lived here, including a Raise-the-tank path the doctrine forbids, was
            // removed as dead code duplicating Reunion.)
            // 1) CURE THE TANK below the threshold (60% per the user — topping a healthy WAR above that is
            //    wasted MP; below it, curing outranks everything).
            if (haveMp && canHeal && tankHp > 0 && tankHp < cfg.CureTankBelow)
            {
                if (!tankInRange) { nav.Follow(cfg.TankId); await Task.Delay(500, ct); continue; }
                await StandCast(heal, cfg.TankId, $"{heal} tank (HP {tankHp}%)"); continue;
            }
            // 2) DIA + PARALYZE the foe EARLY, once per foe — they shorten the fight and cut incoming damage,
            //    which saves more curing than one topping-cure delivers. They used to sit BELOW the 90% cure
            //    threshold, and since a tanking WAR is almost never above 90% mid-fight, the cure-continue loop
            //    starved them out completely (Paralyze was Known for 3 days and never cast once — user report,
            //    confirmed by the spells-known diagnostic).
            if (cfg.Enfeeble && haveMp && tankAttackers > 0)
            {
                var foe0 = p.Nearest(e => e.IsMob && e.Hpp > 0 && p.DistanceTo(e.X, e.Z) <= CastRange);
                if (foe0 != null)
                {
                    bool castOne = false;
                    foreach (var (line, every) in new[] { (SpellLine.Dia, 60_000L), (SpellLine.Paralyze, 90_000L) })
                        if (magic.BestReady(line) is { } sp
                            && (!castMs.TryGetValue((sp, foe0.Id), out var eLast) || p.World.NowMs - eLast > every))
                        {
                            castMs[(sp, foe0.Id)] = p.World.NowMs;
                            await StandCast(sp, foe0.Id, $"{sp} on {foe0.Name} (early)");
                            castOne = true; break;
                        }
                    if (castOne) continue;
                }
            }
            // 3) SELF-CURE when low + safe.
            if (haveMp && canHeal && p.World.Hpp > 0 && p.World.Hpp < cfg.CureSelfBelow)
            { await StandCast(heal, p.World.MyId, $"{heal} self (HP {p.World.Hpp}%)"); continue; }

            // 3) BUFFS — keep Protect + Shell on tank + self (recast on a long timer; they last ~30 min),
            //    best known tier via the line selector.
            if (cfg.Buff && haveMp && tankInRange)
            {
                bool buffed = false;
                foreach (var (line, who) in new[] { (SpellLine.Protect, cfg.TankId), (SpellLine.Shell, cfg.TankId), (SpellLine.Protect, p.World.MyId), (SpellLine.Shell, p.World.MyId) })
                    if (magic.BestReady(line) is { } sp
                        && (!castMs.TryGetValue((sp, who), out var last) || p.World.NowMs - last > 1_500_000L))
                    { castMs[(sp, who)] = p.World.NowMs; await StandCast(sp, who, $"{sp} on 0x{who:X}"); buffed = true; break; }
                if (buffed) continue;
            }
            // (The old #4 late-enfeeble block was a verbatim copy of #2 under identical conditions — never
            // reachable with anything to cast, and its unconditional `continue` starved the critical-OOM rest
            // whenever a foe stood within cast range. Deleted.)
            // 5) REST MP — ONLY between fights (no mob on the tank) with the tank topped, so we're NEVER seated
            //    while the WAR is being beaten (the rest-lock bug). Trigger at the REST TARGET, not a lower
            //    floor: a floor of 20 with the tank's ready-gate at 30 left MP parked at 29% in the dead band
            //    forever (no passive MP regen in FFXI) — tank waiting on MP the healer would never rest for.
            //    Rest whenever idle-safe and below target; the tank's own gate waits for us, so this can't strand it.
            //    The tank-topped condition only applies while we CAN still cure — OOM (below cast MP) means
            //    sitting is strictly better than standing, else post-fight mp=3% + tank at 80% three-way
            //    stalled the duo (can't cure, won't sit, tank won't pull).
            // Tank-safe = above the CURE line (not "topped"): with curing now starting at 60%, a tank riding
            // 60-89 is by-design uncured, and demanding >=90 here meant the WHM could never sit — MP never
            // recovered and the tether rallied in a loop.
            // CRITICALLY OOM (<15%): rest EVEN DURING the tank's fights — a chain-pulling tank never has zero
            // attackers, so the old gate starved the healer at 4% MP for hours (zero cures; it finally died
            // with full HP standing beside a winning tank). Below cure-capability, sitting is strictly better.
            bool criticalOom = p.World.Mpp < 15;
            if (p.World.Mpp < cfg.RestMpTo && !beingHit && (tankAttackers == 0 || criticalOom) && (tankHp >= cfg.CureTankBelow || !haveMp || criticalOom))
            {
                // NEVER sit with a live mob near US: every overnight WHM KO followed a "resting MP — no mobs
                // on tank" line — the gate watched the TANK's attackers while an aggressive wanderer walked
                // onto the seated healer (seated at 1% MP = dead). Step DIRECTLY AWAY from the offender first,
                // same as the grind loop's rest (we only choose the destination; nav walks it).
                for (int s = 0; s < 3 && !ct.IsCancellationRequested; s++)
                {
                    var near = p.Nearest(e => e.IsMob && e.Hpp > 0 && p.DistanceTo(e.X, e.Z) < 16f);
                    if (near is null) break;
                    // A mob this close has usually already noticed us — stepping AWAY just means dying alone
                    // (every recent WHM KO was a rest attempt with a wanderer 2-9y out). Rest AT THE TANK:
                    // the peel duty yanks anything that follows. Step away only when the tank isn't visible.
                    if (p.World.Entities.TryGetValue(cfg.TankId, out var tankEnt) && tankEnt is not null && (tankEnt.X != 0 || tankEnt.Z != 0))
                    {
                        Log.Info($"[{cfg.Tag}] '{near.Name}' {p.DistanceTo(near.X, near.Z):F0}y away — resting AT the tank (peel range)");
                        nav.MoveTo(tankEnt.X, tankEnt.Z);
                    }
                    else
                    {
                        if (!RestRoutines.StepAway(nav, p, near.X, near.Z, 18f)) break;   // pinned against unwalkable ground — sit rather than freeze
                        Log.Info($"[{cfg.Tag}] '{near.Name}' {p.DistanceTo(near.X, near.Z):F0}y away — stepping off before resting");
                    }
                    for (int w = 0; w < 12 && nav.IsMoving && !ct.IsCancellationRequested; w++) await Task.Delay(500, ct);
                }
                Log.Info($"[{cfg.Tag}] resting MP ({p.World.Mpp}%) — no mobs on tank");
                nav.Stop();
                // Abort the rest THE MOMENT the tank acquires attackers — UNLESS we're critically OOM (then
                // only a genuine tank emergency, <35% HP, or an attack on US stands us up: aborting for every
                // routine pull is what kept MP pinned at 4%).
                await combat.Rest(0, cfg.RestMpTo,
                    () => (p.World.Mpp >= 15 && p.AttackersOn(cfg.TankId, 12000) > 0)
                          || (p.World.PartyMembers.TryGetValue(cfg.TankId, out var tp) && tp.Hpp is > 0 and < 35)
                          || p.AttackersOn(p.World.MyId, 8000) > 0, ct);
                continue;
            }

            // 6) Nothing to cast — hold in heal range (FollowBuffer behind the tank), or drift to camp if the tank
            //    isn't visible. Stand otherwise.
            if (tankVisible && tankDist > cfg.FollowBuffer) nav.Follow(cfg.TankId);
            else nav.Stop();
            await Task.Delay(900, ct);
        }
    }
}
