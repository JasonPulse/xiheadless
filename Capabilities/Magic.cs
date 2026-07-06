namespace XiHeadless.Capabilities;

public sealed class Magic(ISession s) : IMagic
{
    public void Cast(Spell spell, uint target)
    {
        // The server resolves a spell's target by ActIndex (targid), NOT char id — passing index 0 makes the cast
        // a SILENT NO-OP (exactly like combat). This was the bug: every Cure was logged but never actually cast.
        // WorldState.TargidOf resolves self (MyIndex) / tracked-entity index / &0xFFF fallback in one place.
        ushort idx = s.State.TargidOf(target);
        s.State.CurrentTargetId = target;
        s.Enqueue(ActionPacket.Build(ActionPacket.CastMagic, target, idx, (uint)spell));
    }
    public bool Known(Spell spell) => s.State.KnowsSpell((ushort)spell);
    public bool Ready(Spell spell) // known + affordable MP (recast timers TODO)
        => Known(spell) && (!Spells.Info.TryGetValue(spell, out var i) || i.Mp <= s.State.Mp);

    public Spell? Highest(SpellLine line)
    {
        if (!Spells.Tiers.TryGetValue(line, out var tiers)) return null;
        for (int i = tiers.Length - 1; i >= 0; i--) if (Known(tiers[i])) return tiers[i];
        return null;
    }
    public Spell? Lowest(SpellLine line)
    {
        if (!Spells.Tiers.TryGetValue(line, out var tiers)) return null;
        foreach (var t in tiers) if (Known(t)) return t;
        return null;
    }
    // Cast the best/cheapest tier we can actually afford right now.
    public void CastHighest(SpellLine line, uint target)
    {
        if (!Spells.Tiers.TryGetValue(line, out var tiers)) return;
        for (int i = tiers.Length - 1; i >= 0; i--) if (Ready(tiers[i])) { Cast(tiers[i], target); return; }
    }
    public void CastLowest(SpellLine line, uint target)
    {
        if (!Spells.Tiers.TryGetValue(line, out var tiers)) return;
        foreach (var t in tiers) if (Ready(t)) { Cast(t, target); return; }
    }
}
