namespace XiHeadless.Brains;

/// RUNE FENCER life path (PldBrain's 3-phase shape — brain = config only):
///   1. Prereq sub: WAR to 30 as MAIN (RUN/WAR per the roster).
///   2. Unlock: "Children of the Rune" (QuestDefs.Unlock[Run], researched from the server scripts:
///      Octavien in Eastern Adoulin ev23/ev26 + the Yahse Wildflower petal examine). No item trades —
///      pure walk-and-talk, but the final trial HALVES HP/MP. TODO(BLOCKED): the Adoulin zones have no
///      overland edge from the mainland (ship route) — coded but blocked until ship routing exists.
///   3. Seesaw: level RUN main / WAR sub (JobLeveling switches whenever WAR < ceil(RUN/2)).
/// Weapon: the unlock's own Sowilo Claymore (20781, RUN-only great sword, wearable from 1) carries the
/// whole bracket — no great-sword purchases needed. Great Sword skill 4; WAR phases keep the Great Axe.
/// Gear: network-gnomes "Rune Fencer" guide lv1-29 bracket (light DD armor), ids verified vs the server
/// DB (the guide's Kampf set is WAR/PLD-only there, so the Beetle+1 line is the 21-29 body instead).
public sealed class RunBrain(
    IPerception p, INavigation nav, ICombat combat, IZoning zoning, IGear gear, IAuctionHouse ah,
    IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, IQuests quests, ITradeNpc trade, IEvents events) : IBrain
{
    const byte GreatSwordSkill = 4;
    const byte GreatAxeSkill = 6;             // WAR prereq/sub phases ride the proven Great Axe kit
    const ushort SowiloClaymore = 20781;      // unlock reward, RUN-only GS — the bracket weapon, never sell
    const string AhZone = "Windurst Woods";   // home-nation AH (char is Windurst)

    // Ascending by level so later (better) pieces override earlier ones in the same slot.
    static readonly (ushort item, byte slot, byte lvl)[] Gear =
    {
        (20781, EquipSlot.Main, 1),    // Sowilo Claymore (quest reward — AH won't have it; equips once owned)
        (12526, EquipSlot.Head, 1),    // Copper Hairpin +1
        (12440, EquipSlot.Head, 7),    // Leather Bandana
        (12568, EquipSlot.Body, 7),    // Leather Vest
        (12696, EquipSlot.Hands, 7),   // Leather Gloves
        (12824, EquipSlot.Legs, 7),    // Leather Trousers
        (12952, EquipSlot.Feet, 7),    // Leather Highboots
        (12432, EquipSlot.Head, 10),   // Faceguard
        (12560, EquipSlot.Body, 10),   // Scale Mail
        (12944, EquipSlot.Feet, 10),   // Scale Greaves
        (12799, EquipSlot.Hands, 14),  // Battle Gloves
        (13522, EquipSlot.Ring1, 14),  // Courage Ring
        (13240, EquipSlot.Waist, 15),  // Warrior's Belt +1
        (13826, EquipSlot.Head, 16),   // Bone Mask +1
        (13716, EquipSlot.Body, 16),   // Bone Harness +1
        (12912, EquipSlot.Legs, 16),   // Bone Subligar +1
        (13225, EquipSlot.Waist, 18),  // Brave Belt
        (15486, EquipSlot.Back, 18),   // Breath Mantle
        (13061, EquipSlot.Neck, 21),   // Spike Necklace
        (13326, EquipSlot.Ear1, 21),   // Beetle Earring +1
        (13827, EquipSlot.Head, 21),   // Beetle Mask +1
        (13717, EquipSlot.Body, 21),   // Beetle Harness +1
        (14314, EquipSlot.Legs, 21),   // Garrison Hose
        (15224, EquipSlot.Head, 24),   // Empress Hairpin
        (13119, EquipSlot.Neck, 24),   // Tiger Stole
        (13631, EquipSlot.Back, 24),   // Nomad's Mantle
        (14260, EquipSlot.Legs, 25),   // Republic Subligar
        (13366, EquipSlot.Ear2, 29),   // Dodge Earring
    };

    // Full arc (sub WAR->30, unlock, seesaw RUN/WAR) via the shared JobLifecycle — brain = config only.
    // TODO(BLOCKED): "Children of the Rune" is in Eastern Adoulin (no overland edge — ship route) — the
    // stealth-trek attempt fails there, so the unlock will fail gracefully (hold + level WAR) until ship
    // routing exists. (The ev26 trial halves HP/MP, which the char survives.)
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, quests, trade, events,
            new JobLifecycle.Config
            {
                MainJob = Job.Run, SubJob = Job.War, Advanced = true,
                UnlockSteps = QuestDefs.Unlock[Job.Run],   // "Children of the Rune"
                StealthUnlock = true, UnlockTrekZone = "Eastern_Adoulin", UnlockTrekZoneId = 257,
                GrindCfgFor = GrindCfg, Tag = "run",
            }).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job) => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear).Where(i => i != SowiloClaymore).ToArray(),   // reward GS isn't on the AH
        Keep = GearRoutines.KeepSet(Gear, 1126, 1127),
        Equip = Equip,
        WepSkillForLevel = _ => job == Job.War ? GreatAxeSkill : GreatSwordSkill,
        ConMin = 1, ConMax = 3,
        CleanPullNeighborCon = 3,
        SkipMobNames = new[] { "Saplin", "Mandragora" },
        RestHpTrigger = 70, RestHpTarget = 90,
        Tag = "run",
    };

    async Task Equip(CancellationToken ct)
    {
        (byte slot, ushort item)? phase = p.World.MainJob == Job.War ? (EquipSlot.Main, WarBrain.Weapon20) : null;
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, Gear, ct, phase);
        Console.WriteLine($"[run] equipped {n}/{total} (job {p.World.MainJob} lvl {p.World.MainJobLevel}, gsword={gear.SkillLevel(GreatSwordSkill)})");
    }
}
