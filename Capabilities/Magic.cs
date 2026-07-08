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

    // 0x102 GP_CLI_COMMAND_EXTENDED_JOB (blu_data_t): hdr(4) SpellId@4 unk@5 pad@6-7 JobIndex@8
    // SupportJobFlg@9 pad@10-11 Spells[20]@12-31 unused[132]@32-163 => 168B = 42 words. Blue spell ids are
    // 512+ and ride the packet offset by 0x200 (server: "spells in this packet are offsetted by 0x200").
    // SET: SpellId = id-0x200 AND Spells[slot] = id-0x200. UNSET: SpellId = 0, Spells[slot] = id-0x200.
    // Requires BLU main or sub (server-enforced); the LEARNING itself comes via the GM !addspell grant.
    public void SetBlueSpell(ushort blueSpellId, byte slot, bool set = true)
    {
        if (blueSpellId < 0x200 || slot >= 20) { Log.Info($"[magic] SetBlueSpell refused: id {blueSpellId} / slot {slot}"); return; }
        var p = new byte[168];
        SubPacket.WriteHeader(p, 0x102);
        byte off = (byte)(blueSpellId - 0x200);
        p[4] = set ? off : (byte)0;   // SpellId: non-zero = add
        p[8] = 16;                    // JobIndex = JOB_BLU
        p[9] = (byte)(s.State.MainJob == 16 ? 0 : 1);   // SupportJobFlg: BLU is our sub, not main
        p[12 + slot] = off;           // the slot being played with
        s.Enqueue(p);
        Log.Info($"[magic] {(set ? "set" : "unset")} blue spell {blueSpellId} slot {slot}");
    }
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
