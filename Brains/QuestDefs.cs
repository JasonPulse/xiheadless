using XiHeadless.Capabilities;

namespace XiHeadless.Brains;

/// One step of a quest flow: go to Zone, walk to (X,Y,Z), talk to the NPC there and answer with Option.
/// Positions/zones come straight from the server quest scripts (the `!pos` comments). The server tracks
/// quest state + key items, so the bot only needs to visit the NPCs in order with the right options.
public readonly record struct QuestStep(string Zone, float X, float Y, float Z, uint Option, string Label);

/// Advanced-job unlock quest flows, keyed by job id. Data transcribed from the server quest Lua.
/// Server-enforced gates NOT encoded here (the runner can't bypass them): ADVANCED_JOB_LEVEL (30) and
/// any prerequisite quest chain. Expansion jobs need expansion-zone access.
public static class QuestDefs
{
    public static readonly Dictionary<byte, QuestStep[]> Unlock = new()
    {
        // PLD — "A Knight's Test" (Southern San d'Oria + Davoi). PREREQUISITE: "A Squire's Test" + "A
        // Squire's Test II" must be completed first (not yet encoded), and main level >= 30.
        // NPCs (from A_Knights_Test.lua !pos): Balasiel -136,-11,64; Baunise -55,-8,-32;
        // Cahaurme 55.7,-8.6,-29.4 (zone 230); Disused Well -221,2,-293 (Davoi, zone 149).
        [Job.Pld] = new QuestStep[]
        {
            new("Southern_San_dOria", -136f, -11f,  64f,    0, "Balasiel: accept the test (ev627)"),
            new("Southern_San_dOria", -55f,  -8f,  -32f,    0, "Baunise: Book of the West (ev634)"),
            new("Southern_San_dOria",  55.749f, -8.601f, -29.354f, 0, "Cahaurme: Book of the East (ev633)"),
            new("Davoi",              -221f,  2f,  -293f,   0, "Disused Well: get Knight's Soul"),
            new("Southern_San_dOria", -136f, -11f,  64f,    0, "Balasiel: complete -> unlock PLD (ev628)"),
        },

        // TODO transcribe the remaining flows (same shape) from their Lua:
        //   DRK bastok/Blade_of_Darkness, NIN bastok/Ayame_and_Kaede, RNG windurst/The_Fanged_One,
        //   SMN windurst/SMN_I_Can_Hear_a_Rainbow, BRD jeuno/Path_of_the_Bard,
        //   BST jeuno/Path_of_the_Beastmaster, DNC jeuno/Lakeside_Minuet.
        // Expansion-gated (need access first): BLU/COR/PUP (ahtUrhgan), SAM (outlands), SCH (crystalWar).
    };
}
