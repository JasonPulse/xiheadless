namespace XiHeadless.Brains;

/// GEOMANCER life path (PldBrain's 3-phase shape — brain = config only):
///   1. Prereq sub: WHM to 30 as MAIN (GEO/WHM per the roster).
///   2. Unlock: "Dances with Luopans" (QuestDefs.Unlock[Geo], researched from the server scripts:
///      Sylvie in Western Adoulin ev31/ev34/ev36 + the Windurst-nation Ergon Locus in Tahrongi Canyon).
///      Needs a Petrified Log (703, AH-bought). TODO(BLOCKED): the Adoulin zones have no overland edge
///      from the mainland (ship route) — coded but blocked until ship routing exists. TODO(rest-step):
///      the Luopan phase requires RESTING inside a Ceizak/Yahse Ergon Locus trigger area for up to ~8
///      minutes of healing ticks; QuestRunner has no Rest step (placeholder Examine in the QuestDefs).
///   3. Seesaw: level GEO main / WHM sub (JobLeveling switches whenever WHM < ceil(GEO/2)).
/// Gear: network-gnomes GEO guide lv1-29 bracket (wand + mage cloth, all WHM+GEO wearable so the ladder
/// is shared across phases), ids verified vs the server DB. Matre Bell (21460, GEO ranged-slot focus) and
/// the Plate of Indi-Poison (6074) are the unlock rewards — kept, bell equipped on GEO.
public sealed class GeoBrain(
    IPerception p, INavigation nav, ICombat combat, IMagic magic, IZoning zoning, IGear gear, IAuctionHouse ah,
    IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, IQuests quests, ITradeNpc trade, IEvents events, IChat chat, ILifecycle lifecycle, IParty party) : IBrain
{
    const byte ClubSkill = 11;                // wands are clubs; both GEO and the WHM phases swing them
    const ushort PetrifiedLog = 703;          // Sylvie trade item (AH-bought)
    const ushort MatreBell = 21460;           // unlock reward, GEO-only ranged-slot bell — never sell
    const ushort PlateOfIndiPoison = 6074;    // unlock reward (geomancy scroll) — never sell
    const string AhZone = "Windurst Woods";   // home-nation AH (char is Windurst)

    // Ascending by level so later (better) pieces override earlier ones in the same slot.
    static readonly (ushort item, byte slot, byte lvl)[] Gear =
    {
        (17087, EquipSlot.Main, 1),    // Maple Wand +1
        (12526, EquipSlot.Head, 1),    // Copper Hairpin +1
        (13093, EquipSlot.Neck, 7),    // Justice Badge
        (17138, EquipSlot.Main, 9),    // Willow Wand +1
        (12529, EquipSlot.Head, 10),   // Brass Hairpin +1
        (13379, EquipSlot.Ear1, 10),   // Energy Earring
        (13548, EquipSlot.Ring1, 10),  // Astral Ring
        (12626, EquipSlot.Body, 12),   // Linen Robe +1
        (12901, EquipSlot.Legs, 12),   // Linen Slops +1
        (17413, EquipSlot.Main, 13),   // Hermit's Wand
        (13211, EquipSlot.Waist, 14),  // Friar's Rope
        (17140, EquipSlot.Main, 18),   // Yew Wand +1
        (12531, EquipSlot.Head, 20),   // Silver Hairpin +1
        (14447, EquipSlot.Body, 20),   // Baron's Saio
        (15405, EquipSlot.Legs, 20),   // Baron's Slops
        (13113, EquipSlot.Neck, 20),   // Black Neckerchief
        (14884, EquipSlot.Hands, 25),  // Mycophile Cuffs
        (13073, EquipSlot.Neck, 26),   // Holy Phial
        (14025, EquipSlot.Hands, 27),  // Devotee's Mitts +1
        (15166, EquipSlot.Head, 29),   // Seer's Crown +1
        (14427, EquipSlot.Body, 29),   // Seer's Tunic +1
        (14328, EquipSlot.Legs, 29),   // Seer's Slacks +1
        (15316, EquipSlot.Feet, 29),   // Seer's Pumps +1
    };

    // Full arc (sub WHM->30, unlock, seesaw GEO/WHM) via the shared JobLifecycle — brain = config only.
    // A Petrified Log (703) for Sylvie's trade + stealth are stocked at the home AH. TODO(BLOCKED): the
    // Adoulin cluster has no overland edge (ship route), and the Luopan phase needs a REST-step QuestRunner
    // lacks — so the unlock will fail gracefully (hold + level WHM) until both are supported.
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, quests, trade, events,
            new JobLifecycle.Config
            {
                MainJob = Job.Geo, SubJob = Job.Whm, Advanced = true,
                UnlockSteps = QuestDefs.Unlock[Job.Geo],   // "Dances with Luopans"
                UnlockStockItems = new[] { (PetrifiedLog, 1) }, StealthUnlock = true,
                UnlockTrekZone = "Western_Adoulin", UnlockTrekZoneId = 256,
                GrindCfgFor = GrindCfg, Tag = "geo",
            }, lifecycle: lifecycle, chat: chat, magic: magic, party: party).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job) => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear).ToArray(),
        Keep = GearRoutines.KeepSet(Gear, 1126, 1127, PetrifiedLog, MatreBell, PlateOfIndiPoison),
        Equip = Equip,
        WepSkillForLevel = _ => ClubSkill,        // every phase (GEO and WHM) swings a wand
        ConMin = 1, ConMax = 3,
        CleanPullNeighborCon = 3,
        RestHpTrigger = 70, RestHpTarget = 90,
        RestMpPct = 40,                           // mage on both sides of the seesaw — rest MP back
        Tag = "geo",
    };

    async Task Equip(CancellationToken ct)
    {
        // GEO phases park the Matre Bell in the ranged slot (geomancy focus; the unlock reward).
        (byte slot, ushort item)? phase = p.World.MainJob == Job.Geo ? (EquipSlot.Ranged, MatreBell) : null;
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, Gear, ct, phase);
        Log.Info($"[geo] equipped {n}/{total} (job {p.World.MainJob} lvl {p.World.MainJobLevel}, club={gear.SkillLevel(ClubSkill)})");
    }
}
