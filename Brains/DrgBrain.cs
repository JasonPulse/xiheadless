namespace XiHeadless.Brains;

/// DRAGOON life path (PldBrain's 3-phase shape — brain = config only):
///   1. Prereq sub: WAR to 30 as MAIN.
///   2. Unlock: "The Holy Crest" via QuestRunner (QuestDefs.Unlock[Job.Drg] — steps transcribed from
///      the server scripts; Morjean's position verified via npc_list.sql).
///   3. Seesaw: level DRG main / WAR sub (JobLeveling switches whenever WAR < ceil(DRG/2)).
/// Unlock TODOs (live work needed):
///   * Novalmauge PATROLS inside Bostaunieux Oubliette (reached through Chateau d'Oraguille) — the
///     fixed-coord Talk may miss him mid-patrol.
///   * Needs a Pickaxe (605) in the bag for the Maze of Shakhrami excavation trade.
///   * The finale is a BATTLEFIELD (Hut Door, Ghelsba Outpost): enter "Holy Crest", kill Cyranuce M
///     Cutauleon, and answer the win CS with a NON-ZERO option (the wyvern's name; 0 = decline) —
///     battlefield entry/fight/CS-option are not expressible as QuestRunner steps yet.
public sealed class DrgBrain(
    IPerception p, INavigation nav, ICombat combat, IMagic magic, IZoning zoning, IGear gear, IAuctionHouse ah,
    IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, IQuests quests, ITradeNpc trade, IEvents events) : IBrain
{
    const byte PolearmSkill = 8;              // LSB skill enum (Polearm=8 — verified vs the generated WS table)
    const byte GreatAxeSkill = 6;             // WAR prereq/sub phases ride the proven Great Axe kit
    const ushort Pickaxe = 605;               // quest excavation tool — never sell
    const ushort WyvernEgg = 1159;            // quest item — never sell
    const string AhZone = "Windurst Woods";   // home-nation AH (char is Windurst)

    // DRG-specific pieces (ascending by level); low-level body/hands/legs/feet come from the job-shared
    // WarBrain.Armor leather set (reused like ThfBrain does — never forked).
    static readonly (ushort item, byte slot, byte lvl)[] Gear =
    {
        (16862, EquipSlot.Main, 1),    // Harpoon +1
        (13014, EquipSlot.Feet, 7),    // Leaping Boots
        (13613, EquipSlot.Back, 12),   // Traveler's Mantle
        (18076, EquipSlot.Main, 14),   // Spark Spear
        (12799, EquipSlot.Hands, 14),  // Battle Gloves
        (13522, EquipSlot.Ring1, 14),  // Courage Ring
        (13240, EquipSlot.Waist, 15),  // Warrior's Belt +1
        (18085, EquipSlot.Main, 20),   // Platoon Lance
        (13061, EquipSlot.Neck, 21),   // Spike Necklace
        (13326, EquipSlot.Ear1, 21),   // Beetle Earring +1
        (15224, EquipSlot.Head, 24),   // Empress Hairpin
        (14260, EquipSlot.Legs, 25),   // Republic Subligar
        (12567, EquipSlot.Body, 27),   // Steam Scale Mail
    };

    // Full arc (sub WAR->30, unlock, seesaw DRG/WAR) via the shared JobLifecycle — brain = config only.
    // A Pickaxe (605) is stocked at the home AH for the Maze of Shakhrami excavation trade. CAVEAT
    // (unchanged): "The Holy Crest" ends in a battlefield (kill Cyranuce, non-zero win-CS option) plus a
    // patrolling Novalmauge — not fully expressible, so the unlock will fail gracefully (hold + level WAR).
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, quests, trade, events,
            new JobLifecycle.Config
            {
                MainJob = Job.Drg, SubJob = Job.War, Advanced = true,
                UnlockSteps = QuestDefs.Unlock[Job.Drg],   // "The Holy Crest"
                UnlockStockItems = new[] { (Pickaxe, 1) }, StealthUnlock = true,
                GrindCfgFor = GrindCfg, Tag = "drg",
            }, magic: magic).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job) => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear)
            .Concat(WarBrain.Armor.Select(g => g.item))
            .Concat(WarBrain.Armor21.Select(g => g.item)).ToArray(),
        Keep = new HashSet<ushort>(GearRoutines.BuyList(Gear)
            .Concat(WarBrain.Armor.Select(g => g.item))
            .Concat(WarBrain.Armor21.Select(g => g.item))
            .Concat(new ushort[] { 1126, 1127, Pickaxe, WyvernEgg })),
        Equip = Equip,
        WepSkillForLevel = _ => job == Job.War ? GreatAxeSkill : PolearmSkill,
        ConMin = 1, ConMax = 3,
        CleanPullNeighborCon = 3,
        SkipMobNames = new[] { "Saplin", "Mandragora" },
        RestHpTrigger = 70, RestHpTarget = 90,
        Tag = "drg",
    };

    async Task Equip(CancellationToken ct)
    {
        // Job-shared leather set (low levels) + Beetle/Spike at 21 underneath; DRG pieces override last.
        var basePieces = WarBrain.Armor.Select(g => (g.slot, (uint)g.item))
            .Concat(WarBrain.Armor21.Select(g => (g.slot, (uint)g.item)));
        // WAR phases keep the proven Great Axe kit (owned already).
        (byte slot, ushort item)? phase = p.World.MainJob == Job.War ? (EquipSlot.Main, WarBrain.Weapon20) : null;
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, Gear, ct, phase, basePieces);
        Log.Info($"[drg] equipped {n}/{total} (job {p.World.MainJob} lvl {p.World.MainJobLevel}, polearm={gear.SkillLevel(PolearmSkill)})");
    }
}
