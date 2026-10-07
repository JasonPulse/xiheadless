namespace XiHeadless.Routines;

/// Shared spell-learning. WhmBrain and PartyLeechBrain both did "find the scroll slot -> UseItem -> poll
/// until Known" — one canonical version now (slot scan already lives on IInventory.SlotOf).
public static class MagicRoutines
{
    /// Learn `spell` by using its scroll from the main bag, then poll (~6s) until it registers as Known.
    /// No-op (returns true) if already known; returns false if the scroll isn't held or it never registered.
    public static async Task<bool> LearnFromScroll(IInventory inv, IMagic magic, IPerception p,
                                                   ushort scrollId, Spell spell, CancellationToken ct, string tag = "magic")
    {
        if (magic.Known(spell)) return true;
        ushort slot = inv.SlotOf(scrollId);
        if (slot == 0) return false;
        Log.Info($"[{tag}] learning {spell} from scroll {scrollId} (slot {slot})");
        inv.UseItem(0, (byte)slot);
        for (int t = 0; t < 6000 && !magic.Known(spell) && !ct.IsCancellationRequested; t += 250) await Task.Delay(250, ct);
        bool ok = magic.Known(spell);
        Log.Info(ok ? $"[{tag}] learned {spell}" : $"[{tag}] {spell} not learned yet (retry on next level-up)");
        return ok;
    }

    /// Pull a mob by casting the best ready tier of `line` at range. WhmBrain (Dia) and BlmBrain (Stone)
    /// carried twin copies of this differing only by the hardcoded spell — one selector-driven version now.
    public static async Task<bool> SpellPull(IMagic magic, IPerception p, SpellLine line, uint mobId,
                                             CancellationToken ct, float range = 18f, string tag = "magic")
    {
        if (!p.World.Entities.TryGetValue(mobId, out var e) || p.DistanceTo(e.X, e.Z) > range) return false;
        if (!magic.CastHighest(line, mobId)) return false;
        Log.Info($"[{tag}] {line} pull on 0x{mobId:X}");
        await Task.Delay(3000, ct);
        return true;
    }

    /// The single-target elemental nukes, in no particular order: the selector ranks them by spell level.
    static readonly SpellLine[] NukeLines =
        { SpellLine.Stone, SpellLine.Water, SpellLine.Aero, SpellLine.Fire, SpellLine.Blizzard, SpellLine.Thunder };

    /// Cast the STRONGEST ready nuke: the best ready tier of every elemental line, ranked by the level the
    /// spell is learned at (Thunder 21 > Blizzard 17 > Fire 13 > Aero 9 > Water 5 > Stone 1, tier II above
    /// all tier I). Casters used to pick the CHEAPEST ready tier of Stone first, so a BLM 23 chipped an even-
    /// match lizard with Stone I while it was beaten to death (Droben: 58 deaths, 0 kills in one session).
    public static bool CastBestNuke(IMagic magic, IPerception p, uint mobId)
    {
        Spell? best = null; int bestLvl = -1;
        foreach (var line in NukeLines)
            if (magic.BestReady(line) is { } sp)
            {
                int lvl = SpellLevels.For((ushort)sp, p.World.MainJob) ?? SpellLevels.For((ushort)sp, p.World.SubJob) ?? 0;
                if (lvl > bestLvl) { best = sp; bestLvl = lvl; }
            }
        if (best is not { } cast) return false;
        magic.Cast(cast, mobId);
        return true;
    }

    /// Self-cure below `hppBelow` with the best affordable Cure tier (level-gated selector). The single
    /// emergency-heal used by JobKits and the caster brains — thresholds are the only per-brain config.
    public static async Task<bool> EmergencyCure(IMagic magic, IPerception p, CancellationToken ct,
                                                 byte hppBelow = 50, byte minMpp = 10, string tag = "magic")
    {
        if (p.World.Hpp >= hppBelow || p.World.Mpp < minMpp) return false;
        if (!magic.CastHighest(SpellLine.Cure, p.World.MyId)) return false;
        Log.Info($"[{tag}] Cure self (HP {p.World.Hpp}% MP {p.World.Mpp}%)");
        await Task.Delay(2500, ct);
        return true;
    }
}
