namespace XiHeadless.Capabilities;

public interface IMagic
{
    void Cast(Spell spell, uint target);            // a specific spell, straight up
    bool Known(Spell spell);                         // learned (0x0AA bitmap)
    bool Ready(Spell spell);                          // known + enough MP (recast TODO)
    Spell? Highest(SpellLine line);                   // highest KNOWN tier
    Spell? Lowest(SpellLine line);                    // lowest KNOWN tier
    void CastHighest(SpellLine line, uint target);    // cast highest READY (known + affordable) tier
    void CastLowest(SpellLine line, uint target);     // cast lowest READY tier
}

public sealed class Magic(ISession s) : IMagic
{
    public void Cast(Spell spell, uint target) { s.State.CurrentTargetId = target; s.Enqueue(ActionPacket.Build(ActionPacket.CastMagic, target, 0, (uint)spell)); }
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
