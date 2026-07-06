namespace XiHeadless.Brains;

/// RDM leveling brain — BASIC job (no unlock quest), ThfBrain's shape: change main to RDM, nursery in
/// West Sarutabaruta until the canyon is survivable, then the shared solo LevelGrind on the Windurst path.
/// Config only: the network-gnomes RDM gear ladder (sword + mage cloth), sword WS, mage rest (MP too).
public sealed class RdmBrain(
    IPerception p, INavigation nav, ICombat combat, IZoning zoning, IGear gear,
    IAuctionHouse ah, IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs) : IBrain
{
    const byte SwordSkill = 3;
    const string AhZone = "Windurst Woods";   // home-nation AH (char is Windurst)

    // Ascending by level so later (better) pieces override earlier ones in the same slot during Equip.
    static readonly (ushort item, byte slot, byte lvl)[] Gear =
    {
        (16610, EquipSlot.Main, 1),    // Wax Sword +1
        (12526, EquipSlot.Head, 1),    // Copper Hairpin +1
        (13093, EquipSlot.Neck, 7),    // Justice Badge
        (12568, EquipSlot.Body, 7),    // Leather Vest
        (12696, EquipSlot.Hands, 7),   // Leather Gloves
        (12824, EquipSlot.Legs, 7),    // Leather Trousers
        (12952, EquipSlot.Feet, 7),    // Leather Highboots
        (12529, EquipSlot.Head, 10),   // Brass Hairpin +1
        (12560, EquipSlot.Body, 10),   // Scale Mail
        (13379, EquipSlot.Ear1, 10),   // Energy Earring
        (13548, EquipSlot.Ring1, 10),  // Astral Ring
        (16611, EquipSlot.Main, 11),   // Bee Spatha +1
        (13211, EquipSlot.Waist, 14),  // Friar's Rope
        (13592, EquipSlot.Back, 17),   // Lizard Mantle
        (16621, EquipSlot.Main, 18),   // Flame Sword
        (17708, EquipSlot.Main, 19),   // Auriga Xiphos
        (12531, EquipSlot.Head, 20),   // Silver Hairpin +1
        (13113, EquipSlot.Neck, 20),   // Black Neckerchief
        (14447, EquipSlot.Body, 20),   // Baron's Saio
        (14054, EquipSlot.Hands, 20),  // Baron's Cuffs
        (12917, EquipSlot.Legs, 20),   // Mage's Slacks
        (13048, EquipSlot.Feet, 20),   // Mage's Sandals
        (15224, EquipSlot.Head, 24),   // Empress Hairpin
        (14025, EquipSlot.Hands, 27),  // Devotee's Mitts +1
    };

    // Full arc via the shared JobLifecycle: RDM is a basic job (no unlock) — level it from 1 with a MNK sub
    // kept at half via the seesaw (MNK = hand-to-hand, so the sub needs no extra weapon). The level-gated
    // nursery (West/East Sarutabaruta -> Tahrongi -> nation path), which replaces the old hand-rolled
    // "nursery until 14", plus baby phase + safe recovery come for free.
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, null, null, null,
            new JobLifecycle.Config
            {
                MainJob = Job.Rdm, SubJob = Job.Mnk, Advanced = false,
                GrindCfgFor = GrindCfg, Tag = "rdm",
            }).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job) => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear).ToArray(),   // cheap-first: the array is ascending by level
        Keep = GearRoutines.KeepSet(Gear, 1126, 1127),
        Equip = Equip,
        WepSkillForLevel = _ => job == Job.Mnk ? (byte)1 : SwordSkill,   // MNK sub melees hand-to-hand
        ConMin = 1, ConMax = 3,
        CleanPullNeighborCon = 3,
        SkipMobNames = new[] { "Saplin", "Mandragora" },
        RestHpTrigger = 70, RestHpTarget = 90,
        RestMpPct = job == Job.Rdm ? 40 : 0,             // RDM casts — rest MP back; the MNK sub is pure melee
        Tag = "rdm",
    };

    async Task Equip(CancellationToken ct)
    {
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, Gear, ct);
        Console.WriteLine($"[rdm] equipped {n}/{total} (lvl {p.World.MainJobLevel}, sword={gear.SkillLevel(SwordSkill)})");
    }
}
