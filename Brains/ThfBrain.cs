namespace XiHeadless.Brains;

/// THF leveling brain (bot1 account) — the fleet's DPS. Fresh accounts create as WAR (lobby default), so the
/// life-goal starts with a job change to THF, then runs the shared solo LevelGrind on the Windurst path.
/// Config only: gear bracket (daggers + the job-shared leather set), dagger WS, THF ability rotation.
/// Subjob (MNK per user) comes later — it needs its own subjob-quest item run at 18+.
public sealed class ThfBrain(
    IPerception p, INavigation nav, ICombat combat, IMagic magic, IZoning zoning, IGear gear,
    IAuctionHouse ah, IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, IChat chat, ILifecycle lifecycle, IParty party) : IBrain
{
    const byte DaggerSkill = 2;
    const byte SwordSkill = 3;
    const string AhZone = "Windurst Woods";
    // Gear per the network-gnomes THF guide (ids verified against server item_equipment.sql).
    // SELF-FUNDED by user directive — this character is the fleet's funding test: the AH buy pass
    // simply skips what it can't afford yet and retries each session as junk-sale gil accumulates.
    const ushort BronzeKnife = 16465;   // lv1
    const ushort BlindDagger = 16454;   // lv7  (guide: Beestinger/Blind Dagger bracket)
    const ushort BrassDagger = 16449;   // lv9
    const ushort Dagger      = 16450;   // lv12
    const ushort Baselard    = 16455;   // lv18
    const ushort FeatherCollar = 13075; // lv7 neck
    const ushort BoneEarring   = 13321; // lv16 ear
    const ushort EmpressHairpin = 15224; // lv24 head (the guide's big DEX/AGI piece)
    const ushort OnionSword  = 16534;   // lv1 all-jobs — the CREATION weapon (char is created as WAR, then
                                        // job-changed): it's the only weapon a broke fresh THF owns. Fighting
                                        // proceeded BAREHANDED (equipped 0/11, 15 gil) until this fallback.

    // Full arc via the shared JobLifecycle: THF is a basic job (no unlock) — level it from 1 with a MNK sub
    // kept at half via the seesaw (MNK = hand-to-hand, so the sub needs no extra weapon; MNK is THF's
    // intended sub per the user). The level-gated nursery (West/East Sarutabaruta -> Tahrongi -> nation
    // path), which replaces the old hand-rolled "nursery until 14", plus baby phase + safe recovery come free.
    public Task RunAsync(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, null, null, null,
            new JobLifecycle.Config
            {
                MainJob = Job.Thf, SubJob = Job.War, Advanced = false,   // user roster: THF/WAR
                GrindCfgFor = GrindCfg, Tag = "thf",
            }, lifecycle: lifecycle, chat: chat, magic: magic, party: party).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job)
    {
        // THF pops its kit (Sneak Attack/Steal); the MNK sub phase just melees hand-to-hand.
        Func<uint, int, CancellationToken, Task> abils = job == Job.Thf ? UseAbilities : (_, _, _) => Task.CompletedTask;
        return new LevelGrind.Config
        {
            HomeNation = Nation.Windurst,
            AhZone = AhZone,
            // Cheap-first so early gil buys the early bracket instead of stalling on a lv18 dagger bid.
            BuyItems = new ushort[] { BronzeKnife, BlindDagger, FeatherCollar, BrassDagger, Dagger, BoneEarring, Baselard, EmpressHairpin }
                .Concat(WarBrain.Armor.Select(g => g.item))
                .Concat(WarBrain.Armor21.Select(g => g.item)).ToArray(),
            Keep = new HashSet<ushort>(new ushort[] { BronzeKnife, BlindDagger, BrassDagger, Dagger, Baselard,
                    FeatherCollar, BoneEarring, EmpressHairpin, OnionSword, 1126, 1127 }
                .Concat(WarBrain.Armor.Select(g => g.item))
                .Concat(WarBrain.Armor21.Select(g => g.item))),
            Equip = Equip,
            // WS follows the weapon we actually WEAR: MNK sub = hand-to-hand (1); THF = sword on the creation
            // Onion Sword, dagger once bought.
            WepSkillForLevel = _ => job == Job.Mnk ? (byte)1 : BestOwnedWeapon() == OnionSword ? SwordSkill : DaggerSkill,
            ConMin = 1, ConMax = 3,
            CleanPullNeighborCon = 3,   // solo + fragile: never pull next to a same-band neighbor (nest chains kill us)
            UseAbilities = abils,
            // 70/90: chaining fights down to ~39% is what every death has in common — a higher floor makes
            // forced fights survivable.
            RestHpTrigger = 70, RestHpTarget = 90,
            Tag = "thf",
        };
    }

    // Best weapon we OWN and can wear (the guide's dagger ladder; creation Onion Sword as the broke fallback).
    ushort BestOwnedWeapon() =>
        p.World.MainJobLevel >= 18 && inv.Has(Baselard) ? Baselard
        : p.World.MainJobLevel >= 12 && inv.Has(Dagger) ? Dagger
        : p.World.MainJobLevel >= 9 && inv.Has(BrassDagger) ? BrassDagger
        : p.World.MainJobLevel >= 7 && inv.Has(BlindDagger) ? BlindDagger
        : inv.Has(BronzeKnife) ? BronzeKnife
        : OnionSword;

    async Task Equip(CancellationToken ct)
    {
        ushort weapon = BestOwnedWeapon();
        var set = new List<(byte slot, uint item)> { (EquipSlot.Main, weapon) };
        set.AddRange(WarBrain.Armor.Select(g => (g.slot, (uint)g.item)));   // leather set is job-shared
        set.Add((EquipSlot.Neck, FeatherCollar));                            // guide accessories (server skips unwearable/unowned)
        set.Add((EquipSlot.Ear1, BoneEarring));
        set.AddRange(WarBrain.Armor21.Select(g => (g.slot, (uint)g.item))); // Beetle + Spike at 21 replace leather
        set.Add((EquipSlot.Head, EmpressHairpin));                           // after Beetle: replaces the mask at 24
        int n = await gear.EquipSet(set, ct);
        Log.Info($"[thf] equipped {n}/{set.Count} (lvl {p.World.MainJobLevel}, wep={weapon} dagger={gear.SkillLevel(DaggerSkill)} sword={gear.SkillLevel(SwordSkill)})");
    }

    // Sneak Attack (lv15, 60s): free burst when it lands (full effect needs to be behind the mob — the chase
    // loop often is). UseAbility gates job/level/recast, so calling every tick is safe.
    async Task UseAbilities(uint mob, int con, CancellationToken ct)
    {
        if (con >= 2 && await combat.UseAbility(Ability.SneakAttack, mob, ct)) Log.Info("[thf] Sneak Attack");
        if (await combat.UseAbility(Ability.Steal, mob, ct)) Log.Info("[thf] Steal");   // lv5: free loot, tiny hate
    }
}
