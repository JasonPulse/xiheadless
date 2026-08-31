namespace XiHeadless.Game;

/// BLU spell progression. On this server blue magic is GM-GRANTED via !addspell (the same mechanism as SMN
/// avatars — bots can't farm the retail learn-by-being-hit), then equipped with IMagic.SetBlueSpell before it
/// can be cast. GRANT CADENCE = the spell's NATURAL learn level, read from SpellLevels (job BLU): a bot never
/// holds a spell before it has leveled to where it would be fighting the mobs that teach it (user 2026-08-31).
/// BluBrain requests each at its gate level via GmGrant.RequestSpell and SetBlueSpells it; the BLU kit
/// (JobKits) casts the strongest READY set spell, Pollen to self-heal.
public static class BlueSpells
{
    // A damage-first leveling set (plus a self-heal, a DEF buff, and a stoneskin), in learn order; `slot` is
    // the BLU set-slot. The server caps total set POINTS, not slots, so the higher entries only stick once set
    // points have grown (SetBlueSpell logs any it can't fit) — the low, cheap spells set first and always hold.
    // Grant level is NOT stored here: it comes from SpellLevels.For(spell, Blu) so it always matches the
    // server's own learn level.
    public static readonly (Spell spell, byte slot)[] LevelingSet =
    {
        (Spell.FootKick,     0),   // lv1  damage (kick)
        (Spell.Pollen,       1),   // lv1  self-heal
        (Spell.SproutSmack,  2),   // lv4  damage + slow
        (Spell.WildOats,     3),   // lv4  damage
        (Spell.MetallicBody, 4),   // lv8  stoneskin
        (Spell.Cocoon,       5),   // lv8  DEF+ self-buff
        (Spell.Queasyshroom, 6),   // lv8  damage + poison
        (Spell.HeadButt,     7),   // lv12 damage + stun
        (Spell.Bludgeon,     8),   // lv18 damage
        (Spell.CursedSphere, 9),   // lv18 magical damage
        (Spell.BloodDrain,   10),  // lv20 drain (damage + HP)
        (Spell.ClawCyclone,  11),  // lv20 AoE damage
        (Spell.Screwdriver,  12),  // lv26 damage
        (Spell.BombToss,     13),  // lv28 damage
        (Spell.GrandSlam,    14),  // lv30 damage
        (Spell.Uppercut,     15),  // lv38 damage
    };

    // Kit cast priority: strongest first (reverse of learn order) so the bot leads with its best READY nuke
    // and falls down the ladder. The utility spells (Pollen heal, Cocoon/MetallicBody buffs) are cast by the
    // kit in their own context, not as damage, so they are NOT in this list.
    public static readonly Spell[] DamageByStrength =
    {
        Spell.Uppercut, Spell.GrandSlam, Spell.BombToss, Spell.Screwdriver, Spell.ClawCyclone,
        Spell.BloodDrain, Spell.CursedSphere, Spell.Bludgeon, Spell.HeadButt, Spell.Queasyshroom,
        Spell.WildOats, Spell.SproutSmack, Spell.FootKick,
    };
}
