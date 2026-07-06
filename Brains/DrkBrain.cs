namespace XiHeadless.Brains;

/// DARK KNIGHT life path (PldBrain's 3-phase shape — brain = config only):
///   1. Prereq sub: WAR to 30 as MAIN.
///   2. Unlock: "Blade of Darkness" via QuestRunner (QuestDefs.Unlock[Job.Drk]).
///   3. Seesaw: level DRK main / WAR sub (JobLeveling switches whenever WAR < ceil(DRK/2)).
/// Unlock note: the KillWith(16607, 100) step requires 100 kills wielding Chaosbringer where the KILLING
/// BLOW must NOT be a weapon skill. QuestRunner.KillWith engages with plain auto-attack (no KillRoutine,
/// no weapon skills), so it is already rule-compliant. TODO: KillWith counts a kill when the target's HP
/// hits 0 nearby — it doesn't verify WE landed the blow, so the 100-count may drift on contested mobs.
public sealed class DrkBrain(
    IPerception p, INavigation nav, ICombat combat, IZoning zoning, IGear gear, IAuctionHouse ah,
    IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, IQuests quests, ITradeNpc trade, IEvents events) : IBrain
{
    const byte ScytheSkill = 7;               // LSB skill enum (Scythe=7 — verified vs the generated WS table)
    const byte GreatAxeSkill = 6;             // WAR prereq/sub phases ride the proven Great Axe kit
    const ushort Chaosbringer = 16607;        // quest scythe — never sell
    const string AhZone = "Windurst Woods";   // home-nation AH (char is Windurst)

    // Ascending by level so later pieces override earlier ones in the same slot.
    // (Flame Claymore 16588 is a lv13 GREAT SWORD alt — omitted: scythe is the trained skill.)
    static readonly (ushort item, byte slot, byte lvl)[] Gear =
    {
        (16637, EquipSlot.Main, 5),    // Deathbringer (scythe)
        (15351, EquipSlot.Feet, 7),    // Bounding Boots
        (14803, EquipSlot.Ear1, 10),   // Optical Earring
        (13613, EquipSlot.Back, 12),   // Traveler's Mantle
        (12799, EquipSlot.Hands, 14),  // Battle Gloves
        (13522, EquipSlot.Ring1, 14),  // Courage Ring
        (18039, EquipSlot.Main, 15),   // Republic Scythe
        (13240, EquipSlot.Waist, 15),  // Warrior's Belt +1
        (13225, EquipSlot.Waist, 18),  // Brave Belt
        (16773, EquipSlot.Main, 20),   // Cruel Scythe
        (14314, EquipSlot.Legs, 21),   // Garrison Hose
        (13061, EquipSlot.Neck, 21),   // Spike Necklace
        (13326, EquipSlot.Ear1, 21),   // Beetle Earring +1
        (15224, EquipSlot.Head, 24),   // Empress Hairpin
        (14433, EquipSlot.Body, 25),   // Shade Harness +1
        (16784, EquipSlot.Main, 27),   // Frostreaper
    };

    // Full arc (sub WAR->30, unlock, seesaw DRK/WAR) via the shared JobLifecycle — brain = config only.
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, quests, trade, events,
            new JobLifecycle.Config
            {
                MainJob = Job.Drk, SubJob = Job.War, Advanced = true,
                UnlockSteps = QuestDefs.Unlock[Job.Drk],   // "Blade of Darkness"; KillWith(Chaosbringer,100) is rule-compliant
                GrindCfgFor = GrindCfg, Tag = "drk",
            }).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job) => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear).ToArray(),
        Keep = GearRoutines.KeepSet(Gear, 1126, 1127, Chaosbringer),
        Equip = Equip,
        WepSkillForLevel = _ => job == Job.War ? GreatAxeSkill : ScytheSkill,
        ConMin = 1, ConMax = 3,
        CleanPullNeighborCon = 3,
        SkipMobNames = new[] { "Saplin", "Mandragora" },
        RestHpTrigger = 70, RestHpTarget = 90,
        Tag = "drk",
    };

    async Task Equip(CancellationToken ct)
    {
        (byte slot, ushort item)? phase = p.World.MainJob == Job.War ? (EquipSlot.Main, WarBrain.Weapon20) : null;
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, Gear, ct, phase);
        Console.WriteLine($"[drk] equipped {n}/{total} (job {p.World.MainJob} lvl {p.World.MainJobLevel}, scythe={gear.SkillLevel(ScytheSkill)})");
    }
}
