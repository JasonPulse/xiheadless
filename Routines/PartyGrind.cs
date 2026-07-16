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
        var role = PartyRoles.PrimaryOf(w.MainJob);

        // Stations: the PULLER owns the geometry and announces on a strict cadence; everyone (announcer
        // included) reads back ONE source — the announced camp — falling back to the announcer's memory.
        var st = PartyCombat.Stations(p) ?? _myStations;
        if (iAmPuller && w.NowMs - _annMs > PartyCombat.StationAnnounceEveryMs)
        {
            _camp ??= (w.X, w.Z);
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
            if (role == PartyRoles.Role.Healer && await HealPass(ct)) return;   // cures outrank swings
            await KillRoutine.Fight(combat, p, nav, gear, mob, fightCon: 3, new KillRoutine.Hooks
            {
                UseAbilities = g.UseAbilities, EmergencyHeal = g.EmergencyHeal,
                WepSkillForLevel = g.WepSkillForLevel, Tag = tag,
            }, breakOffHpp: 0, ct);
            return;
        }

        // Between fights: heal pass, then puller pulls / members hold station and rest.
        if (role == PartyRoles.Role.Healer && await HealPass(ct)) return;
        if (iAmPuller) { await PullNext(camp, st, ct); return; }

        var mine = MyStation(role, camp, st);
        if (p.DistanceTo(mine.x, mine.z) > 5f)
            await NavRoutines.WalkTo(nav, p, mine.x, mine.z, within: 3f, ct, legTimeoutMs: 20_000);
        else if (w.Hpp < g.RestHpTrigger || (g.RestMpPct > 0 && w.Mpp < g.RestMpPct))
            await combat.Rest(g.RestHpTarget, g.RestMpPct, () => p.AttackersOn(w.MyId, 8000) > 0, ct);
        await Task.Delay(1500, ct);
    }

    // Casters + healers sit at the announced caster camp; everyone else holds the melee camp.
    static (float x, float z) MyStation(PartyRoles.Role role, (float x, float z) camp,
                                        ((float x, float z) camp, (float x, float z) casters)? st) =>
        st is { } s && role is PartyRoles.Role.Healer or PartyRoles.Role.Support ? s.casters : camp;

    // The mob the party is fighting AT CAMP: close to the anchor and either already damaged or actively
    // attacking a party member (never a random full-HP wanderer nobody has hate on).
    Entity? CampMob((float x, float z) camp) =>
        p.Nearest(e => e.IsMob && e.Hpp > 0 && CombatRoutines.NotObject(e)
            && Geometry.Dist2D(e.X, e.Z, camp.x, camp.z) < 15f
            && (e.Hpp < 100 || (p.World.Attackers.TryGetValue(e.Id, out var a)
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
            if (m.Zone == 0 && m.Hpp is > 0 and < 70)
            { await NavRoutines.WalkTo(nav, p, camp.x, camp.z, within: 3f, ct, legTimeoutMs: 15_000); await Task.Delay(2000, ct); return; }

        if (p.World.MainJob == Job.Brd && magic is not null) await SongPass(camp, st, ct);

        var target = p.Nearest(e => e.IsMob && e.Hpp == 100 && CombatRoutines.NotObject(e)
            && !CombatRoutines.SleepLockMobs.Any(n => e.Name.Contains(n, StringComparison.OrdinalIgnoreCase))
            && Geometry.Dist2D(e.X, e.Z, camp.x, camp.z) is > 16f and < 60f
            && nav.CanReach(e.X, e.Y, e.Z));
        if (target is null) { await Task.Delay(2500, ct); return; }
        int con = await combat.Consider(target.Id, ct);
        if (con < 1 || con > g.ConMax) { await Task.Delay(1000, ct); return; }   // con is the sole arbiter

        Log.Info($"[{tag}] pulling '{target.Name}' (con {con}) to camp");
        if (p.World.MainJob == Job.Brd && magic is not null)
            await PartyCombat.BardPull(magic, p, nav, target.Id, camp, ct);
        else
            await PartyCombat.RangedPull(combat, p, nav, target.Id, camp, ct);
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
