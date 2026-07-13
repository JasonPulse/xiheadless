namespace XiHeadless.Brains;

/// RANGER life path (PldBrain's 3-phase shape — brain = config only):
///   1. Prereq sub: WAR to 30 as MAIN (sub kept from the basic six per the user).
///   2. Unlock: "The Fanged One" via QuestRunner (QuestDefs.Unlock[Job.Rng]).
///   3. Seesaw: level RNG main / WAR sub (JobLeveling switches whenever WAR < ceil(RNG/2)).
/// Unlock TODO: Old Sabertooth must die UNTOUCHED from its own poison — the KillWith(0,1) row in
/// QuestDefs is a PLACEHOLDER; the real action is "spawn it, then do NOT attack" (live work needed).
/// Combat note: the engine has no ranged auto-attack/ranged-WS support yet, so fights are MELEE (axe,
/// then Archer's Knife at 28); the bow/arrows are equipped for stats + future ranged support.
/// TODO: ammo restock policy (Wooden Arrow 17318 is consumable — needs several 99-stacks and a
/// rebuy-when-low pass; validate on a live run).
public sealed class RngBrain(
    IPerception p, INavigation nav, ICombat combat, IMagic magic, IZoning zoning, IGear gear, IAuctionHouse ah,
    IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, IQuests quests, ITradeNpc trade, IEvents events) : IBrain
{
    const byte ArcherySkill = 25;             // LSB skill enum (Archery=25) — unused until ranged support lands
    const byte AxeSkill = 5;                  // melee carry 1-27
    const byte DaggerSkill = 2;               // Archer's Knife bracket (28+)
    const byte GreatAxeSkill = 6;             // WAR prereq/sub phases ride the proven Great Axe kit
    const string AhZone = "Windurst Woods";   // home-nation AH (char is Windurst)

    // Ascending by level so later pieces override earlier ones in the same slot.
    static readonly (ushort item, byte slot, byte lvl)[] Gear =
    {
        (16640, EquipSlot.Main, 1),    // Bronze Axe
        (17175, EquipSlot.Ranged, 1),  // Shortbow +1
        (17318, EquipSlot.Ammo, 1),    // Wooden Arrow (consumable — see restock TODO above)
        (12472, EquipSlot.Head, 1),    // Circlet
        (12600, EquipSlot.Body, 1),    // Robe
        (12728, EquipSlot.Hands, 1),   // Cuffs
        (12856, EquipSlot.Legs, 1),    // Slops
        (13199, EquipSlot.Waist, 1),   // Blood Stone
        (13402, EquipSlot.Ear1, 1),    // Cassie Earring
        (17177, EquipSlot.Ranged, 5),  // Longbow +1
        (13060, EquipSlot.Neck, 7),    // Feather Collar +1
        (13014, EquipSlot.Feet, 7),    // Leaping Boots
        (17183, EquipSlot.Ranged, 12), // Hunter's Longbow
        (12778, EquipSlot.Hands, 12),  // Linen Cuffs +1
        (13613, EquipSlot.Back, 12),   // Traveler's Mantle
        (17967, EquipSlot.Main, 13),   // Felling Axe
        (15558, EquipSlot.Ring1, 14),  // Mighty Ring
        (14349, EquipSlot.Body, 15),   // Kingdom Tunic
        (17178, EquipSlot.Ranged, 16), // Power Bow +1
        (14464, EquipSlot.Body, 16),   // Trailer's Tunica
        (13225, EquipSlot.Waist, 18),  // Brave Belt
        (14080, EquipSlot.Feet, 20),   // Strider Boots
        (13061, EquipSlot.Neck, 21),   // Spike Necklace
        (14314, EquipSlot.Legs, 21),   // Garrison Hose
        (15224, EquipSlot.Head, 24),   // Empress Hairpin
        (12922, EquipSlot.Legs, 24),   // Martial Slacks
        (14133, EquipSlot.Feet, 24),   // Winged Boots +1
        (12765, EquipSlot.Hands, 27),  // Wonder Mitts
        (16755, EquipSlot.Main, 28),   // Archer's Knife (dagger — WS skill flips at 28)
    };

    // Full arc (sub WAR->30, unlock, seesaw RNG/WAR) via the shared JobLifecycle — brain = config only.
    // CAVEAT (unchanged): the quest's Old Sabertooth must die UNTOUCHED from its own poison; the KillWith(0,1)
    // row is a placeholder, so the unlock will fail gracefully (hold + level WAR) until that's expressible.
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, quests, trade, events,
            new JobLifecycle.Config
            {
                MainJob = Job.Rng, SubJob = Job.War, Advanced = true,
                UnlockSteps = QuestDefs.Unlock[Job.Rng],   // "The Fanged One"
                GrindCfgFor = GrindCfg, Tag = "rng",
            }, magic: magic).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job) => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear).ToArray(),
        Keep = GearRoutines.KeepSet(Gear, 1126, 1127),
        Equip = Equip,
        // Melee WS off the actually-equipped main: axe until the Archer's Knife bracket flips it to dagger.
        WepSkillForLevel = lvl => job == Job.War ? GreatAxeSkill : lvl >= 28 ? DaggerSkill : AxeSkill,
        ConMin = 1, ConMax = 3,
        CleanPullNeighborCon = 3,
        SkipMobNames = new[] { "Saplin", "Mandragora" },
        RestHpTrigger = 70, RestHpTarget = 90,
        Tag = "rng",
    };

    async Task Equip(CancellationToken ct)
    {
        (byte slot, ushort item)? phase = p.World.MainJob == Job.War ? (EquipSlot.Main, WarBrain.Weapon20) : null;
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, Gear, ct, phase);
        Log.Info($"[rng] equipped {n}/{total} (job {p.World.MainJob} lvl {p.World.MainJobLevel}, axe={gear.SkillLevel(AxeSkill)} archery={gear.SkillLevel(ArcherySkill)})");
    }
}
