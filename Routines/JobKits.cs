using XiHeadless.Game;

namespace XiHeadless.Routines;

/// GENERIC per-job combat kits, injected by JobLifecycle when a brain didn't wire its own UseAbilities /
/// EmergencyHeal (most fleet brains are gear+quest configs with NO kit — live fleet day 1: a lvl-1 BRD pure
/// melee auto-attacked and lost to a con-2 rabbit 17x/h; with its songs it kills far above its level, per
/// the user). Everything self-gates: combat.UseAbility checks job/level/recast; magic checks Known/MP —
/// so kits list generously and the wrong-job entries no-op. The CURATED brains (WarBrain/BlmBrain) keep
/// their own rotations; this covers the other ~18.
public static class JobKits
{
    // "Mages use spells" (user) — the early scroll kit IS a caster's weapon. Per-job relevance comes from
    // SpellLevels (usable by ~lv12), so WHM resolves to Cure/Dia/Banish/Paralyze, BLM to Stone/Water/etc.
    // Bought by LevelGrind's existing AH buy phase (appended to BuyItems, cheapest essentials first) and
    // learned in the Equip pass — the proven WhmBrain arc (club + Dia + Cure -> 18), now engine-provided
    // so every advanced-job mage phase (SCH/GEO/SMN/RDM sub-arcs) gets it without brain-side lists.
    // buyable=false = the EX STARTING scrolls (granted by charCreate to mage-created chars: Cure_EX 4608,
    // Stone_EX 4607, Dia_EX 4606) — learn-only, never in the AH buy list (EX is unlistable) and listed
    // FIRST so a char holding its free starting scroll learns from it before ever bidding on the shop copy.
    static readonly (ushort scroll, Spell spell, bool buyable)[] ScrollKit =
    {
        (4608, Spell.Cure, false), (4606, Spell.Dia, false), (4607, Spell.Stone, false),
        (4609, Spell.Cure, true), (4631, Spell.Dia, true), (4636, Spell.Banish, true), (4666, Spell.Paralyze, true),
        (4767, Spell.Stone, true), (4777, Spell.Water, true), (4762, Spell.Aero, true), (4862, Spell.Blind, true),
        // BRD songs ARE its spells (user: lvl-1s with no songs were red flags) — SpellLevels filters to BRD.
        (4976, Spell.FoeRequiem, true), (4986, Spell.ArmysPaeon, true),
        (5002, Spell.ValorMinuet, true), (5007, Spell.SwordMadrigal, true),
    };

    static (ushort scroll, Spell spell, bool buyable)[] EssentialScrolls(byte job) =>
        ScrollKit.Where(s => SpellLevels.For((ushort)s.spell, job) is { } lvl && lvl <= 12).ToArray();

    /// Wire the generic kit into a grind config IF the brain left the defaults in place.
    public static void Apply(LevelGrind.Config g, byte job, ICombat combat, IMagic? magic, IPerception p, string tag,
                             IInventory? inv = null)
    {
        if (ReferenceEquals(g.UseAbilities, LevelGrind.Config.NoAbilities))
            g.UseAbilities = For(job, combat, magic, p, tag);
        // Self-funding default: if the brain wired NO bag policy at all, sell junk drops when the bag
        // fills (drops -> gil -> scrolls/gear is the whole broke-bot economy; a bag that silently fills
        // just bounces loot). Brains with an explicit OnBagFull (party farms) are untouched.
        if (!g.SellJunkWhenFull && g.OnBagFull is null) { g.SellJunkWhenFull = true; g.SellAtItems = Math.Min(g.SellAtItems, 22); }
        // Essential scrolls for this phase's job: buy (via the standard buy phase) + learn (in the Equip
        // pass). Applies to EVERY brain's mage phases — scroll learning is engine duty, not brain config.
        if (magic is not null && inv is not null && EssentialScrolls(job) is { Length: > 0 } scrolls)
        {
            g.BuyItems = scrolls.Where(s => s.buyable).Select(s => s.scroll).Where(s => !g.BuyItems.Contains(s)).Concat(g.BuyItems).ToArray();
            foreach (var (scroll, _, _) in scrolls) g.Keep.Add(scroll);
            var innerEquip = g.Equip;
            g.Equip = async ct =>
            {
                await innerEquip(ct);
                foreach (var (scroll, spell, _) in scrolls)
                    if (!magic.Known(spell) && inv.Has(scroll)
                        && SpellLevels.For((ushort)spell, p.World.MainJob) is { } lvl && p.World.MainJobLevel >= lvl)
                        await MagicRoutines.LearnFromScroll(inv, magic, p, scroll, spell, ct, tag);
            };
        }
        // Any job that can Cure (WHM/RDM main, or a WHM/RDM sub once set) self-heals below 50% — the one
        // shared MagicRoutines.EmergencyCure (level-gated Cure line selector, never a hardcoded tier).
        if (magic is not null && ReferenceEquals(g.EmergencyHeal, LevelGrind.Config.NoHeal))
            g.EmergencyHeal = ct => MagicRoutines.EmergencyCure(magic, p, ct, tag: tag);
    }

