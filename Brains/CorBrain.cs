namespace XiHeadless.Brains;

/// CORSAIR life path (PldBrain's 3-phase shape — brain = config only):
///   1. Prereq sub: RDM to 30 as MAIN (COR/RDM per the roster).
///   2. Unlock: "Luck of the Draw" (QuestDefs.Unlock[Cor], Whitegate + Arrapago Reef + Talacca Cove).
///      TODO(BLOCKED): Whitegate is a ToAU boat destination — no overland edge in the zone graph, so
///      QuestRunner cannot route there on foot; coded but blocked until ship routing exists.
///   3. Seesaw: level COR main / RDM sub (JobLeveling switches whenever RDM < ceil(COR/2)).
/// Weapons — TODO(ranged): COR's real skill is Marksmanship but the engine has NO ranged-attack loop, so
/// this brain melees. The server's COR sword list dies at lv7 (Xiphos), while its DAGGER ladder runs the
/// whole bracket — so the melee fallback is DAGGER (skill 2), not sword. RDM phases ride a sword.
/// Gear: network-gnomes COR guide lv1-30 bracket, ids verified vs the server DB (guns omitted — unusable).
public sealed class CorBrain(
    IPerception p, INavigation nav, ICombat combat, IZoning zoning, IGear gear, IAuctionHouse ah,
    IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, IQuests quests, ITradeNpc trade, IEvents events) : IBrain
{
    const byte DaggerSkill = 2;
    const byte SwordSkill = 3;                // RDM prereq/sub phases
    const ushort WaxSword = 16610;            // RDM-phase swords (RdmBrain's proven picks)
    const ushort BeeSpatha = 16611;
    const ushort FlameSword = 16621;
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
        (13613, EquipSlot.Back, 12),   // Traveler's Mantle
        (12778, EquipSlot.Hands, 12),  // Linen Cuffs +1
        (16614, EquipSlot.Main, 13),   // Knife +1
        (13522, EquipSlot.Ring1, 14),  // Courage Ring
        (16089, EquipSlot.Head, 15),   // Njord's Mask
        (14551, EquipSlot.Body, 15),   // Njord's Jerkin
        (15630, EquipSlot.Legs, 15),   // Njord's Trousers
        (16746, EquipSlot.Main, 20),   // Mercenary's Knife
        (13061, EquipSlot.Neck, 21),   // Spike Necklace
        (13326, EquipSlot.Ear1, 21),   // Beetle Earring +1
        (15224, EquipSlot.Head, 24),   // Empress Hairpin
        (13631, EquipSlot.Back, 24),   // Nomad's Mantle
        (14967, EquipSlot.Hands, 25),  // Freyr's Gloves
        (16755, EquipSlot.Main, 28),   // Archer's Knife
        (13020, EquipSlot.Feet, 29),   // Savage Gaiters
        (15281, EquipSlot.Waist, 30),  // Gun Belt
        (15172, EquipSlot.Head, 30),   // Noct Beret +1
        (14434, EquipSlot.Body, 30),   // Noct Doublet +1
        (14333, EquipSlot.Legs, 30),   // Noct Brais +1
        (14207, EquipSlot.Feet, 30),   // Noct Gaiters +1
    };

    // Full arc (sub RDM->30, unlock, seesaw COR/RDM) via the shared JobLifecycle — brain = config only.
    // TODO(BLOCKED): "Luck of the Draw" is in Aht Urhgan Whitegate (ToAU boat destination, no overland edge)
    // — the stealth-trek attempt fails there, so the unlock will fail gracefully (hold + level RDM) until
    // ship routing exists.
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, quests, trade, events,
            new JobLifecycle.Config
            {
                MainJob = Job.Cor, SubJob = Job.Rdm, Advanced = true,
                UnlockSteps = QuestDefs.Unlock[Job.Cor],   // "Luck of the Draw"
                StealthUnlock = true, UnlockTrekZone = "Aht_Urhgan_Whitegate", UnlockTrekZoneId = 50,
                GrindCfgFor = GrindCfg, Tag = "cor",
            }).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job) => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear).Concat(new ushort[] { WaxSword, BeeSpatha, FlameSword }).ToArray(),
        Keep = GearRoutines.KeepSet(Gear, 1126, 1127, WaxSword, BeeSpatha, FlameSword),
        Equip = Equip,
        WepSkillForLevel = _ => job == Job.Rdm ? SwordSkill : DaggerSkill,
        ConMin = 1, ConMax = 3,
        CleanPullNeighborCon = 3,
        SkipMobNames = new[] { "Saplin", "Mandragora" },
        RestHpTrigger = 70, RestHpTarget = 90,
        RestMpPct = job == Job.Rdm ? 40 : 0,     // the RDM phase casts — rest MP back too
        Tag = "cor",
    };

    async Task Equip(CancellationToken ct)
    {
        // RDM phases can't hold most COR daggers — ride RdmBrain's proven sword picks instead.
        int lvl = p.World.MainJobLevel;
        (byte slot, ushort item)? phase = p.World.MainJob == Job.Rdm
            ? (EquipSlot.Main, lvl >= 18 ? FlameSword : lvl >= 11 ? BeeSpatha : WaxSword) : null;
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, Gear, ct, phase);
        Console.WriteLine($"[cor] equipped {n}/{total} (job {p.World.MainJob} lvl {lvl}, dagger={gear.SkillLevel(DaggerSkill)})");
    }
}
