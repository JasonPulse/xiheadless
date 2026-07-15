namespace XiHeadless.Brains;

/// SCHOLAR life path (PldBrain's 3-phase shape — brain = config only):
///   1. Prereq sub: WHM to 30 as MAIN (SCH/WHM per the roster).
///   2. Unlock: "A Little Knowledge" (QuestDefs.Unlock[Sch]: Eldieme Necropolis [S] + Crawlers' Nest [S]).
///      Trade-driven: 12x Rolanberry (4365) -> Tucker -> 12x Sheet of Vellum (2550) -> Erlene. The
///      Rolanberries are AH-bought before the trek. TODO(WotG): both zones are past Cavernous Maws — if
///      the zone graph has no edge into the [S] zones the runner stalls there; coded, verify routing live.
///   3. Seesaw: level SCH main / WHM sub (JobLeveling switches whenever WHM < ceil(SCH/2)).
/// Gear: network-gnomes SCH guide lv1-29 bracket (wand + mage cloth, shared with the WHM phases since
/// every piece is WHM+SCH wearable), ids verified vs the server DB.
public sealed class SchBrain(
    IPerception p, INavigation nav, ICombat combat, IMagic magic, IZoning zoning, IGear gear, IAuctionHouse ah,
    IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, IQuests quests, ITradeNpc trade, IEvents events, IChat chat, ILifecycle lifecycle, IParty party) : IBrain
{
    const byte ClubSkill = 11;                // wands are clubs; both SCH and the WHM phases swing them
    const ushort Rolanberry = 4365;           // Tucker trade x12 (AH-bought)
    const ushort SheetOfVellum = 2550;        // Erlene trade x12 (given by Tucker)
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
        (15405, EquipSlot.Legs, 20),   // Baron's Slops
        (14884, EquipSlot.Hands, 25),  // Mycophile Cuffs
        (13073, EquipSlot.Neck, 26),   // Holy Phial
        (14025, EquipSlot.Hands, 27),  // Devotee's Mitts +1
        (15166, EquipSlot.Head, 29),   // Seer's Crown +1
        (14427, EquipSlot.Body, 29),   // Seer's Tunic +1
        (14328, EquipSlot.Legs, 29),   // Seer's Slacks +1
        (15316, EquipSlot.Feet, 29),   // Seer's Pumps +1
    };

    // Full arc (sub WHM->30, unlock, seesaw SCH/WHM) via the shared JobLifecycle — brain = config only.
    // 12 Rolanberries (4365) for the Tucker trade + stealth are stocked at the home AH, then the brain
    // stealth-treks to the Eldieme Necropolis [S] (the chain start). TODO(WotG, unchanged): both zones are
    // past Cavernous Maws — if the graph has no [S] edge the unlock fails gracefully (hold + level WHM).
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, quests, trade, events,
            new JobLifecycle.Config
            {
                MainJob = Job.Sch, SubJob = Job.Whm, Advanced = true,
                UnlockSteps = QuestDefs.Unlock[Job.Sch],   // "A Little Knowledge"
                UnlockStockItems = new[] { (Rolanberry, 12) }, StealthUnlock = true,
                UnlockTrekZone = "The_Eldieme_Necropolis_[S]", UnlockTrekZoneId = 175,
                GrindCfgFor = GrindCfg, Tag = "sch",
            }, lifecycle: lifecycle, chat: chat, magic: magic, party: party).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job) => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear).ToArray(),
        Keep = GearRoutines.KeepSet(Gear, 1126, 1127, Rolanberry, SheetOfVellum),
        Equip = Equip,
        WepSkillForLevel = _ => ClubSkill,        // every phase (SCH and WHM) swings a wand
        ConMin = 1, ConMax = 3,
        CleanPullNeighborCon = 3,
        RestHpTrigger = 70, RestHpTarget = 90,
        RestMpPct = 40,                           // mage on both sides of the seesaw — rest MP back
        Tag = "sch",
    };

    async Task Equip(CancellationToken ct)
    {
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, Gear, ct);
        Log.Info($"[sch] equipped {n}/{total} (job {p.World.MainJob} lvl {p.World.MainJobLevel}, club={gear.SkillLevel(ClubSkill)})");
    }
}
