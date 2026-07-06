namespace XiHeadless.Brains;

/// DANCER life path (PldBrain's 3-phase shape — brain = config only):
///   1. Prereq sub: WAR to 30 as MAIN (DNC/WAR per the roster).
///   2. Unlock: "Lakeside Minuet" (QuestDefs.Unlock[Dnc]: Upper Jeuno -> S.San d'Oria -> Jugner Forest [S]).
///      TODO(WotG): the Jugner_Forest_[S] step needs a Cavernous Maw crossing — if the zone graph has no
///      edge into the [S] past zones the runner will stall there; coded, verify routing live.
///   3. Seesaw: level DNC main / WAR sub (JobLeveling switches whenever WAR < ceil(DNC/2)).
/// Gear: network-gnomes DNC guide lv1-29 bracket (dagger + light DD), ids verified vs the server DB.
public sealed class DncBrain(
    IPerception p, INavigation nav, ICombat combat, IZoning zoning, IGear gear, IAuctionHouse ah,
    IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, IQuests quests, ITradeNpc trade, IEvents events) : IBrain
{
    const byte DaggerSkill = 2;
    const byte GreatAxeSkill = 6;             // WAR prereq/sub phases ride the proven Great Axe kit
    const string AhZone = "Windurst Woods";   // home-nation AH (char is Windurst)

    // Ascending by level so later (better) pieces override earlier ones in the same slot.
    static readonly (ushort item, byte slot, byte lvl)[] Gear =
    {
        (16465, EquipSlot.Main, 1),    // Bronze Knife
        (12526, EquipSlot.Head, 1),    // Copper Hairpin +1
        (12568, EquipSlot.Body, 7),    // Leather Vest
        (12696, EquipSlot.Hands, 7),   // Leather Gloves
        (12824, EquipSlot.Legs, 7),    // Leather Trousers
        (15351, EquipSlot.Feet, 7),    // Bounding Boots
        (16614, EquipSlot.Main, 13),   // Knife +1
        (12799, EquipSlot.Hands, 14),  // Battle Gloves
        (13522, EquipSlot.Ring1, 14),  // Courage Ring
        (13240, EquipSlot.Waist, 15),  // Warrior's Belt +1
        (13826, EquipSlot.Head, 16),   // Bone Mask +1
        (13716, EquipSlot.Body, 16),   // Bone Harness +1
        (12912, EquipSlot.Legs, 16),   // Bone Subligar +1
        (16304, EquipSlot.Neck, 18),   // Focus Collar +1
        (16746, EquipSlot.Main, 20),   // Mercenary's Knife
        (16742, EquipSlot.Main, 21),   // Poison Knife +1
        (13061, EquipSlot.Neck, 21),   // Spike Necklace
        (13326, EquipSlot.Ear1, 21),   // Beetle Earring +1
        (13827, EquipSlot.Head, 21),   // Beetle Mask +1
        (13717, EquipSlot.Body, 21),   // Beetle Harness +1
        (12913, EquipSlot.Legs, 21),   // Beetle Subligar +1
        (15224, EquipSlot.Head, 24),   // Empress Hairpin
        (13631, EquipSlot.Back, 24),   // Nomad's Mantle
        (16755, EquipSlot.Main, 28),   // Archer's Knife
        (13366, EquipSlot.Ear2, 29),   // Dodge Earring
    };

    // Full arc (sub WAR->30, unlock, seesaw DNC/WAR) via the shared JobLifecycle — brain = config only.
    // Stealth is stocked at the home AH and the brain stealth-treks to Upper Jeuno (the chain start) before
    // the quest steps. TODO(WotG, unchanged): the Jugner_Forest_[S] step needs a Cavernous Maw crossing; if
    // the zone graph has no [S] edge the unlock fails gracefully (hold + level WAR).
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, quests, trade, events,
            new JobLifecycle.Config
            {
                MainJob = Job.Dnc, SubJob = Job.War, Advanced = true,
                UnlockSteps = QuestDefs.Unlock[Job.Dnc],   // "Lakeside Minuet"
                StealthUnlock = true, UnlockTrekZone = "Upper_Jeuno", UnlockTrekZoneId = 244,
                GrindCfgFor = GrindCfg, Tag = "dnc",
            }).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job) => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear).ToArray(),
        Keep = GearRoutines.KeepSet(Gear, 1126, 1127),
        Equip = Equip,
        WepSkillForLevel = _ => job == Job.War ? GreatAxeSkill : DaggerSkill,
        ConMin = 1, ConMax = 3,
        CleanPullNeighborCon = 3,
        SkipMobNames = new[] { "Saplin", "Mandragora" },
        RestHpTrigger = 70, RestHpTarget = 90,
        Tag = "dnc",
    };

    async Task Equip(CancellationToken ct)
    {
        (byte slot, ushort item)? phase = p.World.MainJob == Job.War ? (EquipSlot.Main, WarBrain.Weapon20) : null;
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, Gear, ct, phase);
        Console.WriteLine($"[dnc] equipped {n}/{total} (job {p.World.MainJob} lvl {p.World.MainJobLevel}, dagger={gear.SkillLevel(DaggerSkill)})");
    }
}
