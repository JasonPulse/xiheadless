namespace XiHeadless.Brains;

/// WAR leveling brain. The grind loop lives in the reusable LevelGrind routine; this class only supplies
/// WAR-specific job logic: the gear set (Great Axe + Leather), the weapon-skill the equipped weapon trains,
/// the job abilities to pop, and the con band. Gear guide (network-gnomes WAR guide, lv1-18 bracket).
public sealed class WarBrain(IPerception p, INavigation nav, ICombat combat, IMagic magic, IZoning zoning, IGear gear, IAuctionHouse ah, IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs) : IBrain
{
    const Nation HomeNation = Nation.Windurst; // selects the HuntZones leveling path (the bot is a Windurst char)
    const string AhZone = "Windurst Woods";  // where we buy gear (has the AH)
    const byte WepSkill = 6;                  // Great Axe — the WS auto-pick reads this skill
    public const ushort Weapon = 16704;              // Butterfly Axe (Great Axe, lv5)
    public const ushort EarlyWeapon = 16534;         // Onion Sword (lv1) — used until we can wield the axe at lv5
    public const ushort Weapon20 = 16714;            // Neckchopper (Great Axe, lv20 on this server) — the 21/24-bracket weapon

    // Non-weapon gear (item id, slot). The main-hand weapon is chosen by level in Equip() so we never send
    // two main-hand equips in one pass. EquipSet applies each piece; the server ignores any above our level.
    public static readonly (ushort item, byte slot)[] Armor =
    {
        (12440, EquipSlot.Head),   // Leather Bandana (lv7)
        (12568, EquipSlot.Body),   // Leather Vest    (lv7)
        (12696, EquipSlot.Hands),  // Leather Gloves  (lv7)
        (12824, EquipSlot.Legs),   // Leather Trousers(lv7)
        (13081, EquipSlot.Neck),   // Leather Gorget  (lv7)
        (13014, EquipSlot.Feet),   // Leaping Boots   (lv7)
        (17280, EquipSlot.Ranged), // Boomerang       (lv14)
        (13380, EquipSlot.Ear1),   // Hope Earring    (lv10)
        (13194, EquipSlot.Waist),  // Warrior's Belt  (lv15)
        (13522, EquipSlot.Ring1),  // Courage Ring    (lv14)
    };

    // The 21-bracket set (levels VERIFIED against this server's item_equipment.sql — Beetle is lv21 here).
    // Listed after Armor in the equip pass so these replace the lv7 pieces the moment they're wearable.
    public static readonly (ushort item, byte slot)[] Armor21 =
    {
        (12455, EquipSlot.Head),   // Beetle Mask     (lv21)
        (12583, EquipSlot.Body),   // Beetle Harness  (lv21)
        (12711, EquipSlot.Hands),  // Beetle Mittens  (lv21)
        (12835, EquipSlot.Legs),   // Beetle Subligar (lv21)
        (12967, EquipSlot.Feet),   // Beetle Leggings (lv21)
        (13061, EquipSlot.Neck),   // Spike Necklace  (lv21)
    };

