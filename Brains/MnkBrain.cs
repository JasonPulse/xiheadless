namespace XiHeadless.Brains;

/// MNK leveling brain — the WAR character's SUBJOB grind (goal: MNK 20 so WAR/MNK has a full sub).
/// Life-goal shape: change main job to MNK if needed, then run the shared solo LevelGrind on the nation
/// path until the target level. H2H needs no weapon purchases early (fists work); armor rides whatever
/// the character owns (the leather/beetle sets are job-shared).
public sealed class MnkBrain(
    IPerception p, INavigation nav, ICombat combat, IMagic magic, IZoning zoning, IGear gear,
    IAuctionHouse ah, IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, ILifecycle lifecycle, IChat chat, IParty party) : IBrain
{
    const byte TargetLevel = 20;   // MNK 20 = full WAR/MNK sub once WAR main reaches 40 (user goal)
    const byte H2HSkill = 1;

    // Full arc via the shared JobLifecycle: a basic MNK-to-20 subjob grind (no unlock, no seesaw partner —
    // this is a dedicated sub-leveling helper). JobLifecycle changes to MNK, runs the level-gated nursery
    // grind (baby phase + safe recovery folded in), stops at 20 and logs out.
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, null, null, null,
            new JobLifecycle.Config
            {
                MainJob = Job.Mnk, SubJob = 0, MainTarget = TargetLevel,   // SubJob 0 = single-job (no seesaw)
                GrindCfgFor = _ => new LevelGrind.Config
                {
                    HomeNation = Nation.Windurst,
                    Keep = new HashSet<ushort> { 1126, 1127 },        // seals are never junk
                    WepSkillForLevel = _ => H2HSkill,
                    ConMin = 1, ConMax = 4,
                    RestHpTrigger = 50, RestHpTarget = 80,
                    Tag = "mnk",
                },
                Tag = "mnk",
            }, lifecycle, chat: chat, magic: magic, party: party).RunAsync(ct);
}
