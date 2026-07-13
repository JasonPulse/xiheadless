namespace XiHeadless.Brains;

/// PUPPETMASTER life path (PldBrain's 3-phase shape — brain = config only):
///   1. Prereq sub: WAR to 30 as MAIN (PUP/WAR per the roster).
///   2. Unlock: "No Strings Attached" (QuestDefs.Unlock[Pup], Bastok Markets + Whitegate + Arrapago).
///      TODO(BLOCKED): Whitegate is a ToAU boat destination — no overland edge in the zone graph, so
///      QuestRunner cannot route there on foot; coded but blocked until ship routing exists. The quest
///      also has a ~1 game-day wait between the Ghatsad steps that the step model can't express.
///   3. Seesaw: level PUP main / WAR sub (JobLeveling switches whenever WAR < ceil(PUP/2)).
/// TODO(automaton): the engine has no Activate/pet handling — the puppet stays un-deployed; PUP melees
/// hand-to-hand (skill 1) like a lesser MNK until pet support exists.
/// Gear: network-gnomes PUP guide lv1-30 bracket (baghnakhs + light cloth), ids verified vs the server DB.
public sealed class PupBrain(
    IPerception p, INavigation nav, ICombat combat, IMagic magic, IZoning zoning, IGear gear, IAuctionHouse ah,
    IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, IQuests quests, ITradeNpc trade, IEvents events) : IBrain
{
    const byte H2HSkill = 1;
    const byte GreatAxeSkill = 6;             // WAR prereq/sub phases ride the proven Great Axe kit
    const ushort Animator = 17859;            // unlock reward — never sell
    const string AhZone = "Windurst Woods";   // home-nation AH (char is Windurst)

    // Ascending by level so later (better) pieces override earlier ones in the same slot.
    static readonly (ushort item, byte slot, byte lvl)[] Gear =
    {
        (17476, EquipSlot.Main, 1),    // Cat Baghnakhs +1
        (12526, EquipSlot.Head, 1),    // Copper Hairpin +1
        (14803, EquipSlot.Ear1, 10),   // Optical Earring
        (12530, EquipSlot.Head, 12),   // Sage's Circlet
        (12626, EquipSlot.Body, 12),   // Linen Robe +1
        (12901, EquipSlot.Legs, 12),   // Linen Slops +1
        (16701, EquipSlot.Main, 14),   // Strike Baghnakhs
        (12799, EquipSlot.Hands, 14),  // Battle Gloves
        (13522, EquipSlot.Ring1, 14),  // Courage Ring
        (13211, EquipSlot.Waist, 14),  // Friar's Rope
        (16702, EquipSlot.Main, 20),   // Cougar Baghnakhs
        (13061, EquipSlot.Neck, 21),   // Spike Necklace
        (13326, EquipSlot.Ear1, 21),   // Beetle Earring +1
        (16409, EquipSlot.Main, 24),   // Lynx Baghnakhs
        (15224, EquipSlot.Head, 24),   // Empress Hairpin
        (16368, EquipSlot.Legs, 25),   // Herder's Subligar
        (16445, EquipSlot.Main, 30),   // Claws +1
        (15058, EquipSlot.Hands, 30),  // Combat Mittens +1
    };

    // Full arc (sub WAR->30, unlock, seesaw PUP/WAR) via the shared JobLifecycle — brain = config only.
    // The chain starts in Bastok Markets (reachable overland — stealth-trek there first). TODO(BLOCKED):
    // it then needs Aht Urhgan Whitegate (no overland edge) + a ~1 game-day wait, so the unlock will fail
    // gracefully (hold + level WAR) until ship routing / a wait primitive land.
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, quests, trade, events,
            new JobLifecycle.Config
            {
                MainJob = Job.Pup, SubJob = Job.War, Advanced = true,
                UnlockSteps = QuestDefs.Unlock[Job.Pup],   // "No Strings Attached"
                StealthUnlock = true, UnlockTrekZone = "Bastok_Markets", UnlockTrekZoneId = 235,
                GrindCfgFor = GrindCfg, Tag = "pup",
            }, magic: magic).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job) => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear).ToArray(),
        Keep = GearRoutines.KeepSet(Gear, 1126, 1127, Animator),
        Equip = Equip,
        WepSkillForLevel = _ => job == Job.War ? GreatAxeSkill : H2HSkill,
        ConMin = 1, ConMax = 3,
        CleanPullNeighborCon = 3,
        RestHpTrigger = 70, RestHpTarget = 90,
        Tag = "pup",
    };

    async Task Equip(CancellationToken ct)
    {
        (byte slot, ushort item)? phase = p.World.MainJob == Job.War ? (EquipSlot.Main, WarBrain.Weapon20) : null;
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, Gear, ct, phase);
        Log.Info($"[pup] equipped {n}/{total} (job {p.World.MainJob} lvl {p.World.MainJobLevel}, h2h={gear.SkillLevel(H2HSkill)})");
    }
}
