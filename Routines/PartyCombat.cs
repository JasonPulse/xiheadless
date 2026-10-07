using XiHeadless.Game;

namespace XiHeadless.Routines;

/// Party COMBAT doctrine (user spec, 2026-07-08). All of this happens IN the level-appropriate hunt zone —
/// bots travel there FIRST (HuntZonePlan, the leveling guide) and only then form/run the party.
///
///   * CAMP vs ROAM: a party of MORE than 3 anchors at a CAMP — only the puller leaves; everyone else holds
///     position (rest/buff between kills). A party of exactly 3 ROAMS like the solo grind (with party gating).
///   * PULLER SELECTION:
///       1. BRD in party -> ALWAYS the puller. Pull-and-sleep preferred: sing at the next mob while the
///          current one dies, lullaby it at camp — zero-downtime chains.
///       2. THF (Trick Attack, lv30+) AND a second tank-capable member -> the SUB-TANK pulls, and the kill
///          opens with the SATA line: subtank - mob - TANK - thief. The mob faces its puller (the sub-tank),
///          so the TANK stands at the mob's BACK, and the THF stands behind the TANK: Sneak Attack + Trick
///          Attack (+ WeaponSkill when TP allows) through the tank plants all that hate ON the tank.
///       3. Otherwise the MAIN TANK pulls — never by walking into melee: Provoke at range if available
///          (WAR main/sub), else a ranged Shoot with the non-expendable boomerang — so the mob chases the
///          tank home instead of beating on it during the drag back.
///   * JOB ROSTER: bots can't see each other's jobs, so each announces "JOB <token> <level>" on party chat
///     when it joins (and re-announces periodically). Humans never announce — they're simply never assigned
///     puller/SATA duty, which is exactly right for OPEN parties.
public static class PartyCombat
{
    public const int CampThreshold = 4;        // total members (incl. self) >= this -> camp mode; 3 -> roam
    // THE party-ready line — the puller's all-members-good gate AND every member's between-fight rest use
    // this ONE constant. (Live deadlock: puller required >=70 while a DRG's own rest trigger was 50 — a
    // member at 68% never rested and never crossed 70, freezing the party for its entire session.)
    public const byte ReadyHpp = 70;
    const int AnnounceEveryMs = 180_000;

    // ---- job roster over the party bus ----------------------------------------------------------------

    /// Announce our job on the party bus (call on join + periodically; cheap and idempotent).
    public static void AnnounceJob(IChat chat, IPerception p, ref long lastMs)
    {
        if (p.World.NowMs - lastMs < AnnounceEveryMs) return;
        lastMs = p.World.NowMs;
        chat.Party($"JOB {JobToken(p.World.MainJob)} {p.World.MainJobLevel}");
    }

