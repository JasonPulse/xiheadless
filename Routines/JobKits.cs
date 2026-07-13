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
    /// Wire the generic kit into a grind config IF the brain left the defaults in place.
    public static void Apply(LevelGrind.Config g, byte job, ICombat combat, IMagic? magic, IPerception p, string tag)
    {
        if (ReferenceEquals(g.UseAbilities, LevelGrind.Config.NoAbilities))
            g.UseAbilities = For(job, combat, magic, p, tag);
        // Any job that can Cure (WHM/RDM main, or a WHM/RDM sub once set) self-heals below 50% — via the
        // Cure LINE selector (best affordable tier), never a hardcoded tier; magic gates known/MP itself.
        if (magic is not null && ReferenceEquals(g.EmergencyHeal, LevelGrind.Config.NoHeal))
            g.EmergencyHeal = async ct =>
            {
                if (p.World.Hpp >= 50 || p.World.Mpp < 10) return false;
                if (!magic.CastHighest(SpellLine.Cure, p.World.MyId)) return false;
                Log.Info($"[{tag}] Cure self (HP {p.World.Hpp}%)");
                await Task.Delay(2500, ct);
                return true;
            };
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
                // ---- BARD: songs ARE the kit. Foe Requiem (DoT, lowest known tier) on the mob, re-sung on a
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

                // ---- WHM offense: Banish between cures (heal comes via EmergencyHeal).
                case Job.Whm:
                    if (magic is not null && w.Mpp >= 25 && magic.CastLowest(SpellLine.Banish, mob))
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
