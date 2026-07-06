namespace XiHeadless.Brains;

/// SUMMONER life path (PldBrain's 3-phase shape — brain = config only):
///   1. Prereq sub: WHM to 30 as MAIN.
///   2. Unlock: "I Can Hear a Rainbow" via QuestRunner (QuestDefs.Unlock[Job.Smn]).
///   3. Seesaw: level SMN main / WHM sub (JobLeveling switches whenever WHM < ceil(SMN/2)).
/// Unlock TODOs (live work needed):
///   * Needs Carbuncle's Ruby (1125) in the bag BEFORE the start talk — ~1% drop from leech mobs
///     (incl. Poison Leech in Buburimu); a farm pass must bank it first.
///   * The middle phase collects 7 elemental "lights" via onZoneIn auto-events that only fire under
///     MATCHING WEATHER — the ZoneInFrom rows in QuestDefs are placeholders; the real flow needs a
///     weather-watch roam loop, which QuestRunner cannot express yet.
public sealed class SmnBrain(
    IPerception p, INavigation nav, ICombat combat, IZoning zoning, IGear gear, IAuctionHouse ah,
    IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, IQuests quests, ITradeNpc trade, IEvents events) : IBrain
{
    const byte ClubSkill = 11;                // LSB skill enum (Club=11 — same id WhmBrain/BlmBrain train)
    const byte DaggerSkill = 2;               // Ceremonial Dagger bracket (1-9)
    const byte StaffSkill = 12;               // Elm Pole bracket (30+)
    const ushort CarbunclesRuby = 1125;       // unlock-quest key drop — never sell
    const string AhZone = "Windurst Woods";   // home-nation AH (char is Windurst)

    // Ascending by level so later pieces override earlier ones in the same slot.
    static readonly (ushort item, byte slot, byte lvl)[] Gear =
    {
        (16753, EquipSlot.Main, 1),    // Ceremonial Dagger
        (12529, EquipSlot.Head, 10),   // Brass Hairpin +1
        (18394, EquipSlot.Main, 10),   // Pilgrim's Wand
        (13548, EquipSlot.Ring1, 10),  // Astral Ring
        (14694, EquipSlot.Ear1, 10),   // Energy Earring +1
        (12530, EquipSlot.Head, 12),   // Sage's Circlet
        (17413, EquipSlot.Main, 13),   // Hermit's Wand
        (13211, EquipSlot.Waist, 14),  // Friar's Rope
        (12531, EquipSlot.Head, 20),   // Silver Hairpin +1
        (14062, EquipSlot.Hands, 20),  // Carbuncle Mitts
        (15405, EquipSlot.Legs, 20),   // Baron's Slops
        (13073, EquipSlot.Neck, 26),   // Holy Phial
        (14025, EquipSlot.Hands, 27),  // Devotee's Mitts +1
        (14427, EquipSlot.Body, 29),   // Seer's Tunic +1
        (15316, EquipSlot.Feet, 29),   // Seer's Pumps +1
        (17119, EquipSlot.Main, 30),   // Elm Pole +1 (staff)
    };

    // Full arc (sub WHM->30, unlock, seesaw SMN/WHM) via the shared JobLifecycle — brain = config only.
    // CAVEAT (unchanged): "I Can Hear a Rainbow" needs Carbuncle's Ruby (1125, farmed live) and a
    // weather-gated 7-light roam QuestRunner can't express, so the unlock will fail gracefully (hold + level WHM).
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, quests, trade, events,
            new JobLifecycle.Config
            {
                MainJob = Job.Smn, SubJob = Job.Whm, Advanced = true,
                UnlockSteps = QuestDefs.Unlock[Job.Smn],   // "I Can Hear a Rainbow"
                GrindCfgFor = GrindCfg, Tag = "smn",
            }).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job) => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear).ToArray(),
        Keep = GearRoutines.KeepSet(Gear, 1126, 1127, CarbunclesRuby),
        Equip = Equip,
        // WS follows the equipped main bracket: dagger 1-9, club 10-29 (both phases), staff at 30+.
        WepSkillForLevel = lvl => lvl < 10 ? DaggerSkill : lvl >= 30 ? StaffSkill : ClubSkill,
        ConMin = 1, ConMax = 3,
        CleanPullNeighborCon = 3,
        SkipMobNames = new[] { "Saplin", "Mandragora" },
        RestHpTrigger = 70, RestHpTarget = 90,
        RestMpPct = 40,                        // both phases are mages
        Tag = "smn",
    };

    async Task Equip(CancellationToken ct)
    {
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, Gear, ct);
        Console.WriteLine($"[smn] equipped {n}/{total} (job {p.World.MainJob} lvl {p.World.MainJobLevel}, club={gear.SkillLevel(ClubSkill)})");
    }
}