    // The per-beat rotation for `job`. One action per beat max (each firing path delays internally).
    static Func<uint, int, CancellationToken, Task> For(byte job, ICombat combat, IMagic? magic, IPerception p, string tag)
    {
        long lastSongMs = 0;
        return async (mob, con, ct) =>
        {
            var w = p.World;
            switch (job)
            {
                // ---- BARD: songs ARE the kit. Foe Requiem (DoT, BEST castable tier) on the mob, re-sung on a
                // song-length cadence; melee carries the rest. (Lv-1 BRD + Requiem beats far above its level.)
                case Job.Brd:
                    if (magic is not null && w.NowMs - lastSongMs > 30_000
                        && magic.CastHighest(SpellLine.FoeRequiem, mob))   // tier selector — best known Requiem
                    {
                        lastSongMs = w.NowMs;
                        Log.Info($"[{tag}] singing Requiem on the mob");
                        await Task.Delay(3000, ct);
                    }
                    return;

                // ---- CASTERS: the cheapest known nuke each beat (the BLM pattern, generalized). CastLowest
                // picks the lowest READY tier; the line list covers each caster's early book.
                case Job.Blm or Job.Rdm or Job.Sch or Job.Geo or Job.Smn:
                    if (magic is null || w.Mpp < 10) return;
                    // Dia LAST: it's a one-per-fight DoT, but it's also all a lvl-1-3 RDM has (Stone is RDM 4)
                    foreach (var line in new[] { SpellLine.Stone, SpellLine.Water, SpellLine.Aero, SpellLine.Bio, SpellLine.Banish, SpellLine.Dia })
                        if (magic.CastLowest(line, mob))   // tier selector: cheapest ready tier of the line
                        {
                            await Task.Delay(3000, ct);
                            return;
                        }
                    return;

                // ---- WHM offense: Banish between cures (heal comes via EmergencyHeal); Dia when Banish
                // isn't castable yet — a lv3-4 WHM's entire offense is Dia (user: mages use spells).
                case Job.Whm:
                    if (magic is not null && w.Mpp >= 25
                        && (magic.CastLowest(SpellLine.Banish, mob) || magic.CastLowest(SpellLine.Dia, mob)))
                        await Task.Delay(3000, ct);
                    return;

                // ---- MELEE/other: fire the job's signature low/mid JAs. UseAbility self-gates on
                // job/level/recast, so the whole list is safe to try; long-recast buffs only on real fights.
                default:
                    if (con >= 2)
                    {
                        foreach (var ab in MeleeJas)
                            if (await combat.UseAbility(ab, mob, ct)) { Log.Info($"[{tag}] {ab}"); return; }
                    }
                    return;
            }
        };
    }

    // Signature offensive/self JAs across the melee jobs (each no-ops unless the char's job/level holds it):
    // MNK Boost/Focus/Dodge, SAM Meditate/Third Eye, DRK Last Resort, THF (SA solo), DRG Jump/High Jump,
    // BST no early JA needed, RNG Sharpshot, NIN —, PLD Sentinel late, WAR handled by its curated brain.
    static readonly Ability[] MeleeJas =
    {
        Ability.Meditate, Ability.Jump, Ability.HighJump, Ability.Boost, Ability.Focus,
        Ability.LastResort, Ability.SneakAttack, Ability.Sharpshot, Ability.Dodge, Ability.ThirdEye,
    };
}
