namespace XiHeadless.Brains;

/// BLUE MAGE life path (PldBrain's 3-phase shape — brain = config only):
///   1. Prereq sub: WAR to 30 as MAIN (BLU/WAR per the roster).
///   2. Unlock: "An Empty Vessel" (QuestDefs.Unlock[Blu], Aht Urhgan Whitegate + Aydeewa Subterrane).
///      TODO(BLOCKED): Whitegate is a ToAU boat destination — the zone graph has no overland edge to it,
///      so QuestRunner cannot route there on foot; the phase is coded but will fail at travel until ship
///      routing / teleport support exists. The quest also ends with a per-player RANDOM item trade
///      (Siren's Tear 576 / Valkurm Sunsand 503 / Dangruf Stone 553) the step model can't branch on.
///   3. Seesaw: level BLU main / WAR sub (JobLeveling switches whenever WAR < ceil(BLU/2)).
/// Gear: network-gnomes BLU guide lv1-30 bracket (sword + light DD), ids verified vs the server DB.
public sealed class BluBrain(
    IPerception p, INavigation nav, ICombat combat, IMagic magic, IZoning zoning, IGear gear, IAuctionHouse ah,
    IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, IQuests quests, ITradeNpc trade, IEvents events, IChat chat, ILifecycle lifecycle, IParty party) : IBrain
{
    const byte SwordSkill = 3;
    const byte GreatAxeSkill = 6;             // WAR prereq/sub phases ride the proven Great Axe kit
    const string AhZone = "Windurst Woods";   // home-nation AH (char is Windurst)

    // Divination-trade candidates (one is assigned at random) — never sell any of them.
    static readonly ushort[] VesselItems = { 576, 503, 553 };

    // Ascending by level so later (better) pieces override earlier ones in the same slot.
    static readonly (ushort item, byte slot, byte lvl)[] Gear =
    {
        (16610, EquipSlot.Main, 1),    // Wax Sword +1
        (12526, EquipSlot.Head, 1),    // Copper Hairpin +1
        (12568, EquipSlot.Body, 7),    // Leather Vest
        (12696, EquipSlot.Hands, 7),   // Leather Gloves
        (12824, EquipSlot.Legs, 7),    // Leather Trousers
        (15351, EquipSlot.Feet, 7),    // Bounding Boots
        (16625, EquipSlot.Main, 13),   // Scimitar +1
        (12799, EquipSlot.Hands, 14),  // Battle Gloves
        (13522, EquipSlot.Ring1, 14),  // Courage Ring
        (13240, EquipSlot.Waist, 15),  // Warrior's Belt +1
        (13826, EquipSlot.Head, 16),   // Bone Mask +1
        (13716, EquipSlot.Body, 16),   // Bone Harness +1
        (12912, EquipSlot.Legs, 16),   // Bone Subligar +1
        (16621, EquipSlot.Main, 18),   // Flame Sword
        (13061, EquipSlot.Neck, 21),   // Spike Necklace
        (13326, EquipSlot.Ear1, 21),   // Beetle Earring +1
        (14314, EquipSlot.Legs, 21),   // Garrison Hose
        (15224, EquipSlot.Head, 24),   // Empress Hairpin
        (14260, EquipSlot.Legs, 25),   // Republic Subligar
        (13609, EquipSlot.Back, 28),   // Wolf Mantle +1
        (16806, EquipSlot.Main, 30),   // Centurion's Sword
    };

    // Full arc (sub WAR->30, unlock, seesaw BLU/WAR) via the shared JobLifecycle — brain = config only.
    // TODO(BLOCKED): "An Empty Vessel" is in Aht Urhgan Whitegate, a ToAU boat destination with no overland
    // edge in the zone graph — the stealth-trek attempt fails there, so the unlock will fail gracefully
    // (hold + level WAR) until ship routing / teleport support exists. The quest also ends with a per-player
    // RANDOM item trade the step model can't branch on.
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, quests, trade, events,
            new JobLifecycle.Config
            {
                MainJob = Job.Blu, SubJob = Job.War, Advanced = true,
                UnlockSteps = QuestDefs.Unlock[Job.Blu],   // "An Empty Vessel"
                StealthUnlock = true, UnlockTrekZone = "Aht_Urhgan_Whitegate", UnlockTrekZoneId = 50,
                GrindCfgFor = GrindCfg, Tag = "blu",
            }, lifecycle: lifecycle, chat: chat, magic: magic, party: party).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job) => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear).ToArray(),
        Keep = GearRoutines.KeepSet(Gear, new ushort[] { 1126, 1127 }.Concat(VesselItems).ToArray()),
        Equip = Equip,
        WepSkillForLevel = _ => job == Job.War ? GreatAxeSkill : SwordSkill,
        ConMin = 1, ConMax = 3,
        CleanPullNeighborCon = 3,
        RestHpTrigger = 70, RestHpTarget = 90,
        RestMpPct = job == Job.Blu ? 40 : 0,     // BLU casts — rest MP back too
        Tag = "blu",
    };

    async Task Equip(CancellationToken ct)
    {
        (byte slot, ushort item)? phase = p.World.MainJob == Job.War ? (EquipSlot.Main, WarBrain.Weapon20) : null;
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, Gear, ct, phase);
        Log.Info($"[blu] equipped {n}/{total} (job {p.World.MainJob} lvl {p.World.MainJobLevel}, sword={gear.SkillLevel(SwordSkill)})");
    }
}
