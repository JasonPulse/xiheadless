namespace XiHeadless.Brains;

/// MONK fleet main (WarBrain's shape — brain = config only). MNK is a BASIC job (no unlock): JobLifecycle
/// levels it from 1 with a WAR sub kept at half via the seesaw. /WAR is the survivability + puller sub
/// (Provoke, Berserk, Warcry, Double Attack, Defender, +HP) and needs no GM job-grant — it directly fixes
/// the old death cause (a sub-less, gearless MNK bled out 1v1 to con-4 mobs, 2026-07-26). It's the mirror
/// of WarBrain's proven WAR/MNK pairing; swap SubJob to Job.Drg for the guide's top-damage sub.
///
/// Gear: MNK lv1-24 bracket, EVERY id verified MNK-wearable against this server's item_equipment.sql (the
/// Leather set is NOT MNK here — Bronze -> Beetle is). H2H weapon line: Cesti -> Bronze/Brass/Metal Knuckles
/// -> Baghnakhs. The generic JobKits melee rotation fires MNK's JAs (Boost/Focus/Dodge); no per-brain kit.
public sealed class MnkBrain(
    IPerception p, INavigation nav, ICombat combat, IMagic magic, IZoning zoning, IGear gear,
    IAuctionHouse ah, IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, ILifecycle lifecycle, IChat chat, IParty party) : IBrain
{
    const byte H2HSkill = 1;                  // LSB skill enum (Hand-to-Hand = 1)
    const string AhZone = "Windurst Woods";   // Windurst default; nation detection overlays HomeNation/AhZone

    // Ascending by level so later pieces override earlier ones in the same slot. Weapon line first.
    static readonly (ushort item, byte slot, byte lvl)[] Gear =
    {
        (16385, EquipSlot.Main, 1),    // Cesti (H2H)
        (16390, EquipSlot.Main, 5),    // Bronze Knuckles
        (16391, EquipSlot.Main, 9),    // Brass Knuckles
        (16392, EquipSlot.Main, 20),   // Metal Knuckles
        (16406, EquipSlot.Main, 24),   // Baghnakhs
        (12448, EquipSlot.Head, 1),    // Bronze Cap
        (12576, EquipSlot.Body, 1),    // Bronze Harness
        (12704, EquipSlot.Hands, 1),   // Bronze Mittens
        (12832, EquipSlot.Legs, 1),    // Bronze Subligar
        (12960, EquipSlot.Feet, 1),    // Bronze Leggings
        (13594, EquipSlot.Back, 4),    // Rabbit Mantle
        (13093, EquipSlot.Neck, 7),    // Justice Badge
        (12456, EquipSlot.Head, 8),    // Hachimaki
        (12584, EquipSlot.Body, 8),    // Kenpogi
        (12712, EquipSlot.Hands, 8),   // Tekko
        (12840, EquipSlot.Legs, 8),    // Sitabaki
        (12968, EquipSlot.Feet, 8),    // Kyahan
        (12590, EquipSlot.Body, 13),   // Power Gi
        (12498, EquipSlot.Head, 14),   // Cotton Headband
        (12799, EquipSlot.Hands, 14),  // Battle Gloves
        (13211, EquipSlot.Waist, 14),  // Friar's Rope
        (13194, EquipSlot.Waist, 15),  // Warrior's Belt (STR)
        (13592, EquipSlot.Back, 17),   // Lizard Mantle
        (12585, EquipSlot.Body, 18),   // Cotton Dogi
        (12455, EquipSlot.Head, 21),   // Beetle Mask
        (12583, EquipSlot.Body, 21),   // Beetle Harness
        (12711, EquipSlot.Hands, 21),  // Beetle Mittens
        (12835, EquipSlot.Legs, 21),   // Beetle Subligar
        (12967, EquipSlot.Feet, 21),   // Beetle Leggings
        (13061, EquipSlot.Neck, 21),   // Spike Necklace
        (12486, EquipSlot.Head, 24),   // Emperor Hairpin
        (12922, EquipSlot.Legs, 24),   // Martial Slacks
        (13119, EquipSlot.Neck, 24),   // Tiger Stole
        (13631, EquipSlot.Back, 24),   // Nomad's Mantle
    };

    // Full arc via the shared JobLifecycle: basic MNK main (no unlock) + WAR sub kept at half by the seesaw.
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, null, null, null,
            new JobLifecycle.Config
            {
                MainJob = Job.Mnk, SubJob = Job.War, Advanced = false,   // MNK/WAR, mirror of WarBrain
                GrindCfgFor = GrindCfg, Tag = "mnk",
            }, lifecycle, chat: chat, magic: magic, party: party).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job) => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear).ToArray(),
        Keep = GearRoutines.KeepSet(Gear, 1126, 1127),   // seals are never junk
        Equip = Equip,
        WepSkillForLevel = _ => H2HSkill,   // both MNK main and WAR sub swing hand-to-hand here
        ConMin = 1, ConMax = 4,
        RestHpTrigger = 50, RestHpTarget = 80,
        Tag = "mnk",
    };

    async Task Equip(CancellationToken ct)
    {
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, Gear, ct);
        Log.Info($"[mnk] equipped {n}/{total} (job {p.World.MainJob}/{p.World.SubJob} lvl {p.World.MainJobLevel}, h2h={gear.SkillLevel(H2HSkill)})");
    }
}
