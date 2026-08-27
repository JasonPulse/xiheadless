namespace XiHeadless.Brains;

/// BLM leveling brain — the WHM character's SUBJOB grind (BLM 18 gives WHM/BLM a full sub at 36+; leveled
/// open-ended past that so the char keeps gaining while online).
/// A basic job (no unlock), leveled as BLM/WHM through the shared JobLifecycle:
///   * SubJob = WHM so the mage can self-Cure in a fight (BLM can't cast Cure alone) AND so a post-death
///     recovery travels back AS the WHM (aggro-free above the mobs) — both hard-won BlmBrain patterns, now
///     provided generically by JobLifecycle (self-heal via the brain's EmergencyHeal; safe-job recovery via
///     SafeTravelJobFor picking the leveled WHM). The old Mhaura home-point flag / stealth-trek band-aids are
///     gone: in the fresh-start model the level-gated Windurst nursery + safe recovery handle the return.
///   * Stone is the ranged pull + in-fight nuke (BLM's real damage — without it the bot meleed and died).
/// Levels BLM open-ended (MainTarget = 0) so it keeps grinding while online instead of self-completing and
/// login-looping at 18. When the char's WHM is already high (its real main), the seesaw never levels WHM —
/// it just carries BLM up.
public sealed class BlmBrain(
    IPerception p, INavigation nav, ICombat combat, IMagic magic, IZoning zoning, IGear gear,
    IAuctionHouse ah, IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, ILifecycle lifecycle,
    IEvents events, IChat chat, IParty party) : IBrain
{
    const byte ClubSkill = 11;
    const string AhZone = "Windurst Woods";   // home-nation AH (Windurst-default fleet chars)

    // Mage gear ladder — INT/MP cloth + wands (Club skill, matching WepSkillForLevel). Every (item, slot, lvl)
    // is reused verbatim from the already-verified WHM/SMN/RDM tables, and each mage-specific piece was checked
    // BLM-wearable against the server item_equipment.sql job-mask (BLM = bit 3). Ascending by level so the best
    // owned piece wins per slot. BLM had NO gear table at all before — Droben grinded to 21 stark naked
    // (0 equipped, user-observed via the char viewer 2026-08-27).
    static readonly (ushort item, byte slot, byte lvl)[] Gear =
    {
        (17024, EquipSlot.Main, 1),    // Ash Club (club, lv1)
        (12526, EquipSlot.Head, 1),    // Copper Hairpin +1
        (13093, EquipSlot.Neck, 7),    // Justice Badge
        (18394, EquipSlot.Main, 10),   // Pilgrim's Wand (club-type, INT)
        (12529, EquipSlot.Head, 10),   // Brass Hairpin +1
        (13379, EquipSlot.Ear1, 10),   // Energy Earring
        (13548, EquipSlot.Ring1, 10),  // Astral Ring
        (17413, EquipSlot.Main, 13),   // Hermit's Wand (club-type)
        (13211, EquipSlot.Waist, 14),  // Friar's Rope
        (12531, EquipSlot.Head, 20),   // Silver Hairpin +1
        (14447, EquipSlot.Body, 20),   // Baron's Saio
        (14054, EquipSlot.Hands, 20),  // Baron's Cuffs
        (15405, EquipSlot.Legs, 20),   // Baron's Slops
        (13073, EquipSlot.Neck, 26),   // Holy Phial
        (14025, EquipSlot.Hands, 27),  // Devotee's Mitts +1
        (14427, EquipSlot.Body, 29),   // Seer's Tunic +1
        (15316, EquipSlot.Feet, 29),   // Seer's Pumps +1
    };

    // Full arc via the shared JobLifecycle: basic BLM leveled as BLM/WHM (WHM enables self-Cure + safe-job
    // recovery). The level-gated nursery (a lv9 BLM dies net-zero in Tahrongi — the plan keeps it in West/East
    // Sarutabaruta until ~15) + baby phase come for free. MainTarget = 0 (OPEN-ENDED): BLM 18 gave a full
    // WHM/BLM sub, but capping there just idled/login-looped once reached — so keep leveling BLM while online
    // (the user steers when to stop); the seesaw only carries WHM along if WHM isn't already the higher main.
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, null, null, events,
            new JobLifecycle.Config
            {
                MainJob = Job.Blm, SubJob = Job.Whm, Advanced = false, MainTarget = 0,
                GrindCfgFor = _ => Cfg(), Tag = "blm",
            }, lifecycle, chat: chat, magic: magic, party: party).RunAsync(ct);

    LevelGrind.Config Cfg() => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear).ToArray(),   // cheap-first: the array is ascending by level
        GearTable = Gear,
        // Stealth consumables + seals are never junk (the bag clear SOLD the oils once and the crossings killed us).
        Keep = GearRoutines.KeepSet(Gear, 1126, 1127, StealthRoutines.SilentOil, StealthRoutines.PrismPowder),
        Equip = Equip,
        WepSkillForLevel = _ => ClubSkill,
        ConMin = 1, ConMax = 3,                            // squishier than a melee — cap at DecentChallenge
        RoamHop = 60f,   // the default 150y overshot the safe bee ground into 3-threat clusters and a lv9 BLM
                         // got ganged there (died to a con-2 crow amid 3 threats). Shorter hops stay local.
        RestHpTrigger = 60, RestHpTarget = 90, RestMpPct = 40,
        Pull = Pull,                                       // Stone from range: DoT-free nuke opener
        UseAbilities = Nuke,                               // keep nuking through the fight (BLM's real damage)
        EmergencyHeal = EmergencyHeal,                     // self-Cure via the WHM sub cuts nursery deaths
        Tag = "blm",
    };

    async Task Equip(CancellationToken ct)
    {
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, Gear, ct);
        Log.Info($"[blm] equipped {n}/{total} (lvl {p.World.MainJobLevel}, club={gear.SkillLevel(ClubSkill)})");
    }

    // Self-Cure via the WHM sub (BLM/WHM). Nursery deaths are frequent and each recovery is a long round-trip,
    // so healing through a fight is a big net win — the shared selector-driven EmergencyCure.
    Task<bool> EmergencyHeal(CancellationToken ct) => MagicRoutines.EmergencyCure(magic, p, ct, tag: "blm");

    // Pull with the cheapest ready Stone tier (shared selector pull — no-op on the WHM phase, Ready gates it).
    Task Pull(uint mobId, CancellationToken ct) => MagicRoutines.SpellPull(magic, p, SpellLine.Stone, mobId, ct, tag: "blm");

    // In-fight nuking — BLM's actual damage. Without it the bot MELEED every fight (hp 100->0 while the mob
    // sat at 79% and MP never left 100%). Called every kill-loop tick; keep an MP floor so the last nukes can
    // still finish a low mob. CastLowest = cheapest ready Stone tier (MP economy while grinding).
    async Task Nuke(uint mob, int con, CancellationToken ct)
    {
        if (p.World.Mpp < 10 || !magic.CastLowest(SpellLine.Stone, mob)) return;
        await Task.Delay(4000, ct);   // cast time + a swing between nukes; the recast gate is server-side
    }
}
