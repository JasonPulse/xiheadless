using XiHeadless.Game;

namespace XiHeadless.Routines;

/// The default in-party GRIND BEAT for a fleet job brain (plugged into FleetDay.PartyGrind): the camp
/// doctrine made executable. Members hold their ROLE STATION between fights (casters at the bard-announced
/// caster camp — see PartyCombat stations), the puller brings mobs TO camp (bard: song pass first, Elegy
/// pull, Lullaby at camp), and everyone kills the camp mob through the ONE shared KillRoutine. Healers top
/// the party between fights. Movement is destinations-only; all mechanics stay in KillRoutine/NavRoutines.
public sealed class PartyGrind(IPerception p, ICombat combat, IMagic? magic, INavigation nav, IGear gear,
                               IChat chat, LevelGrind.Config g, string tag)
{
    (float x, float z)? _camp;    // the announcer's own anchor (first-beat position = the meet spot)
    // The shared per-entity con cache (cleared on level-up): a mob judged out of band (-1 no reply, too weak,
    // too tough) is never re-picked until we level. Without it the puller re-/checked the NEAREST mob every
    // beat: -1 objects thousands of times (Grabu/Kougrou, 2026-08-30), then a con-5 Goblin_Gambler 6,000x
    // in one party day while the in-band cranes behind it got 0 kills (Gamae, 2026-10-05).
    readonly Dictionary<uint, long> _grabFailMs = new();   // mob -> when a pull on it failed or was dirty (3-min cool-off)
    readonly RoamController _cons = new(nav, p, combat, new RoamController.Config { ConMin = 1, ConMax = g.ConMax, Tag = tag });
    long _dryPullMs, _gateLogMs, _scanLogMs;  // dry-pull log throttle; gate log throttle
    int _pullHeading;             // rotating roam-out heading (deg) for finding mobs beyond view range
    ((float x, float z) camp, (float x, float z) casters)? _myStations;   // announcer's SELF-VIEW: a bot
                                                                          // never receives its own party
                                                                          // line, so it must remember what
                                                                          // it announced (live: 6,081 CAMP
                                                                          // re-announces in one session)
    long _annMs, _songMs;

    public async Task Beat(PartyCombat.PullPlan plan, CancellationToken ct)
    {
        if (combat.Dead) { await Task.Delay(2000, ct); return; }   // the core death rule owns recovery
        var w = p.World;
        bool iAmPuller = plan.Puller.Equals(w.MyName, StringComparison.OrdinalIgnoreCase);
        var role = MyRole(plan);

        // Stations: the PULLER owns the geometry and announces on a strict cadence; everyone (announcer
        // included) reads back ONE source — the announced camp — falling back to the announcer's memory.
        var st = PartyCombat.Stations(p) ?? _myStations;
        if (iAmPuller && w.NowMs - _annMs > PartyCombat.StationAnnounceEveryMs)
        {
            _camp ??= SafeCamp((w.X, w.Z));
            var casters = PartyCombat.DeriveCasterStation(_camp.Value, PullLaneProbe(_camp.Value));
            PartyCombat.AnnounceStations(chat, _camp.Value, casters);
            _annMs = w.NowMs;
            st = _myStations = (_camp.Value, casters);
        }
        // MEMBERS anchor on the ANNOUNCED camp — never their own first-beat position (live: members
        // camped where THEY stood at formation, never saw the puller's camp mob, and scored 0 kills).
        var camp = st?.camp ?? (_camp ??= (w.X, w.Z));

        // A mob at camp fighting the party -> play the role on it.
        if (CampMob(camp) is { } mob)
        {
            if (role == PartyRoles.Role.Healer)
            {
                if (await HealPass(ct)) return;   // cures outrank swings
                // Healers don't melee: swinging plus curing pulls hate off the tank. The RDM healer engaged
                // the first Rolanberry pull, took the wasp's hate and died (2026-10-07). Hold the station and
                // only fight what is actually hitting us.
                if (KillRoutine.AttackerOnMe(p)?.Id != mob.Id)
                {
                    var post = MyStation(role, camp, st);
                    if (p.DistanceTo(post.x, post.z) > 5f)
                        await NavRoutines.WalkTo(nav, p, post.x, post.z, within: 3f, ct, legTimeoutMs: 10_000, defend: Defend);
                    await Task.Delay(1000, ct);
                    return;
                }
            }
            await KillRoutine.Fight(combat, p, nav, gear, mob, fightCon: 3, new KillRoutine.Hooks
            {
                // The party's TANK holds hate: Provoke whenever it's up, then the job kit. Provoke lived only in
                // the PLD kit, so a voted NIN tank pulled, then let the RNG eat the wasp to 13% (2026-10-07).
                // PEEL first: a mob hitting a party member gets the Provoke, not our own target (the PLD
                // provoked its bat while a Midnight_Wings killed the RDM healer, 2026-10-07).
                UseAbilities = role == PartyRoles.Role.Tank
                    ? async (m, c2, t) =>
                    {
                        var loose = LooseOnMember();
                        if (await combat.UseAbility(Ability.Provoke, loose?.Id ?? m, t))
                        { Log.Info($"[{tag}] Provoke {(loose is null ? "(hold)" : $"peels '{loose.Name}' off a member")}"); return; }
                        await g.UseAbilities(m, c2, t);
                    }
                    : g.UseAbilities,
                EmergencyHeal = g.EmergencyHeal,
                WepSkillForLevel = g.WepSkillForLevel, Tag = tag,
            }, breakOffHpp: 0, ct);
            return;
        }

        // Between fights: heal pass, then puller pulls / members hold station and rest.
        if (role == PartyRoles.Role.Healer && await HealPass(ct)) return;
        if (iAmPuller) { await PullNext(camp, st, ct); return; }

        var mine = MyStation(role, camp, st);
        if (p.DistanceTo(mine.x, mine.z) > 5f)
            await NavRoutines.WalkTo(nav, p, mine.x, mine.z, within: 3f, ct, legTimeoutMs: 20_000, defend: Defend);
        else if (w.Hpp <= PartyCombat.ReadyHpp || w.Hpp < g.RestHpTrigger || (g.RestMpPct > 0 && w.Mpp < g.RestMpPct))
            await combat.Rest(Math.Max(g.RestHpTarget, PartyCombat.ReadyHpp + 10), g.RestMpPct,
                () => p.AttackersOn(w.MyId, 8000) > 0, ct);   // members rest ABOVE the ready line — never park under the puller's gate
        await Task.Delay(1500, ct);
    }

    // The puller's camp: the meet spot moved off spawn ground (PartyCombat.SafeCampSpot), reachability by the navmesh.
    (float x, float z) SafeCamp((float x, float z) meet)
    {
        var (spot, clear) = PartyCombat.SafeCampSpot(p.World.ZoneId, meet, (x, z) => nav.CanReach(x, p.World.Y, z));
        if (spot != meet) Log.Info($"[{tag}] meet spot is spawn ground — camping at ({spot.x:F0},{spot.z:F0}), {Geometry.Dist2D(spot.x, spot.z, meet.x, meet.z):F0}y away, {clear:F0}y clear of spawns");
        else if (clear < PartyCombat.CampClearYalms) Log.Info($"[{tag}] no spawn-clear camp within 120y of the meet spot — camping on it ({clear:F0}y from a spawn)");
        return spot;
    }

    // A mob within Provoke range (~16y) that is hitting a party member other than us: the tank's peel target.
    Entity? LooseOnMember()
    {
        var w = p.World;
        return p.Nearest(e => e.IsMob && e.Hpp > 0 && p.DistanceTo(e.X, e.Z) <= 16f
            && w.Attackers.TryGetValue(e.Id, out var a) && w.NowMs - a.ms < 6000
            && a.target != w.MyId && w.PartyMembers.ContainsKey(a.target));
    }

    // The role THIS member plays in THIS party, the same staffing the comp gate and puller vote use: the voted
    // tank tanks; the party's healer is its WHM, else the first healer-capable member (RDM/SCH) by name. A job's
    // primary role alone made the party's only healer (a RDM, primary DD) melee the camp mob.
    PartyRoles.Role _role; long _roleMs = -1;
    PartyRoles.Role MyRole(PartyCombat.PullPlan plan)
    {
        var w = p.World;
        if (_roleMs >= 0 && w.NowMs - _roleMs < 15_000) return _role;
        _roleMs = w.NowMs;
        var roster = PartyCombat.Roster(p);
        var healer = roster.Where(kv => PartyRoles.PrimaryOf(kv.Value) == PartyRoles.Role.Healer)
                           .Concat(roster.Where(kv => PartyRoles.CanFillOf(kv.Value).HasFlag(PartyRoles.Role.Healer))
                                         .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                           .Select(kv => kv.Key).FirstOrDefault();
        var primary = PartyRoles.PrimaryOf(w.MainJob);
        _role = plan.Tank is { } t && t.Equals(w.MyName, StringComparison.OrdinalIgnoreCase) ? PartyRoles.Role.Tank
              : primary == PartyRoles.Role.Healer || (healer?.Equals(w.MyName, StringComparison.OrdinalIgnoreCase) ?? false) ? PartyRoles.Role.Healer
              : primary == PartyRoles.Role.Tank ? PartyRoles.Role.Dps   // a second tank who wasn't voted swings as DD
              : primary;
        return _role;
    }

    // Fight back if jumped mid-walk (members heading to station): the one shared KillRoutine defense.
    Task<bool> Defend(CancellationToken ct) => KillRoutine.DefendSelf(combat, p, nav, gear, new KillRoutine.Hooks
    {
        UseAbilities = g.UseAbilities, EmergencyHeal = g.EmergencyHeal, WepSkillForLevel = g.WepSkillForLevel, Tag = tag,
    }, ct);

    // Casters + healers sit at the announced caster camp; everyone else holds the melee camp.
    static (float x, float z) MyStation(PartyRoles.Role role, (float x, float z) camp,
                                        ((float x, float z) camp, (float x, float z) casters)? st) =>
        st is { } s && role is PartyRoles.Role.Healer or PartyRoles.Role.Support ? s.casters : camp;

    // The mob the party is fighting AT CAMP: close to the anchor and either already damaged or actively
    // attacking a party member (never a random full-HP wanderer nobody has hate on).
    Entity? CampMob((float x, float z) camp) =>
        p.Nearest(e => e.IsMob && e.Hpp > 0 && CombatRoutines.NotObject(e)
            // FRESH only: a mob damaged to <100% that then goes STALE (moved/despawned, entity not
            // updated) otherwise stays a valid CampMob forever, and KillRoutine.Fight bails on it instantly
            // (its own 20s stale guard) -> a party DD re-engaged one dead hare 23,664x, 0 kills (2026-08-03).
            && p.World.NowMs - e.LastSeenMs < 15_000
            && Geometry.Dist2D(e.X, e.Z, camp.x, camp.z) < 15f
            && (e.Hpp < 100 || PartyCombat.ClaimedByParty(p, e) || (p.World.Attackers.TryGetValue(e.Id, out var a)
                && p.World.NowMs - a.ms < 15_000
                && (a.target == p.World.MyId || p.World.PartyMembers.ContainsKey(a.target)))));

    // Cure the lowest-HP party member below 60% (or self) with the best affordable tier — the selector is
    // level-gated, capped at III for MP economy (the PartySupport pattern, party-wide).
    async Task<bool> HealPass(CancellationToken ct)
    {
        if (magic is null || p.World.Mp < 10) return false;
        uint target = 0; byte low = 60;
        if (p.World.Hpp > 0 && p.World.Hpp < low) { target = p.World.MyId; low = p.World.Hpp; }
        foreach (var (id, m) in p.World.PartyMembers.ToArray())
            if (m.Zone == 0 && m.Hpp > 0 && m.Hpp < low && p.World.Entities.TryGetValue(id, out var e)
                && p.DistanceTo(e.X, e.Z) <= 20f)
            { target = id; low = m.Hpp; }
        if (target == 0 || magic.BestReady(SpellLine.Cure, maxTier: 3) is not { } sp) return false;
        nav.Stop();
        await Task.Delay(250, ct);                     // settle — moving interrupts the cast
        Log.Info($"[{tag}] {sp} on 0x{target:X} (HP {low}%)");
        magic.Cast(sp, target);
        await Task.Delay(3200, ct);
        return true;
    }

    // The puller's beat: party-good gate, bard song pass, then bring the next mob home.
    async Task PullNext((float x, float z) camp, ((float x, float z) camp, (float x, float z) casters)? st,
                        CancellationToken ct)
    {
        // NEVER pull until every in-zone member is good (user rule) — hold at camp while they recover.
        foreach (var (_, m) in p.World.PartyMembers.ToArray())
            if (m.Zone == 0 && p.World.NowMs - m.LastSeenMs < 30_000   // stale rows can't hold the party hostage
                && m.Hpp > 0 && m.Hpp < PartyCombat.ReadyHpp)
            {
                if (p.World.NowMs - _gateLogMs > 60_000) { _gateLogMs = p.World.NowMs; Log.Info($"[{tag}] holding pulls — a member is at {m.Hpp}%"); }
                await NavRoutines.WalkTo(nav, p, camp.x, camp.z, within: 3f, ct, legTimeoutMs: 15_000); await Task.Delay(2000, ct); return;
            }

        // Jumped away from camp (roaming aggro, a link): that mob IS the next pull. Drag it home to the party
        // rather than fighting it alone in the field; the camp fight takes it from there.
        if (KillRoutine.AttackerOnMe(p) is { } jumped && Geometry.Dist2D(jumped.X, jumped.Z, camp.x, camp.z) >= 15f)
        {
            Log.Info($"[{tag}] '{jumped.Name}' jumped me {p.DistanceTo(camp.x, camp.z):F0}y out — dragging it to camp");
            await NavRoutines.WalkTo(nav, p, camp.x, camp.z, within: 4f, ct, legTimeoutMs: 60_000);
            return;
        }
        // The puller is a member too: never pull hurt or dry (live: a BLU puller at 19% HP pulled a goblin).
        if (p.World.Hpp < PartyCombat.ReadyHpp || (g.RestMpPct > 0 && p.World.Mpp < g.RestMpPct))
        {
            await NavRoutines.WalkTo(nav, p, camp.x, camp.z, within: 3f, ct, legTimeoutMs: 15_000);
            await combat.Rest(Math.Max(g.RestHpTarget, PartyCombat.ReadyHpp + 10), g.RestMpPct,
                () => p.AttackersOn(p.World.MyId, 8000) > 0, ct);
            return;
        }
        // ...and never pull before every member in the zone is AT camp (user rule: "in range"). A member still
        // walking in from the zone line or back from a home point would otherwise watch the puller fight the
        // pull alone (live: the BRD puller died solo on a crane while the BLU was still on the roster wait).
        foreach (var (id, m) in p.World.PartyMembers.ToArray())
            if (m.Zone == 0 && p.World.NowMs - m.LastSeenMs < 30_000 && m.Hpp > 0
                && !(p.World.Entities.TryGetValue(id, out var me) && Geometry.Dist2D(me.X, me.Z, camp.x, camp.z) <= 25f))
            {
                if (p.World.NowMs - _gateLogMs > 60_000) { _gateLogMs = p.World.NowMs; Log.Info($"[{tag}] holding pulls — a member isn't at camp yet"); }
                await NavRoutines.WalkTo(nav, p, camp.x, camp.z, within: 3f, ct, legTimeoutMs: 15_000); await Task.Delay(2000, ct); return;
            }
        if (p.World.MainJob == Job.Brd && magic is not null) await SongPass(camp, st, ct);

        var target = p.Nearest(e => e.IsMob && e.Hpp == 100 && CombatRoutines.NotObject(e)
            && e.ClaimId == 0                                          // someone else's claim can't be pulled
            && (_cons.KnownCon(e.Id) is not int kc || (kc >= 1 && kc <= g.ConMax))   // out-of-band never re-picked this level
            && (!_grabFailMs.TryGetValue(e.Id, out var gf) || p.World.NowMs - gf > 180_000)   // failed grab: retry later, not every beat
            && !CombatRoutines.SleepLockMobs.Any(n => e.Name.Contains(n, StringComparison.OrdinalIgnoreCase))
            && Geometry.Dist2D(e.X, e.Z, camp.x, camp.z) > 16f      // never the camp bubble; no outer cap —
            && nav.CanReach(e.X, e.Y, e.Z));                          // the puller walks out and drags it home
        if (target is null)
        {
            // PULL TELEMETRY (the third party bottleneck was invisible: 1 pull in 5h with NO log naming
            // the filter that ate every candidate). Every ~60s while dry, count the view through each
            // rejection stage so the blocking filter names itself.
            if (p.World.NowMs - _scanLogMs > 60_000)
            {
                _scanLogMs = p.World.NowMs;
                var mobs = p.World.Entities.Values.Where(e => e.IsMob && e.Hpp > 0).ToArray();
                int fullHp = mobs.Count(e => e.Hpp == 100);
                int eligible = mobs.Count(e => e.Hpp == 100 && CombatRoutines.NotObject(e)
                    && !CombatRoutines.SleepLockMobs.Any(n => e.Name.Contains(n, StringComparison.OrdinalIgnoreCase))
                    && Geometry.Dist2D(e.X, e.Z, camp.x, camp.z) > 16f);
                int reachable = mobs.Count(e => e.Hpp == 100 && Geometry.Dist2D(e.X, e.Z, camp.x, camp.z) > 16f
                    && nav.CanReach(e.X, e.Y, e.Z));
                Log.Info($"[{tag}] PULL-SCAN: view={mobs.Length} fullHP={fullHp} eligible={eligible} reachable={reachable} | roaming heading {_pullHeading}° at {p.DistanceTo(camp.x, camp.z):F0}y from camp");
            }
            // THE CAMP NEVER MOVES (user: camps are deliberately placed OUTSIDE spawn ground so nothing
            // pops on top of the party). Finding prey is the PULLER'S legs: perception only sees ~50y, so
            // roam OUTWARD from camp in rotating headings — hop, scan, hop — as far as it takes (in-game
            // pull runs are long; 40y is nothing), then drag the catch all the way home.
            if (_dryPullMs == 0 || p.World.NowMs - _dryPullMs > 120_000)
            { _dryPullMs = p.World.NowMs; Log.Info($"[{tag}] no prey in view — roaming out from camp to find a pull (heading {_pullHeading}°)"); }
            float fromCamp = p.DistanceTo(camp.x, camp.z);
            if (fromCamp > 250f)
            {
                // This heading came up empty — walk home and fan to the next spoke.
                _pullHeading = (_pullHeading + 60) % 360;
                await NavRoutines.WalkTo(nav, p, camp.x, camp.z, within: 5f, ct, legTimeoutMs: 90_000);
                return;
            }
            float rad = _pullHeading * MathF.PI / 180f;
            float hx = camp.x + MathF.Sin(rad) * (fromCamp + 45f), hz = camp.z + MathF.Cos(rad) * (fromCamp + 45f);
            if (!nav.CanReach(hx, p.World.Y, hz)) { _pullHeading = (_pullHeading + 60) % 360; return; }   // wall — next spoke
            await NavRoutines.WalkTo(nav, p, hx, hz, within: 5f, ct, legTimeoutMs: 45_000);
            return;
        }
        _dryPullMs = 0;
        int con = await _cons.ConsiderCached(target.Id, ct);
        if (con < 1 || con > g.ConMax)
        {
            Log.Info($"[{tag}] pull candidate '{target.Name}' rejected: con={con} (want 1-{g.ConMax})");
            return;   // con is the sole arbiter; the cache keeps it out of selection until we level
        }

        // CLEAN PULL (the shared RoamController gate): no in-band-or-tougher neighbor within 16y of the target.
        // A Moon_Bat pull brought two linked bats and killed the PLD tank under three attackers (2026-10-07).
        if (!await _cons.CleanPull(target, null, ct, dirtyCon: g.ConMax))
        { _grabFailMs[target.Id] = p.World.NowMs; return; }

        Log.Info($"[{tag}] pulling '{target.Name}' (con {con}) at {p.DistanceTo(target.X, target.Z):F0}y from me, {Geometry.Dist2D(target.X, target.Z, camp.x, camp.z):F0}y from camp");
        if (p.DistanceTo(target.X, target.Z) > 14f)
            await NavRoutines.WalkTo(nav, p, target.X, target.Z, within: 13f, ct, legTimeoutMs: 60_000);   // into Provoke/song range
        if (p.World.MainJob == Job.Brd && magic is not null)
            await PartyCombat.BardPull(magic, p, nav, target.Id, camp, ct);
        else if (!await PartyCombat.RangedPull(combat, p, nav, target.Id, camp, ct))
            _grabFailMs[target.Id] = p.World.NowMs;   // unreachable/ungrabbable right now: try another for a while
        await NavRoutines.WalkTo(nav, p, camp.x, camp.z, within: 4f, ct, legTimeoutMs: 30_000);
    }

    // The BARD's two-camp song route (user spec): melee songs sung INSIDE the melee cluster at camp,
    // Ballads at the caster station, then back out to pull. Songs are ~2 min — one pass per cadence.
    async Task SongPass((float x, float z) camp, ((float x, float z) camp, (float x, float z) casters)? st,
                        CancellationToken ct)
    {
        if (p.World.NowMs - _songMs < 120_000) return;
        _songMs = p.World.NowMs;
        await NavRoutines.WalkTo(nav, p, camp.x, camp.z, within: 3f, ct, legTimeoutMs: 15_000);
        foreach (var line in new[] { SpellLine.ValorMinuet, SpellLine.SwordMadrigal })   // melee pair
            if (magic!.CastHighest(line, p.World.MyId)) await Task.Delay(3200, ct);
        if (st is { } s)
        {
            await NavRoutines.WalkTo(nav, p, s.casters.x, s.casters.z, within: 3f, ct, legTimeoutMs: 15_000);
            foreach (var line in new[] { SpellLine.MagesBallad, SpellLine.ArmysPaeon })  // caster pair
                if (magic!.CastHighest(line, p.World.MyId)) await Task.Delay(3200, ct);
        }
    }

    // Where the puller heads for mobs (used to place the caster camp on the OPPOSITE side): the nearest
    // live mob's direction, else an arbitrary fixed bearing.
    (float x, float z) PullLaneProbe((float x, float z) camp) =>
        p.Nearest(e => e.IsMob && e.Hpp > 0 && Geometry.Dist2D(e.X, e.Z, camp.x, camp.z) > 12f) is { } m
            ? (m.X, m.Z) : (camp.x, camp.z + 30f);
}
