namespace XiHeadless.Brains;

/// SAMURAI life path (PldBrain's 3-phase shape — brain = config only):
///   1. Prereq sub: WAR to 30 as MAIN.
///   2. Unlock: "Forge Your Destiny" via QuestRunner (QuestDefs.Unlock[Job.Sam]).
///   3. Seesaw: level SAM main / WAR sub (JobLeveling switches whenever WAR < ceil(SAM/2)).
/// Unlock TODOs (live work needed — the quest is trade/NM/wait-driven):
///   * Two NM fights are spawned by TRADES to quest markers: the Forger via a Lump of Oriental Steel
///     (1151) at Konschtat qm2, and the Guardian Treant via a Hatchet (1021) at Zi'Tah qm2 — the
///     current Examine rows don't perform the trade or fight the spawn.
///   * After the forge turn-in (ev27) there is a ~3 game-day (~2h53m REAL) wait before the final talk
///     — QuestRunner has no wait primitive.
///   * Norg access is a server-enforced prereq (Sea Serpent Grotto route).
public sealed class SamBrain(
    IPerception p, INavigation nav, ICombat combat, IMagic magic, IZoning zoning, IGear gear, IAuctionHouse ah,
    IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, IQuests quests, ITradeNpc trade, IEvents events) : IBrain
{
    const byte GreatKatanaSkill = 10;         // LSB skill enum (GreatKatana=10 — verified vs the generated WS table)
    const byte GreatAxeSkill = 6;             // WAR prereq/sub phases ride the proven Great Axe kit
    const string AhZone = "Windurst Woods";   // home-nation AH (char is Windurst)

    // SAM-specific pieces (ascending by level); the low-level body/hands/legs/feet come from the
    // job-shared WarBrain.Armor leather set (reused like ThfBrain does — never forked).
    static readonly (ushort item, byte slot, byte lvl)[] Gear =
    {
        (17811, EquipSlot.Main, 10),   // Katayama Ichimonji
        (15351, EquipSlot.Feet, 7),    // Bounding Boots
        (14803, EquipSlot.Ear1, 10),   // Optical Earring
        (16978, EquipSlot.Main, 12),   // Uchigatana +1
        (12799, EquipSlot.Hands, 14),  // Battle Gloves
        (13522, EquipSlot.Ring1, 14),  // Courage Ring
        (13240, EquipSlot.Waist, 15),  // Warrior's Belt +1
        (13061, EquipSlot.Neck, 21),   // Spike Necklace
        (14314, EquipSlot.Legs, 21),   // Garrison Hose
        (13326, EquipSlot.Ear1, 21),   // Beetle Earring +1
        (15224, EquipSlot.Head, 24),   // Empress Hairpin
    };

    // Quest materials — never sell (Oriental Steel 1151, Bomb Steel 1152, Sacred Branch 1153,
    // Sacred Sprig 1198, Hatchet 1021).
    static readonly ushort[] QuestItems = { 1151, 1152, 1153, 1198, 1021 };

    // Full arc (sub WAR->30, unlock, seesaw SAM/WAR) via the shared JobLifecycle — brain = config only.
    // CAVEAT (unchanged): "Forge Your Destiny" is trade/NM/wait-driven (Forger + Guardian Treant spawns, a
    // ~3 game-day forge wait, Norg access) that QuestRunner can't fully express, so the unlock will fail
    // gracefully (hold + level WAR) until those are supported.
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, quests, trade, events,
            new JobLifecycle.Config
            {
                MainJob = Job.Sam, SubJob = Job.War, Advanced = true,
                UnlockSteps = QuestDefs.Unlock[Job.Sam],   // "Forge Your Destiny"
                GrindCfgFor = GrindCfg, Tag = "sam",
            }, magic: magic).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job) => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear)
            .Concat(WarBrain.Armor.Select(g => g.item))
            .Concat(WarBrain.Armor21.Select(g => g.item)).ToArray(),
        Keep = new HashSet<ushort>(GearRoutines.BuyList(Gear)
            .Concat(WarBrain.Armor.Select(g => g.item))
            .Concat(WarBrain.Armor21.Select(g => g.item))
            .Concat(QuestItems).Concat(new ushort[] { 1126, 1127 })),
        Equip = Equip,
        WepSkillForLevel = _ => job == Job.War ? GreatAxeSkill : GreatKatanaSkill,
        ConMin = 1, ConMax = 3,
        CleanPullNeighborCon = 3,
        RestHpTrigger = 70, RestHpTarget = 90,
        Tag = "sam",
    };

    async Task Equip(CancellationToken ct)
    {
        // Job-shared leather set (low levels) + Beetle/Spike at 21 underneath; SAM pieces override last.
        var basePieces = WarBrain.Armor.Select(g => (g.slot, (uint)g.item))
            .Concat(WarBrain.Armor21.Select(g => (g.slot, (uint)g.item)));
        // WAR phases keep the proven Great Axe kit (owned already).
        (byte slot, ushort item)? phase = p.World.MainJob == Job.War ? (EquipSlot.Main, WarBrain.Weapon20) : null;
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, Gear, ct, phase, basePieces);
        Log.Info($"[sam] equipped {n}/{total} (job {p.World.MainJob} lvl {p.World.MainJobLevel}, gkatana={gear.SkillLevel(GreatKatanaSkill)})");
    }
}
