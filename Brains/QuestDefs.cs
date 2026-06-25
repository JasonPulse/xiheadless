using XiHeadless.Capabilities;

namespace XiHeadless.Brains;

/// What a quest step does. Covers the mechanics the advanced-job unlocks actually use:
///  Talk/Examine — go to a zone, walk to a position, talk to/examine the entity there, answer Option.
///  Goto         — just travel to a zone.
///  ZoneInFrom   — travel so we ENTER Zone from FromZone (many quests fire onZoneIn checking prevZone).
///  Equip        — equip an item (e.g. a quest weapon) into a slot.
///  KillWith     — equip a weapon, then defeat Count monsters with it (combat-objective steps).
public enum StepKind { Goto, Talk, Examine, ZoneInFrom, Equip, KillWith }

/// One step of a quest flow. The server tracks quest state + key items; the engine just performs the
/// physical actions in order. Built via the factory helpers for readability.
public readonly record struct QuestStep(
    StepKind Kind, string Zone = "", float X = 0, float Y = 0, float Z = 0,
    uint Option = 0, ushort ItemId = 0, byte Slot = 0, int Count = 0, string FromZone = "", string Label = "")
{
    public static QuestStep Talk(string zone, float x, float y, float z, uint option, string label) => new(StepKind.Talk, zone, x, y, z, option, Label: label);
    public static QuestStep Examine(string zone, float x, float y, float z, string label) => new(StepKind.Examine, zone, x, y, z, Label: label);
    public static QuestStep Goto(string zone, string label) => new(StepKind.Goto, zone, Label: label);
    public static QuestStep ZoneInFrom(string fromZone, string zone, string label) => new(StepKind.ZoneInFrom, zone, FromZone: fromZone, Label: label);
    public static QuestStep Equip(ushort itemId, byte slot, string label) => new(StepKind.Equip, ItemId: itemId, Slot: slot, Label: label);
    public static QuestStep KillWith(ushort weaponItem, int count, string label) => new(StepKind.KillWith, ItemId: weaponItem, Count: count, Label: label);
}

/// Advanced-job unlock quest flows, keyed by job id. Transcribed from the server quest Lua. Server-
/// enforced gates are NOT in here (the engine can't bypass them): ADVANCED_JOB_LEVEL (30), prerequisite
/// quest chains, fame, and expansion-zone access. So a flow only completes on a suitably leveled/
/// progressed character.
public static class QuestDefs
{
    public static readonly Dictionary<byte, QuestStep[]> Unlock = new()
    {
        // PLD — "A Knight's Test" (Southern San d'Oria + Davoi). PREREQ: A Squire's Test I+II, level 30.
        [Job.Pld] = new[]
        {
            QuestStep.Talk("Southern_San_dOria", -136f, -11f, 64f, 0, "Balasiel: accept (ev627)"),
            QuestStep.Talk("Southern_San_dOria", -55f, -8f, -32f, 0, "Baunise: Book of the West (ev634)"),
            QuestStep.Talk("Southern_San_dOria", 55.749f, -8.601f, -29.354f, 0, "Cahaurme: Book of the East (ev633)"),
            QuestStep.Examine("Davoi", -221f, 2f, -293f, "Disused Well: get Knight's Soul"),
            QuestStep.Talk("Southern_San_dOria", -136f, -11f, 64f, 0, "Balasiel: complete -> unlock PLD (ev628)"),
        },

        // DRK — "Blade of Darkness" (Bastok). PREREQ: level 30. Talk Gumbah -> get Chaosbringer by
        // entering Zeruhn Mines from Palborough -> kill 100 mobs wielding it -> enter Beadeaux from
        // Pashhow Marshlands to unlock. (Chaosbringer item id 16607.)
        [Job.Drk] = new[]
        {
            QuestStep.Talk("Bastok_Mines", 52f, 0f, -36f, 0, "Gumbah: accept (ev99)"),
            QuestStep.ZoneInFrom("Palborough_Mines", "Zeruhn_Mines", "enter Zeruhn from Palborough -> Chaosbringer"),
            QuestStep.Equip(16607, EquipSlot.Main, "equip Chaosbringer"),
            QuestStep.KillWith(16607, 100, "kill 100 monsters wielding Chaosbringer"),
            QuestStep.ZoneInFrom("Pashhow_Marshlands", "Beadeaux", "enter Beadeaux from Pashhow -> unlock DRK"),
        },

        // TODO transcribe (same model): NIN bastok/Ayame_and_Kaede, RNG windurst/The_Fanged_One,
        //   SMN windurst/SMN_I_Can_Hear_a_Rainbow, BRD jeuno/Path_of_the_Bard,
        //   BST jeuno/Path_of_the_Beastmaster, DNC jeuno/Lakeside_Minuet.
        // Expansion-gated: BLU/COR/PUP (ahtUrhgan), SAM (outlands), SCH (crystalWar).
    };
}
