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

    // The network-gnomes MNK guide progression, lv1-75. Every id VERIFIED MNK-wearable + level vs this
    // server's item_equipment.sql. Ascending by level so later pieces override earlier in the same slot.
    // Main is ALWAYS hand-to-hand (MNK's bread and butter): Cesti -> Knuckles -> Patas -> Cross-Counters ->
    // Sainti -> Baghnakhs lines. AH-buying is capped at BuyMaxLevel (below); the 56+ AF/drop/HQ/relic tier
    // here is for EQUIP-when-owned (quests/drops), not purchase.
    static readonly (ushort item, byte slot, byte lvl)[] Gear =
    {
        // ---- weapon (H2H) ----
        (16385, EquipSlot.Main, 1),    // Cesti
        (16390, EquipSlot.Main, 5),    // Bronze Knuckles
        (16391, EquipSlot.Main, 9),    // Brass Knuckles
        (17500, EquipSlot.Main, 15),   // Republic Knuckles
        (16392, EquipSlot.Main, 20),   // Metal Knuckles
        (16406, EquipSlot.Main, 24),   // Baghnakhs
        (18359, EquipSlot.Main, 30),   // Boreas Cesti
        (16393, EquipSlot.Main, 38),   // Mythril Knuckles
        (16419, EquipSlot.Main, 48),   // Patas
        (17472, EquipSlot.Main, 50),   // Cross-Counters
        (18362, EquipSlot.Main, 55),   // Sainti
        (18748, EquipSlot.Main, 70),   // Hades Sainti
        (16395, EquipSlot.Main, 71),   // Diamond Knuckles
        (17509, EquipSlot.Main, 73),   // Destroyers
        (16396, EquipSlot.Main, 75),   // Koenig's Knuckles
        // ---- head ----
        (12448, EquipSlot.Head, 1),    // Bronze Cap
        (12456, EquipSlot.Head, 8),    // Hachimaki
        (12501, EquipSlot.Head, 11),   // Monk's Headgear
        (12484, EquipSlot.Head, 20),   // Mercenary's Hachimaki
        (16133, EquipSlot.Head, 30),   // Fancy Spectacles
        (15184, EquipSlot.Head, 41),   // Voyager Sallet
        (16065, EquipSlot.Head, 50),   // Storm Zucchetto
        (12512, EquipSlot.Head, 56),   // Temple Crown (AF)
        (13915, EquipSlot.Head, 70),   // Optical Hat
        (15270, EquipSlot.Head, 75),   // Walahra Turban
        // ---- body ----
        (12576, EquipSlot.Body, 1),    // Bronze Harness
        (12584, EquipSlot.Body, 8),    // Kenpogi
        (12590, EquipSlot.Body, 13),   // Power Gi
        (12653, EquipSlot.Body, 20),   // Mercenary's Gi
        (12579, EquipSlot.Body, 57),   // Scorpion Harness
        (12639, EquipSlot.Body, 58),   // Temple Cyclas (AF)
        (14536, EquipSlot.Body, 67),   // Arakan Samue
        (14554, EquipSlot.Body, 75),   // Usukane Haramaki
        // ---- hands ----
        (12704, EquipSlot.Hands, 1),   // Bronze Mittens
        (12712, EquipSlot.Hands, 8),   // Tekko
        (12799, EquipSlot.Hands, 14),  // Battle Gloves
        (13952, EquipSlot.Hands, 34),  // Ochiudo's Kote
        (12198, EquipSlot.Hands, 71),  // Shikkoku Kote
        (14969, EquipSlot.Hands, 75),  // Usukane Gote
        // ---- legs ----
        (12832, EquipSlot.Legs, 1),    // Bronze Subligar
        (12840, EquipSlot.Legs, 8),    // Sitabaki
        (12855, EquipSlot.Legs, 20),   // Mercenary's Sitabaki
        (12923, EquipSlot.Legs, 37),   // Jujitsu Sitabaki
        (12838, EquipSlot.Legs, 57),   // Scorpion Subligar
        (14215, EquipSlot.Legs, 60),   // Temple Hose (AF)
        (15633, EquipSlot.Legs, 75),   // Usukane Hizayoroi
        // ---- feet ----
        (12960, EquipSlot.Feet, 1),    // Bronze Leggings
        (12968, EquipSlot.Feet, 8),    // Kyahan
        (14090, EquipSlot.Feet, 52),   // Temple Gaiters (AF)
        (12963, EquipSlot.Feet, 57),   // Scorpion Leggings
        (14168, EquipSlot.Feet, 70),   // Dune Boots
        (15719, EquipSlot.Feet, 75),   // Usukane Sune-Ate
        // ---- neck ----
        (13183, EquipSlot.Neck, 7),    // Wing Pendant
        (13061, EquipSlot.Neck, 21),   // Spike Necklace
        (13119, EquipSlot.Neck, 24),   // Tiger Stole
        (13056, EquipSlot.Neck, 33),   // Peacock Charm (accuracy)
        (13128, EquipSlot.Neck, 59),   // Spectacles
        (15512, EquipSlot.Neck, 73),   // Faith Torque
        // ---- waist ----
        (13184, EquipSlot.Waist, 1),   // White Belt
        (13201, EquipSlot.Waist, 18),  // Purple Belt
        (13202, EquipSlot.Waist, 40),  // Brown Belt
        (13231, EquipSlot.Waist, 48),  // Life Belt
        (15943, EquipSlot.Waist, 54),  // Virtuoso Belt
        (13186, EquipSlot.Waist, 70),  // Black Belt
        // ---- back ----
        (13594, EquipSlot.Back, 4),    // Rabbit Mantle
        (13686, EquipSlot.Back, 47),   // Jaguar Mantle
        (13645, EquipSlot.Back, 61),   // Amemet Mantle +1
        (13690, EquipSlot.Back, 71),   // Forager's Mantle
        // ---- ears / rings / ammo ----
        (14803, EquipSlot.Ear1, 10),   // Optical Earring
        (14813, EquipSlot.Ear1, 75),   // Brutal Earring
        (15543, EquipSlot.Ring1, 30),  // Rajas Ring
        (15548, EquipSlot.Ring1, 75),  // Mars's Ring
        (17296, EquipSlot.Ammo, 1),    // Pebble
        (17298, EquipSlot.Ammo, 35),   // Tathlum
        (19212, EquipSlot.Ammo, 70),   // Black Tathlum
    };

    const byte BuyMaxLevel = 50;   // only AH-buy through the standard leveling tier; 56+ (AF/drop/HQ/relic) isn't listed

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
        BuyItems = GearRoutines.BuyList(Gear.Where(g => g.lvl <= BuyMaxLevel).ToArray()).ToArray(),
        GearTable = Gear,
        Keep = GearRoutines.KeepSet(Gear, 1126, 1127),   // seals are never junk (full table kept — never sell an owned AF/drop)
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
