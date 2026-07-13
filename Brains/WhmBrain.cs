namespace XiHeadless.Brains;

/// WHM solo leveling — SELF-FUNDING loop (the RMT grant API isn't on the server yet, and bots should earn
/// their own keep). It buys what it can afford upfront (with whatever gil it has — a one-time seed, or none),
/// then grinds, SELLS DROPS for gil on vendor trips, and keeps buying upgrades from that drop money as it can
/// afford + reach the level: a real CLUB (a wand does ~1 dmg), Dia (pull+DoT) & Cure scrolls, then better
/// gear. It grinds with Dia-pull, club melee, Cure-self when low. Per the network-gnomes WHM guide.
public sealed class WhmBrain(
    IPerception p, INavigation nav, ICombat combat, IMagic magic, IZoning zoning, IGear gear,
    IAuctionHouse ah, IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs,
    IQuests quests, ITradeNpc trade, IEvents events) : IBrain
{
    const Nation HomeNation = Nation.Windurst;
    const string AhZone = "Windurst Woods";
    const byte ClubSkill = 11;

    const ushort AshClub      = 17024; // lv1 club (4 dmg)
    const ushort ChestnutClub = 17025; // lv16 club (9 dmg) — melee upgrade
    const ushort ScrollDia    = 4631;  // teaches Dia (spell 23), usable lv3
    const ushort ScrollCure   = 4609;  // teaches Cure (spell 1), usable lv5
    const ushort BrassHairpin = 12529; // head, lv10 (+MP)
    const ushort BattleGloves = 12799; // hands, lv14

    // 18-38 bracket per the network-gnomes WHM guide — every id + level VERIFIED against this server's
    // item_equipment.sql. Bought self-funded via Restock as level + gil allow; equipped in the level pass.
    const ushort SilverHairpin = 12495; // head lv20 (+MP)
    const ushort BaronsSaio    = 14447; // body lv20 (INT/MND)
    const ushort BaronsSlops   = 15405; // legs lv20 (hMP)
    const ushort GarrisonHose  = 14314; // legs lv21 (TP alt; slops preferred for heal role)
    const ushort PixieMace     = 17414; // club lv24 (acc+5)
    const ushort HolyPhial     = 13073; // neck lv26
    const ushort DevoteesMitts = 14024; // hands lv27 (cure enhancement)
    const ushort SeersTunic    = 14424; // body lv29
    const ushort SeersPumps    = 15313; // feet lv29 (MP/MND)
    const ushort MelampusStaff = 18605; // staff lv29 (cure potency)
    const ushort PeacockCharm  = 13056; // neck lv33 (acc)
    const ushort HolyMaul      = 17080; // club lv38 (MND+3)
    static readonly HashSet<ushort> Keep = new()
        { AshClub, ChestnutClub, ScrollDia, ScrollCure, BrassHairpin, BattleGloves,
          SilverHairpin, BaronsSaio, BaronsSlops, GarrisonHose, PixieMace, HolyPhial,
          DevoteesMitts, SeersTunic, SeersPumps, MelampusStaff, PeacockCharm, HolyMaul, 1126, 1127,
          // Rare/EX quest items are still droppable — never sell/drop them on solo vendor trips either.
          QuestDefs.WildRabbitTail, QuestDefs.CupOfDhalmelSaliva, QuestDefs.BloodyRobe };

    public async Task RunAsync(CancellationToken ct)
    {
        // LIFE GOAL at 18+ (user architecture: the subjob quest is a PHASE of every job brain, not a brain):
        // accept the quest if new, or run the trade-completion the moment this character holds its item set.
        // Idempotent per session; the grind below proceeds regardless.
        await Task.Delay(4000, ct);
        // LIFE GOAL at 18+ (the subjob-slot quest is a PHASE of this brain): accept if new, or run the
        // trade-completion once this character holds its item set. Idempotent; the grind proceeds regardless.
        var sjq = new SubjobQuest(p, nav, zoning, quests, trade, combat, gear, inv, ah, shop, events);
        if (p.World.MainJobLevel >= 18 && (!sjq.Accepted() || sjq.HasItems()))
            await sjq.Advance(ct);

        // Full arc via the shared JobLifecycle: WHM is a basic job, leveled SOLO + self-funding (no seesaw
        // partner — SubJob 0). BuildCfg keeps every WHM-specific hook (self-funding restock, Dia pull, Cure
        // self-heal/post-kill heal); JobLifecycle adds the level-gated nursery + baby phase + safe recovery.
        await new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, quests, trade, events,
            new JobLifecycle.Config
            {
                MainJob = Job.Whm, SubJob = 0, MainTarget = 0,
                GrindCfgFor = _ => BuildCfg(), Tag = "whm",
            }, magic: magic).RunAsync(ct);
    }

    LevelGrind.Config BuildCfg() => new()
    {
        HomeNation = HomeNation,
        AhZone = AhZone,
        BuyItems = new[] { AshClub, ScrollDia, ScrollCure },   // buy essentials upfront with whatever gil we have
        Keep = Keep,
        Equip = Equip,
        WepSkillForLevel = _ => ClubSkill,
        ConMin = 2, ConMax = 4,                                // lvl-1 cons everything EvenMatch; cap at 4
        SellJunkWhenFull = true, SellAtItems = 18,             // SELL DROPS to fund upgrades (no grants)
        OnRestock = Restock,
        Pull = Pull,
        EmergencyHeal = EmergencyHeal,
        PostKillHeal = PostKillHeal,
        RestHpTrigger = 55, RestHpTarget = 85, RestMpPct = 25, // rest mostly for HP; post-kill Cure handles the rest
        Tag = "whm",
    };

    // Self-funding shopping, run after a sell trip (standing in the AH town with fresh drop money). Buy each
    // unowned, level-appropriate upgrade we can afford; BuyItem bids a ladder up to our gil so it won't
    // overspend, and the thresholds just skip obviously-broke attempts. Cheapest/most-impactful first.
    async Task Restock(CancellationToken ct)
    {
        if (!Game.Zonelines.HasAuctionHouse(zoning.CurrentZone)) return;   // sold at a non-AH vendor — buy next trip
        await Buy(AshClub, 1, 400, ct);
        await Buy(ScrollDia, 3, 1500, ct, Spell.Dia);
        await Buy(ScrollCure, 5, 1500, ct, Spell.Cure);
        await Buy(BrassHairpin, 10, 2500, ct);
        await Buy(BattleGloves, 14, 2000, ct);
        await Buy(ChestnutClub, 16, 2500, ct);
        // 18-38 guide bracket (see consts) — each purchase gated on level + a gil floor so early trips
        // don't blow the budget on gear it can't wear yet.
        await Buy(SilverHairpin, 20, 3000, ct);
        await Buy(BaronsSaio, 20, 3000, ct);
        await Buy(BaronsSlops, 20, 3000, ct);
        await Buy(PixieMace, 24, 3500, ct);
        await Buy(HolyPhial, 26, 3000, ct);
        await Buy(DevoteesMitts, 27, 3500, ct);
        await Buy(SeersTunic, 29, 4000, ct);
        await Buy(SeersPumps, 29, 3500, ct);
        await Buy(MelampusStaff, 29, 4000, ct);
        await Buy(PeacockCharm, 33, 5000, ct);
        await Buy(HolyMaul, 38, 5000, ct);
        await Equip(ct);
    }

    async Task Buy(ushort id, int minLevel, uint minGil, CancellationToken ct, Spell? learnsSpell = null)
    {
        if (p.World.MainJobLevel < minLevel || p.World.Gil < minGil) return;
        if (inv.Has(id) || (learnsSpell is { } sp && magic.Known(sp))) return;   // already own / already learned
        Log.Info($"[whm] restock: buying {id} (gil {p.World.Gil})");
        await ShopRoutines.BuyItem(ah, p, inv, id, Keep, ShopRoutines.NoFree, ct);
    }

    // (item, slot, min level) — ascending per slot, so the best wearable OWNED piece wins per slot
    // (EquipSet skips unowned; the server ignores over-level). The old hand-rolled inv.Has ladder is gone.
    static readonly (ushort item, byte slot, byte lvl)[] GearTable =
    {
        (AshClub, EquipSlot.Main, 1),
        (BrassHairpin, EquipSlot.Head, 10),
        (BattleGloves, EquipSlot.Hands, 14),
        (ChestnutClub, EquipSlot.Main, 16),
        (SilverHairpin, EquipSlot.Head, 20),
        (BaronsSaio, EquipSlot.Body, 20),
        (BaronsSlops, EquipSlot.Legs, 20),
        (PixieMace, EquipSlot.Main, 24),
        (HolyPhial, EquipSlot.Neck, 26),
        (DevoteesMitts, EquipSlot.Hands, 27),
        (SeersTunic, EquipSlot.Body, 29),
        (SeersPumps, EquipSlot.Feet, 29),
        (MelampusStaff, EquipSlot.Main, 29),
        (PeacockCharm, EquipSlot.Neck, 33),
        (HolyMaul, EquipSlot.Main, 38),
    };

    async Task Equip(CancellationToken ct)
    {
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, GearTable, ct);
        Log.Info($"[whm] equipped {n}/{total} (lvl {p.World.MainJobLevel}, club skill {gear.SkillLevel(ClubSkill)})");
        await LearnIfReady(ScrollDia, Spell.Dia, 3, ct);
        await LearnIfReady(ScrollCure, Spell.Cure, 5, ct);
    }

    async Task LearnIfReady(ushort scrollId, Spell spell, int reqLevel, CancellationToken ct)
    {
        if (p.World.MainJobLevel < reqLevel) return;
        await MagicRoutines.LearnFromScroll(inv, magic, p, scrollId, spell, ct, "whm");
    }

    // Pull with the Dia line from range (also a DoT) — the shared selector-driven spell pull.
    Task Pull(uint mobId, CancellationToken ct) => MagicRoutines.SpellPull(magic, p, SpellLine.Dia, mobId, ct, range: 20f, tag: "whm");

    // After a kill (mob dead -> no cast interruption), Cure self up toward full FAST instead of slow /heal
    // regen. The shared EmergencyCure picks the best affordable tier; stop if MP gets low.
    async Task PostKillHeal(CancellationToken ct)
    {
        for (int i = 0; i < 4 && !ct.IsCancellationRequested; i++)
            if (!await MagicRoutines.EmergencyCure(magic, p, ct, hppBelow: 85, minMpp: 20, tag: "whm")) break;
    }

    // Self-Cure when getting low. Trigger at 60% (not 40%): a cast takes ~2.5s and ANY hit during it can
    // interrupt the spell, so curing only at critical HP means it's usually interrupted and we die anyway.
    Task<bool> EmergencyHeal(CancellationToken ct) => MagicRoutines.EmergencyCure(magic, p, ct, hppBelow: 60, tag: "whm");
}
