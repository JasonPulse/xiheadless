namespace XiHeadless.Brains;

/// BEASTMASTER life path (PldBrain's 3-phase shape — brain = config only):
///   1. Prereq sub: WHM to 30 as MAIN.
///   2. Unlock: prereq chain (Chocobo's Wounds -> Save My Son) + "Path of the Beastmaster" via
///      QuestRunner (QuestDefs.Prereqs[Job.Bst] + Unlock[Job.Bst]).
///   3. Seesaw: level BST main / WHM sub (JobLeveling switches whenever WHM < ceil(BST/2)).
/// Unlock TODOs (live work needed — QuestRunner has no wait primitive):
///   * Chocobo's Wounds: the 6 gausebit wildgrass (534) feeds need ~45s BETWEEN feeds (the server
///     rejects an early feed); the back-to-back Trade steps will be refused — needs a wait step.
///     Also needs 6x item 534 in the bag beforehand (not on the AH BuyItems list — source live).
///   * Save My Son: the Qufim Nightflowers examine only works at game NIGHT (21:30-05:40) — needs a
///     game-clock wait/retry loop.
public sealed class BstBrain(
    IPerception p, INavigation nav, ICombat combat, IMagic magic, IZoning zoning, IGear gear, IAuctionHouse ah,
    IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, IQuests quests, ITradeNpc trade, IEvents events, IChat chat, ILifecycle lifecycle, IParty party) : IBrain
{
    const byte AxeSkill = 5;
    const byte ClubSkill = 11;                // WHM prereq/sub phases melee with club (WhmBrain's proven skill)
    const ushort GausebitWildgrass = 534;     // Chocobo's Wounds feed item — never sell
    const string AhZone = "Windurst Woods";   // home-nation AH (char is Windurst)

    // Ascending by level so later pieces override earlier ones in the same slot.
    static readonly (ushort item, byte slot, byte lvl)[] Gear =
    {
        (16640, EquipSlot.Main, 1),    // Bronze Axe
        (16279, EquipSlot.Neck, 3),    // Pile Chain
        (12290, EquipSlot.Sub, 8),     // Maple Shield
        (14803, EquipSlot.Ear1, 10),   // Optical Earring
        (13071, EquipSlot.Neck, 11),   // Scale Gorget
        (15218, EquipSlot.Head, 11),   // Entrancing Ribbon
        (16783, EquipSlot.Main, 14),   // Plantreaper
        (12316, EquipSlot.Sub, 14),    // Fish Scale Shield
        (13833, EquipSlot.Head, 14),   // Noble's Ribbon
        (12799, EquipSlot.Hands, 14),  // Battle Gloves
        (13522, EquipSlot.Ring1, 14),  // Courage Ring
        (13240, EquipSlot.Waist, 15),  // Warrior's Belt +1
        (16643, EquipSlot.Main, 20),   // Battleaxe
        (13061, EquipSlot.Neck, 21),   // Spike Necklace
        (13326, EquipSlot.Ear1, 21),   // Beetle Earring +1
        (17942, EquipSlot.Main, 25),   // Tomahawk
        (15169, EquipSlot.Head, 25),   // Shade Tiara +1
        (14862, EquipSlot.Hands, 25),  // Shade Mittens +1
        (14433, EquipSlot.Body, 25),   // Shade Harness +1
        (14331, EquipSlot.Legs, 25),   // Shade Tights +1
        (15319, EquipSlot.Feet, 25),   // Shade Leggings +1
        (16672, EquipSlot.Main, 26),   // Tigerhunter
        (13094, EquipSlot.Neck, 27),   // Flower Necklace
    };

    // Full arc (sub WHM->30, unlock chain, seesaw BST/WHM) via the shared JobLifecycle — brain = config only.
    // CAVEAT (unchanged): the prereqs (Chocobo's Wounds feed cooldowns; Save My Son's NIGHT-only examine)
    // need wait primitives QuestRunner lacks, so the unlock will fail gracefully (hold + level WHM) until
    // those land. 6x Gausebit Wildgrass (534) for the feeds must be sourced live (not on the AH).
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, quests, trade, events,
            new JobLifecycle.Config
            {
                MainJob = Job.Bst, SubJob = Job.Whm, Advanced = true,
                UnlockSteps = QuestDefs.Prereqs[Job.Bst].Concat(QuestDefs.Unlock[Job.Bst]).ToArray(),
                GrindCfgFor = GrindCfg, Tag = "bst",
            }, lifecycle: lifecycle, chat: chat, magic: magic, party: party).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job) => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear).ToArray(),
        Keep = GearRoutines.KeepSet(Gear, 1126, 1127, GausebitWildgrass),
        Equip = Equip,
        WepSkillForLevel = _ => job == Job.Whm ? ClubSkill : AxeSkill,
        ConMin = 1, ConMax = 3,
        CleanPullNeighborCon = 3,
        RestHpTrigger = 70, RestHpTarget = 90,
        RestMpPct = job == Job.Whm ? 40 : 0,   // the WHM phase casts; BST main is melee
        Tag = "bst",
    };

    async Task Equip(CancellationToken ct)
    {
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, Gear, ct);
        Log.Info($"[bst] equipped {n}/{total} (job {p.World.MainJob} lvl {p.World.MainJobLevel}, axe={gear.SkillLevel(AxeSkill)})");
    }
}
