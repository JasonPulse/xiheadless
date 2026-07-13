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
    IEvents events) : IBrain
{
    // Stealth consumables + seals are never junk (the bag clear SOLD the oils once and the crossings killed us).
    static readonly HashSet<ushort> BlmKeep = new() { 1126, 1127, StealthRoutines.SilentOil, StealthRoutines.PrismPowder };

    const byte ClubSkill = 11;

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
            }, lifecycle).RunAsync(ct);

    LevelGrind.Config Cfg() => new()
    {
        HomeNation = Nation.Windurst,
        Keep = BlmKeep,
        WepSkillForLevel = _ => ClubSkill,
        ConMin = 1, ConMax = 3,                            // squishier than a melee — cap at DecentChallenge
        RoamHop = 60f,   // the default 150y overshot the safe bee ground into 3-threat clusters and a lv9 BLM
                         // got ganged there (died to a con-2 crow amid 3 threats). Shorter hops stay local.
        SkipMobNames = new[] { "Saplin", "Mandragora" },   // sleep-lock = certain death for a squishy mage
        RestHpTrigger = 60, RestHpTarget = 90, RestMpPct = 40,
        Pull = Pull,                                       // Stone from range: DoT-free nuke opener
        UseAbilities = Nuke,                               // keep nuking through the fight (BLM's real damage)
        EmergencyHeal = EmergencyHeal,                     // self-Cure via the WHM sub cuts nursery deaths
        Tag = "blm",
    };

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
