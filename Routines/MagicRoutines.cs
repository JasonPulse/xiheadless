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
}
