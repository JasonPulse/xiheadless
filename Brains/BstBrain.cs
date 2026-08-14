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
        // BODY/LEGS/FEET 1-24 (was EMPTY until the lv25 Shade set — a torso/leg/foot-naked BST died 22x,
        // user: BST is a solo job, deaths = equipment). All verified BST-wearable vs item_equipment.sql;
        // cheap Bronze base (affordable even broke) -> Scale -> Bone -> Beetle -> Chain. Shade (25) overrides.
        (12576, EquipSlot.Body, 1),    // Bronze Harness
        (12568, EquipSlot.Body, 7),    // Leather Vest
        (12560, EquipSlot.Body, 10),   // Scale Mail
        (12582, EquipSlot.Body, 16),   // Bone Harness
        (12583, EquipSlot.Body, 21),   // Beetle Harness
        (12552, EquipSlot.Body, 24),   // Chainmail
        (12832, EquipSlot.Legs, 1),    // Bronze Subligar
        (12824, EquipSlot.Legs, 7),    // Leather Trousers
        (12816, EquipSlot.Legs, 10),   // Scale Cuisses
        (12834, EquipSlot.Legs, 16),   // Bone Subligar
        (12835, EquipSlot.Legs, 21),   // Beetle Subligar
        (12808, EquipSlot.Legs, 24),   // Chain Hose
        (12960, EquipSlot.Feet, 1),    // Bronze Leggings
        (12952, EquipSlot.Feet, 7),    // Leather Highboots
        (12944, EquipSlot.Feet, 10),   // Scale Greaves
        (12966, EquipSlot.Feet, 16),   // Bone Leggings
        (12967, EquipSlot.Feet, 21),   // Beetle Leggings
        (12936, EquipSlot.Feet, 24),   // Greaves
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

    // CHARM IS BST'S COMBAT (user: "it should have charm already" — a rod-poking BST was the red flag).
    // Each fight beat: if a second mob stands near, Charm it (Ability 52 self-gates on job/recast; a resist
    // just retries next beat) and Fight (69) sics whatever pet we hold onto the target. No pet-state packet
    // is parsed yet, so both fire blind and no-op harmlessly when petless/petful — the server arbitrates.
    async Task BstRotation(uint mob, int con, CancellationToken ct)
    {
        if (con < 1) return;
        var petCand = p.Nearest(e => e.IsMob && e.Hpp == 100 && e.Id != mob
            && CombatRoutines.NotObject(e)
            && !CombatRoutines.SleepLockMobs.Any(n => e.Name.Contains(n, StringComparison.OrdinalIgnoreCase))
            && p.DistanceTo(e.X, e.Z) <= 12f);
        if (petCand is not null && await combat.UseAbility(Ability.Charm, petCand.Id, ct))
            Log.Info($"[bst] Charm -> '{petCand.Name}'");
        if (await combat.UseAbility(Ability.Fight, mob, ct))
            Log.Info("[bst] Fight! (pet on the target)");
    }

    LevelGrind.Config GrindCfg(byte job) => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear).ToArray(),
        GearTable = Gear,
        Keep = GearRoutines.KeepSet(Gear, 1126, 1127, GausebitWildgrass),
        Equip = Equip,
        WepSkillForLevel = _ => job == Job.Whm ? ClubSkill : AxeSkill,
        UseAbilities = job == Job.Bst ? BstRotation : LevelGrind.Config.NoAbilities,   // WHM stints keep the generic kit
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
