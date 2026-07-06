namespace XiHeadless.Brains;

/// NINJA life path (PldBrain's 3-phase shape — brain = config only):
///   1. Prereq sub: WAR to 30 as MAIN.
///   2. Unlock: "Ayame and Kaede" via QuestRunner (QuestDefs.Unlock[Job.Nin]).
///   3. Seesaw: level NIN main / WAR sub (JobLeveling switches whenever WAR < ceil(NIN/2)).
/// Unlock TODO: the Korroloka Tunnel qm2 examine spawns 3x Korroloka Leech NMs that must be KILLED
/// between the two examines — QuestRunner has no "clear the spawned NMs" step, so the second examine
/// will fail until that fight is handled (live work needed).
public sealed class NinBrain(
    IPerception p, INavigation nav, ICombat combat, IZoning zoning, IGear gear, IAuctionHouse ah,
    IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, IQuests quests, ITradeNpc trade, IEvents events) : IBrain
{
    const byte KatanaSkill = 9;               // LSB skill enum (Katana=9 — verified vs the generated WS table)
    const byte GreatAxeSkill = 6;             // WAR prereq/sub phases ride the proven Great Axe kit
    const string AhZone = "Windurst Woods";   // home-nation AH (char is Windurst)

    // Ascending by level so later pieces override earlier ones in the same slot.
    static readonly (ushort item, byte slot, byte lvl)[] Gear =
    {
        (16914, EquipSlot.Main, 1),    // Kunai +1
        (16918, EquipSlot.Main, 7),    // Wakizashi +1
        (12542, EquipSlot.Head, 7),    // Leather Bandana +1
        (12599, EquipSlot.Body, 7),    // Leather Vest +1
        (12784, EquipSlot.Hands, 7),   // Leather Gloves +1
        (12908, EquipSlot.Legs, 7),    // Leather Trousers +1
        (13014, EquipSlot.Feet, 7),    // Leaping Boots
        (13183, EquipSlot.Neck, 7),    // Wing Pendant
        (13613, EquipSlot.Back, 12),   // Traveler's Mantle
        (16920, EquipSlot.Main, 13),   // Shinobi-gatana +1
        (13521, EquipSlot.Ring1, 14),  // Reflex Ring
        (13240, EquipSlot.Waist, 15),  // Warrior's Belt +1
        (13716, EquipSlot.Body, 16),   // Bone Harness +1
        (12788, EquipSlot.Hands, 16),  // Bone Mittens +1
        (12912, EquipSlot.Legs, 16),   // Bone Subligar +1
        (13042, EquipSlot.Feet, 16),   // Bone Leggings +1
        (13362, EquipSlot.Ear1, 16),   // Bone Earring +1
        (16917, EquipSlot.Main, 19),   // Suzume
        (17786, EquipSlot.Main, 20),   // Ganko
        (13827, EquipSlot.Head, 21),   // Beetle Mask +1
        (13717, EquipSlot.Body, 21),   // Beetle Harness +1
        (12789, EquipSlot.Hands, 21),  // Beetle Mittens +1
        (12913, EquipSlot.Legs, 21),   // Beetle Subligar +1
        (13043, EquipSlot.Feet, 21),   // Battle Leggings +1
        (13061, EquipSlot.Neck, 21),   // Spike Necklace
        (15224, EquipSlot.Head, 24),   // Empress Hairpin
        (17777, EquipSlot.Main, 24),   // Hibari +1
    };

    // Full arc (sub WAR->30, unlock, seesaw NIN/WAR) via the shared JobLifecycle — brain = config only.
    // CAVEAT (unchanged): the Korroloka qm2 examines spawn 3x Korroloka Leech NMs that must be killed
    // between the two examines; QuestRunner has no "clear the spawned NMs" step, so the unlock will fail
    // gracefully (hold + level WAR) until that fight is handled.
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, quests, trade, events,
            new JobLifecycle.Config
            {
                MainJob = Job.Nin, SubJob = Job.War, Advanced = true,
                UnlockSteps = QuestDefs.Unlock[Job.Nin],   // "Ayame and Kaede"
                GrindCfgFor = GrindCfg, Tag = "nin",
            }).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job) => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear).ToArray(),
        Keep = GearRoutines.KeepSet(Gear, 1126, 1127),
        Equip = Equip,
        WepSkillForLevel = _ => job == Job.War ? GreatAxeSkill : KatanaSkill,
        ConMin = 1, ConMax = 3,
        CleanPullNeighborCon = 3,
        SkipMobNames = new[] { "Saplin", "Mandragora" },
        RestHpTrigger = 70, RestHpTarget = 90,
        Tag = "nin",
    };

    async Task Equip(CancellationToken ct)
    {
        (byte slot, ushort item)? phase = p.World.MainJob == Job.War ? (EquipSlot.Main, WarBrain.Weapon20) : null;
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, Gear, ct, phase);
        Log.Info($"[nin] equipped {n}/{total} (job {p.World.MainJob} lvl {p.World.MainJobLevel}, katana={gear.SkillLevel(KatanaSkill)})");
    }
}
