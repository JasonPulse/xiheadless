namespace XiHeadless.Brains;

/// BARD life path (PldBrain's 3-phase shape — brain = config only):
///   1. Prereq sub: WHM to 30 as MAIN (BRD/WHM per the roster).
///   2. Unlock: The Old Monument -> A Minstrel in Despair (QuestDefs.Prereqs[Brd]) -> Path of the Bard
///      (QuestDefs.Unlock[Brd], Song Runes in Valkurm). Needs a Sheet of Parchment (917) for the monument
///      trade — bought at the home AH before the trek; Poetic Parchment (634) is the quest's own reward.
///   3. Seesaw: level BRD main / WHM sub (JobLeveling switches whenever WHM < ceil(BRD/2)).
/// Gear: network-gnomes BRD guide lv1-38 bracket (dagger + light cloth), ids verified vs the server DB.
public sealed class BrdBrain(
    IPerception p, INavigation nav, ICombat combat, IMagic magic, IZoning zoning, IGear gear, IAuctionHouse ah,
    IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, IQuests quests, ITradeNpc trade, IEvents events) : IBrain
{
    const byte DaggerSkill = 2;
    const byte ClubSkill = 11;                // WHM prereq/sub phases swing a wand
    const ushort MapleWand = 17087;           // Maple Wand +1 — the WHM-phase weapon (not BRD-equippable)
    const ushort SheetOfParchment = 917;      // Old Monument trade item (AH-bought)
    const ushort PoeticParchment = 634;       // Minstrel trade item (given by Old Monument)
    const string AhZone = "Windurst Woods";   // home-nation AH (char is Windurst)

    // Ascending by level so later (better) pieces override earlier ones in the same slot.
    static readonly (ushort item, byte slot, byte lvl)[] Gear =
    {
        (16753, EquipSlot.Main, 1),    // Ceremonial Dagger
        (12526, EquipSlot.Head, 1),    // Copper Hairpin +1
        (13093, EquipSlot.Neck, 7),    // Justice Badge
        (12568, EquipSlot.Body, 7),    // Leather Vest
        (12696, EquipSlot.Hands, 7),   // Leather Gloves
        (12824, EquipSlot.Legs, 7),    // Leather Trousers
        (12952, EquipSlot.Feet, 7),    // Leather Highboots
        (12529, EquipSlot.Head, 10),   // Brass Hairpin +1
        (16614, EquipSlot.Main, 13),   // Knife +1
        (12799, EquipSlot.Hands, 14),  // Battle Gloves
        (13211, EquipSlot.Waist, 14),  // Friar's Rope
        (13072, EquipSlot.Neck, 15),   // Bird Whistle (CHR — song potency)
        (13592, EquipSlot.Back, 17),   // Lizard Mantle
        (16746, EquipSlot.Main, 20),   // Mercenary's Knife
        (15186, EquipSlot.Head, 20),   // Trump Crown
        (14447, EquipSlot.Body, 20),   // Baron's Saio
        (13048, EquipSlot.Feet, 20),   // Mage's Sandals
        (15224, EquipSlot.Head, 24),   // Empress Hairpin
        (13631, EquipSlot.Back, 24),   // Nomad's Mantle
        (13094, EquipSlot.Neck, 27),   // Flower Necklace (CHR)
        (16487, EquipSlot.Main, 38),   // Minstrel's Dagger
    };

    // Full arc (sub WHM->30, unlock chain, seesaw BRD/WHM) via the shared JobLifecycle — brain = config only.
    // A Sheet of Parchment (917) for the Old Monument trade + stealth are stocked at the home AH, then the
    // brain stealth-treks to Lower Jeuno (the chain start) before the quest steps run.
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, quests, trade, events,
            new JobLifecycle.Config
            {
                MainJob = Job.Brd, SubJob = Job.Whm, Advanced = true,
                UnlockSteps = QuestDefs.Prereqs[Job.Brd].Concat(QuestDefs.Unlock[Job.Brd]).ToArray(),
                UnlockStockItems = new[] { (SheetOfParchment, 1) }, StealthUnlock = true,
                UnlockTrekZone = "Lower_Jeuno", UnlockTrekZoneId = 245,
                GrindCfgFor = GrindCfg, Tag = "brd",
            }, magic: magic).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job) => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear).Append(MapleWand).ToArray(),
        Keep = GearRoutines.KeepSet(Gear, 1126, 1127, MapleWand, SheetOfParchment, PoeticParchment),
        Equip = Equip,
        WepSkillForLevel = _ => job == Job.Whm ? ClubSkill : DaggerSkill,
        ConMin = 1, ConMax = 3,
        CleanPullNeighborCon = 3,
        RestHpTrigger = 70, RestHpTarget = 90,
        RestMpPct = job == Job.Whm ? 40 : 0,     // the WHM phase casts — rest MP back too
        Tag = "brd",
    };

    async Task Equip(CancellationToken ct)
    {
        // WHM phases can't hold BRD daggers — swing the wand instead.
        (byte slot, ushort item)? phase = p.World.MainJob == Job.Whm ? (EquipSlot.Main, MapleWand) : null;
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, Gear, ct, phase);
        Log.Info($"[brd] equipped {n}/{total} (job {p.World.MainJob} lvl {p.World.MainJobLevel}, dagger={gear.SkillLevel(DaggerSkill)})");
    }
}