    // Full arc via the shared JobLifecycle: WAR is a basic job (no unlock) — level it from 1 with a MNK sub
    // kept at half via the seesaw (MNK = hand-to-hand, so the sub needs no extra weapon). The level-gated
    // nursery (West/East Sarutabaruta -> Tahrongi -> nation path) + baby phase + safe recovery come for free.
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, null, null, null,
            new JobLifecycle.Config
            {
                MainJob = Job.War, SubJob = Job.Mnk, Advanced = false,
                GrindCfgFor = GrindCfg, Tag = "war",
            }, magic: magic).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job)
    {
        // WAR phase pops its kit; the MNK sub phase just melees (hand-to-hand, no abilities).
        Func<uint, int, CancellationToken, Task> abils = job == Job.War ? UseAbilities : (_, _, _) => Task.CompletedTask;
        return new LevelGrind.Config
        {
            HomeNation = HomeNation,
            AhZone = AhZone,
            BuyItems = new ushort[] { EarlyWeapon, Weapon }.Concat(Armor.Select(g => g.item)).ToArray(),
            Keep = new HashSet<ushort>(new ushort[] { EarlyWeapon, Weapon }.Concat(Armor.Select(g => (ushort)g.item))),
            Equip = Equip,
            // WS off the ACTUALLY-equipped weapon's skill: MNK sub = hand-to-hand (1); WAR = Onion Sword
            // (sword, 3) until lv5, then Butterfly Axe (Great Axe, 6) once it's on.
            WepSkillForLevel = lvl => job == Job.Mnk ? (byte)1 : lvl >= 5 ? WepSkill : (byte)3,
            ConMin = 1, ConMax = 4,   // IncrediblyEasy..EvenMatch
            UseAbilities = abils,
            // Skip Mandragoras/Saplins: they Dream Flower / Sleepga-lock a low-DPS melee, which then bleeds out.
            SkipMobNames = new[] { "Saplin", "Mandragora" },
            RestHpTrigger = 50, RestHpTarget = 75, RestMpPct = 0,   // no cure on WAR/MNK — HP rest only
            Tag = "war",
        };
    }

    Task Equip(CancellationToken ct) => EquipWarSet(gear, p, ct, "war");

    /// The ONE WAR gear set (level-picked weapon + Armor + Armor21) — static + parameterized so SubjobBrain
    /// equips the SAME set (its old EquipWarSet was a near-verbatim fork of this method).
    public static async Task EquipWarSet(IGear gear, IPerception p, CancellationToken ct, string tag = "war")
    {
        // One main-hand weapon, chosen by level: Neckchopper at 20+, Butterfly Axe at 5+, else Onion Sword.
        ushort weapon = p.World.MainJobLevel >= 20 ? Weapon20 : p.World.MainJobLevel >= 5 ? Weapon : EarlyWeapon;
        var set = new List<(byte slot, uint item)> { (EquipSlot.Main, weapon) };
        set.AddRange(Armor.Select(g => (g.slot, (uint)g.item)));
        set.AddRange(Armor21.Select(g => (g.slot, (uint)g.item)));   // after Armor: replaces lv7 pieces once wearable
        int n = await gear.EquipSet(set, ct);
        Log.Info($"[{tag}] equipped {n}/{set.Count} (lvl {p.World.MainJobLevel}, wep={weapon} sword={gear.SkillLevel(3)} ga={gear.SkillLevel(6)})");
    }

    // WAR job abilities — reused by the leveling brain (via cfg.UseAbilities) AND the subjob farm's KillTarget,
    // so the WAR fights with its KIT instead of pure auto-attack (the "only ever auto-attacks" bug the user saw).
    // static + ICombat-parameterized so SubjobBrain calls the SAME rotation — no duplicate. combat.UseAbility
    // gates each on job/level/recast, so calling every tick is safe: off-cooldown ones return instantly (no-op),
    // a firing one costs its 600ms GCD. Used STRATEGICALLY by con: the long-recast buffs aren't burned on trash.
    public static async Task UseWarAbilities(ICombat combat, uint mob, int con, int hpp, bool provoke, CancellationToken ct)
    {
        // Provoke (lv5, 30s recast) is HATE management, NOT a damage button — fire it ONLY when asked: at the
        // START of a fight (grab initial hate) or when a mob has switched to the HEALER (yank it back). The
        // caller decides; spamming it every recast (the old bug) wasted GCDs and did nothing once we had hate.
        if (provoke && await combat.UseAbility(Ability.Provoke, mob, ct)) Log.Info("[war] Provoke");  // lv5
        // Offensive buffs (Berserk +att, Aggressor +acc, Warcry party att): only on a real fight —
        // DecentChallenge+ (con >= 3). Trivial mobs don't need them and the 5-min recast is better saved.
        if (con >= 3)
        {
            if (await combat.UseAbility(Ability.Berserk, mob, ct)) Log.Info("[war] Berserk");      // lv15
            if (await combat.UseAbility(Ability.Aggressor, mob, ct)) Log.Info("[war] Aggressor");  // lv45
            if (await combat.UseAbility(Ability.Warcry, mob, ct)) Log.Info("[war] Warcry");        // lv35 (best in a party)
        }
        // Mighty Strikes (2hr, ~1h recast): TRUE emergency only — low HP AND a genuine threat (con >= 3), so
        // it's never wasted on trash or thrown away in a hopeless fight against something we shouldn't fight.
        if (con >= 3 && hpp is > 0 and < 25 && await combat.UseAbility(Ability.MightyStrikes, mob, ct))
            Log.Info("[war] Mighty Strikes (2hr emergency)");
    }

    // LevelGrind callback — delegates to the shared rotation with our live HP. Solo: no healer to protect, so
    // no Provoke (auto-attack damage holds hate fine); the offensive buffs + WS are what matter.
    Task UseAbilities(uint mob, int con, CancellationToken ct) => UseWarAbilities(combat, mob, con, p.World.Hpp, provoke: false, ct);
}
