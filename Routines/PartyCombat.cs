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
    public static Dictionary<string, byte> Roster(IPerception p)
    {
        var w = p.World;
        var roster = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase) { [w.MyName] = w.MainJob };
        foreach (var (sender, (msg, _)) in w.PartyChat.ToArray())
        {
            if (!msg.StartsWith("JOB ", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && PartyRoles.ParseJobToken(parts[1]) is var j and > 0) roster[sender] = j;
        }
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

        // 2. THF + a SECOND tank-capable = SATA formation: the sub-tank pulls, hate lands on the main tank.
        var thief = First(j => j == Job.Thf);
        if (thief is not null && tank is not null)
        {
            var subTank = roster.Where(kv => PartyRoles.CanFillOf(kv.Value).HasFlag(PartyRoles.Role.Tank)
                                             && !kv.Key.Equals(tank, StringComparison.OrdinalIgnoreCase))
                                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                                .Select(kv => kv.Key).FirstOrDefault();
            if (subTank is not null) return new(subTank, PullStyle.SataSubTank, tank, thief);
        }

        // 3. Main tank pulls at range (Provoke, else boomerang Shoot).
        return new(tank ?? roster.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).First(), PullStyle.TankRanged, tank, thief);
    }

    // ---- pull execution ---------------------------------------------------------------------------------

    /// The RANGED pull (main-tank style): Provoke if the job has it, else Shoot (boomerang), then walk home.
    /// Never engages at the mob — hate-only, so the mob does the walking.
    public static async Task RangedPull(ICombat combat, IPerception p, INavigation nav, uint mobId,
                                        (float x, float z) camp, CancellationToken ct)
    {
        if (!await combat.UseAbility(Ability.Provoke, mobId, ct)) combat.RangedAttack(mobId);
        await Task.Delay(800, ct);
        nav.MoveTo(camp.x, camp.z);                     // drag it home; the party engages at camp
    }

    /// The BARD pull: sing Foe Lullaby AT the next mob (aggro + it sleeps on arrival) and walk home. The
    /// party wakes it on THEIR schedule — pull-and-sleep chaining, zero camp downtime.
    public static async Task BardPull(IMagic magic, INavigation nav, uint mobId,
                                      (float x, float z) camp, CancellationToken ct)
    {
        magic.Cast(Spell.FoeLullaby, mobId);
        await Task.Delay(3500, ct);              // song cast time — let it land before walking
        nav.MoveTo(camp.x, camp.z);
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
        float len = MathF.Max(0.5f, MathF.Sqrt(dx * dx + dz * dz));
        nav.MoveTo(tank.X + dx / len * 2f, tank.Z + dz / len * 2f);
        for (int t = 0; t < 6000 && nav.IsMoving && !ct.IsCancellationRequested; t += 200) await Task.Delay(200, ct);

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
        float len = MathF.Max(0.5f, MathF.Sqrt(dx * dx + dz * dz));
        nav.MoveTo(mob.X + dx / len * 2f, mob.Z + dz / len * 2f);
        for (int t = 0; t < 6000 && nav.IsMoving && !ct.IsCancellationRequested; t += 200) await Task.Delay(200, ct);
        nav.Face(mobId);
    }

    static string JobToken(byte j) => j is >= 1 and <= 22
        ? new[] { "", "WAR", "MNK", "WHM", "BLM", "RDM", "THF", "PLD", "DRK", "BST", "BRD", "RNG",
                  "SAM", "NIN", "DRG", "SMN", "BLU", "COR", "PUP", "DNC", "SCH", "GEO", "RUN" }[j] : "ADV";
}
