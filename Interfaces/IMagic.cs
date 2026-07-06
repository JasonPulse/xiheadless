namespace XiHeadless.Interfaces;

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