    /// Roster of announced jobs (name -> job id), read from party chat. Includes ourselves.
    /// Reads the chat history, not just each sender's LAST party line: a member's JOB line was overwritten by
    /// its next line (ENDAT/START/CAMP), dropping it from the roster until the next re-announce.
    public static Dictionary<string, byte> Roster(IPerception p)
    {
        var w = p.World;
        var roster = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        void Take(string sender, string msg)
        {
            if (!msg.StartsWith("JOB ", StringComparison.OrdinalIgnoreCase)) return;
            var parts = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && PartyRoles.ParseJobToken(parts[1]) is var j and > 0) roster[sender] = j;
        }
        ChatLine[] log;
        try { log = w.ChatLog.ToArray(); } catch (ArgumentException) { log = []; }   // receive thread appends
        foreach (var c in log) if (c.Kind is 4 or 15) Take(c.Sender, c.Message);     // oldest -> newest
        foreach (var (sender, (msg, _)) in w.PartyChat.ToArray()) Take(sender, msg);   // latest line wins
        roster[w.MyName] = w.MainJob;
        return roster;
    }

    // ---- puller selection -------------------------------------------------------------------------------

    public enum PullStyle : byte { BardSleep, SataSubTank, TankRanged }
    public readonly record struct PullPlan(string Puller, PullStyle Style, string? Tank, string? Thief);

    /// Decide the puller + style from the announced roster. Deterministic — every bot computes the same plan.
    public static PullPlan DecidePuller(Dictionary<string, byte> roster)
    {
        string? First(Func<byte, bool> match) =>
            roster.Where(kv => match(kv.Value)).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                  .Select(kv => kv.Key).FirstOrDefault();

        var tank = First(j => PartyRoles.PrimaryOf(j) == PartyRoles.Role.Tank)
                ?? First(j => PartyRoles.CanFillOf(j).HasFlag(PartyRoles.Role.Tank));

        // 1. BRD always pulls.
        if (First(j => j == Job.Brd) is { } bard) return new(bard, PullStyle.BardSleep, tank, null);

        // 2. THF + a HEAVY physical DD (WAR/MNK/DRK/SAM/DRG — the sub-tank must survive holding the mob's
        //    face during the pull) = SATA formation: the sub-tank pulls, SA+TA plants the hate on the tank.
        var thief = First(j => j == Job.Thf);
        if (thief is not null && tank is not null)
        {
            var subTank = roster.Where(kv => PartyRoles.IsHeavyDd(kv.Value)
                                             && !kv.Key.Equals(tank, StringComparison.OrdinalIgnoreCase))
                                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                                .Select(kv => kv.Key).FirstOrDefault();
            if (subTank is not null) return new(subTank, PullStyle.SataSubTank, tank, thief);
        }

        // 3. No BRD and no SATA line: a NON-CASTER pulls. Tank first (Provoke), else the first physical/ranged
        //    DD (it shoots or melee-tags the mob home). A CASTER (SMN/WHM/BLM/RDM/SCH/GEO) NEVER pulls: it is a
        //    fragile back-line job that summons/nukes/heals from camp. A SMN voted puller pulled 700x and killed
        //    nothing (user 2026-09-16). An all-caster roster has no valid puller (Puller = "" -> nobody pulls);
        //    such a party never should have started without a tank/physical DD.
        var puller = tank ?? First(j => !JobKits.CastsPrimary(j));
        return new(puller ?? "", PullStyle.TankRanged, tank, thief);
    }

    // ---- role stations (user spec 2026-07-14) -----------------------------------------------------------
    // The BARD (or whoever pulls) OWNS the camp geometry and ANNOUNCES it on party chat — ONE source of
    // truth. Members never derive their own spot (six casters each "choosing" would ring the camp), and in
    // an OPEN party real players read the same line to see where the bard wants them. Songs are self-AoE:
    // melee songs are sung inside the melee cluster at the mob camp, Ballads at the caster camp.

    public const float CasterCampYalms = 18f;
    public const long StationAnnounceEveryMs = 240_000;

    /// Announce both stations (puller/bard only) — parseable AND readable by human party members.
    public static void AnnounceStations(IChat chat, (float x, float z) camp, (float x, float z) casters)
        => chat.Party($"CAMP {camp.x:F0} {camp.z:F0} CASTERS {casters.x:F0} {casters.z:F0}");

    /// The ANNOUNCER's caster-camp derivation: CasterCampYalms from camp, directly AWAY from the pull lane
    /// (the direction the puller leaves toward the mobs) so casters never sit in an incoming mob's path.
    public static (float x, float z) DeriveCasterStation((float x, float z) camp, (float x, float z) pullLane)
    {
        float dx = camp.x - pullLane.x, dz = camp.z - pullLane.z;
        float len = MathF.Max(0.5f, Geometry.Dist2D(camp.x, camp.z, pullLane.x, pullLane.z));
        return (camp.x + dx / len * CasterCampYalms, camp.z + dz / len * CasterCampYalms);
    }

    /// Latest stations announced on party chat (usually by the bard; includes our own). Null = none yet —
    /// members then hold the plain camp.
    public static ((float x, float z) camp, (float x, float z) casters)? Stations(IPerception p)
    {
        ((float, float), (float, float))? best = null; long bestMs = -1;
        foreach (var (_, (msg, ms)) in p.World.PartyChat.ToArray())
        {
            var m = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (m.Length >= 6 && m[0].Equals("CAMP", StringComparison.OrdinalIgnoreCase)
                && m[3].Equals("CASTERS", StringComparison.OrdinalIgnoreCase)
                && float.TryParse(m[1], out var cx) && float.TryParse(m[2], out var cz)
                && float.TryParse(m[4], out var kx) && float.TryParse(m[5], out var kz)
                && ms > bestMs)
            { best = ((cx, cz), (kx, kz)); bestMs = ms; }
        }
        return best;
    }

    // ---- pull execution ---------------------------------------------------------------------------------

    /// The pull: GRAB the mob, CONFIRM it is ours, then drag it home without outrunning it. Escalates through
    /// the puller's tools until the server shows the claim: the job's hate JA (Provoke/Jump) from range, then a
    /// ranged Shot, then a MELEE TAG from inside melee range. The old version fired these blind and walked
    /// home regardless: an Engage from 13y never swings, so a COR/RDM puller (no Provoke, no ammo) logged 219
    /// "pulling" lines in one party day with 0 grabs and 0 kills (Gamae, 2026-10-05). Returns false when the
    /// grab never landed, so the caller can move on instead of dragging nothing home.
    public static async Task<bool> RangedPull(ICombat combat, IPerception p, INavigation nav, uint mobId,
                                              (float x, float z) camp, CancellationToken ct)
    {
        bool Grabbed() => p.World.Entities.TryGetValue(mobId, out var m)
            && (ClaimedByParty(p, m) || m.Hpp < 100
                || (p.World.Attackers.TryGetValue(mobId, out var a) && a.target == p.World.MyId));
        async Task<bool> Await(int ms)
        {
            for (int t = 0; t < ms && !Grabbed() && !ct.IsCancellationRequested; t += 250) await Task.Delay(250, ct);
            return Grabbed();
        }

        bool got = await combat.UseAbility(PullAbilityFor(p.World.MainJob), mobId, ct) && await Await(2500);
        if (!got) { combat.RangedAttack(mobId); got = await Await(3000); }   // RNG/COR shoot from range
        if (!got)
        {
            // MELEE TAG: close to swing range and hit it once. Hate is what makes it follow; one connect does it.
            await combat.Engage(mobId, ct);
            for (int t = 0; t < 20_000 && !Grabbed() && !ct.IsCancellationRequested; t += 250)
            {
                if (p.World.Entities.GetValueOrDefault(mobId) is not { } m || m.Hpp == 0) break;
                if (p.DistanceTo(m.X, m.Z) > 2.5f) nav.Follow(mobId); else { nav.Stop(); nav.Face(mobId); }
                await Task.Delay(250, ct);
            }
            nav.Stop();
            got = Grabbed();
        }
        if (combat.Engaged) combat.Disengage();   // walk home un-engaged; the hate keeps it chasing
        if (!got) { Log.Info($"[pull] grab on 0x{mobId:X} never landed"); return false; }

        // DRAG: walk straight home, then give it time to arrive. Never stall en route waiting for it: a mob that
        // hangs back (ledge, slow pathing, TP moves from range) kept the puller standing still taking hits from
        // 67% to 19% with no swing back (Drusho, 2026-10-07). Hate keeps it coming; the party engages at camp.
        await NavRoutines.WalkTo(nav, p, camp.x, camp.z, within: 4f, ct, legTimeoutMs: 60_000);
        for (int t = 0; t < 20_000 && !ct.IsCancellationRequested; t += 250)
        {
            if (p.World.Entities.GetValueOrDefault(mobId) is not { } m || m.Hpp == 0) break;
            if (Geometry.Dist2D(m.X, m.Z, camp.x, camp.z) < 12f) break;   // arrived: the camp fight takes it from here
            await Task.Delay(250, ct);
        }
        return true;
    }

    /// The mob's server claim owner is us or a party member (0x00E @0x2C). Ground truth for "the pull landed"
    /// and for "this camp mob is ours", where hate packets and HP% are only indirect hints.
    public static bool ClaimedByParty(IPerception p, Entity e) =>
        e.ClaimId != 0 && (e.ClaimId == p.World.MyId || p.World.PartyMembers.ContainsKey(e.ClaimId));

    /// The PULL ability each puller job grabs with, its OWN tool rather than a borrowed one. Provoke (WAR native
    /// + every /WAR sub) is the enmity pull for 13 of the 15 fleet puller jobs. DRG leads with Jump, a native
    /// gap-closer that lands hate on contact (Chi Blast/Quick Draw are lvl 40+, out of the fleet's band). RNG/COR
    /// have no low hate JA but carry a ranged weapon, and BST (/WHM, no Provoke) has neither, so both fall to the
    /// RangedPull Shoot-then-melee-tag path. (user 2026-09-16)
    static Ability PullAbilityFor(byte job) => job == Job.Drg ? Ability.Jump : Ability.Provoke;

    /// The BARD pull (user spec): pull with ELEGY (slow — hate + a debuff that matters all fight), walk home,
    /// and once the mob has chased back TO CAMP, cast Foe Lullaby — it sleeps AT the camp until the party
    /// engages on their schedule. Pull-and-sleep chaining, zero camp downtime.
    public static async Task BardPull(IMagic magic, IPerception p, INavigation nav, uint mobId,
                                      (float x, float z) camp, CancellationToken ct)
    {
        // Line selectors with Ready gating — raw Cast on an unknown song is the historic silent no-op.
        // Elegy (BRD 39) is the doctrine pull; a younger bard establishes hate with Requiem instead.
        if (!magic.CastHighest(SpellLine.BattlefieldElegy, mobId) && !magic.CastHighest(SpellLine.FoeRequiem, mobId))
        { Log.Info("[brd-pull] no pull song castable — aborting pull"); return; }
        await Task.Delay(3500, ct);              // song cast time — let it land before walking
        nav.MoveTo(camp.x, camp.z);
        // Wait for the mob to arrive near camp (it chases us), then sleep it there.
        for (int t = 0; t < 30_000 && !ct.IsCancellationRequested; t += 500)
        {
            var mob = p.World.Entities.GetValueOrDefault(mobId);
            if (mob is null) return;                                   // lost/killed en route
            float dx = mob.X - camp.x, dz = mob.Z - camp.z;
            if (dx * dx + dz * dz < 12f * 12f) break;                   // mob is at camp
            await Task.Delay(500, ct);
        }
        if (magic.CastHighest(SpellLine.FoeLullaby, mobId))   // sleep it AT camp (BRD 17+; gated, not blind)
            await Task.Delay(2500, ct);
    }

    // ---- SATA choreography --------------------------------------------------------------------------------

    /// The THF's SATA opener once the mob is engaged on the puller at camp: position on the far side of the
    /// TANK from the mob (the line mob->tank, extended), then Sneak Attack + Trick Attack + WS (TP >= 1000)
    /// or a normal swing — the hate lands on the tank through whom we struck. The mob faces the puller, so
    /// tank + thief are at its back (Sneak Attack lands too).
    public static async Task SataOpener(ICombat combat, IPerception p, INavigation nav,
                                        uint mobId, uint tankId, CancellationToken ct)
    {
        var mob = p.World.Entities.GetValueOrDefault(mobId);
        var tank = p.World.Entities.GetValueOrDefault(tankId);
        if (mob is null || tank is null) return;

        // Stand 2y behind the tank on the mob->tank line: pos = tank + normalize(tank - mob) * 2.
        float dx = tank.X - mob.X, dz = tank.Z - mob.Z;
        float len = MathF.Max(0.5f, Geometry.Dist2D(tank.X, tank.Z, mob.X, mob.Z));
        await NavRoutines.WalkTo(nav, p, tank.X + dx / len * 2f, tank.Z + dz / len * 2f, within: 1f, ct, legTimeoutMs: 6_000);

        await combat.UseAbility(Ability.SneakAttack, mobId, ct);
        await combat.UseAbility(Ability.TrickAttack, mobId, ct);
        // THF rides daggers (skill type 2); the shared selector picks the strongest unlocked WS.
        if (combat.CanWeaponSkill && CombatRoutines.BestWeaponSkill(2, p.World.SkillLevel(2)) is { } ws)
            await combat.WeaponSkill(ws, mobId, ct);
        else
            await combat.Engage(mobId, ct);   // SA/TA ride the next swing
    }

    /// The TANK's SATA station: stand at the mob's BACK (opposite the puller it faces), facing the mob,
    /// so the thief's TA line and the mob's rear arc are both satisfied ("the tank must be facing the mob's back").
    public static async Task TankSataStation(IPerception p, INavigation nav, uint mobId, uint pullerId, CancellationToken ct)
    {
        var mob = p.World.Entities.GetValueOrDefault(mobId);
        var puller = p.World.Entities.GetValueOrDefault(pullerId);
        if (mob is null || puller is null) return;
        float dx = mob.X - puller.X, dz = mob.Z - puller.Z;    // direction puller -> mob, extended past the mob = its back side
        float len = MathF.Max(0.5f, Geometry.Dist2D(mob.X, mob.Z, puller.X, puller.Z));
        await NavRoutines.WalkTo(nav, p, mob.X + dx / len * 2f, mob.Z + dz / len * 2f, within: 1f, ct, legTimeoutMs: 6_000);
        nav.Face(mobId);
    }

    static string JobToken(byte j) => Game.PartyRoles.NameOf(j);
}
