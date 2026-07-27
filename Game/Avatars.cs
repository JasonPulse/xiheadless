namespace XiHeadless.Game;

/// SMN avatar progression. On this server avatars are learned as SUMMON SPELLS via the GM `!addspell` grant
/// (NOT the retail lvl-75 prime-avatar fights) — a SMN with no avatar just melees and dies (2026-07-26:
/// lost 1v1 to con-4 Goblins). GATED UNLOCK (user 2026-07-27, bg-wiki strength order): a new avatar every
/// 7 levels; the last two (Alexander/Odin) stay MAIN-JOB-only at 75. SmnBrain requests each at its gate
/// level via GmGrant.RequestSpell; the SMN kit (JobKits) summons the highest KNOWN avatar and Blood-Pacts.
public static class Avatars
{
    // (summon spell, unlock level) in ascending strength/gate order — Carbuncle is the free starter at 1.
    public static readonly (Spell spell, byte level)[] Progression =
    {
        (Spell.Carbuncle,  1),
        (Spell.CaitSith,   8),
        (Spell.Ifrit,     15),
        (Spell.Shiva,     22),
        (Spell.Garuda,    29),
        (Spell.Titan,     36),
        (Spell.Ramuh,     43),
        (Spell.Leviathan, 50),
        (Spell.Fenrir,    57),
        (Spell.Diabolos,  64),
        (Spell.Siren,     71),
        (Spell.Alexander, 75),
        (Spell.Odin,      75),
    };

    // Entity names a summoned avatar shows up under (for "is my avatar out" detection). Both space and
    // underscore forms — the entity parser uses underscores for multi-word names (e.g. 'Sand_Hare').
    static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "Carbuncle", "Cait Sith", "Cait_Sith", "Ifrit", "Shiva", "Garuda", "Titan", "Ramuh",
        "Leviathan", "Fenrir", "Diabolos", "Siren", "Alexander", "Odin",
    };

    public static bool IsAvatarName(string name) => Names.Contains(name);
}
