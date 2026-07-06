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
            }).RunAsync(ct);
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
        SkipMobNames = new[] { "Saplin", "Mandragora" },       // sleep-lock = death for a squishy mage
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
        Console.WriteLine($"[whm] restock: buying {id} (gil {p.World.Gil})");
        await ShopRoutines.BuyItem(ah, p, inv, id, Keep, ShopRoutines.NoFree, ct);
    }

    async Task Equip(CancellationToken ct)
    {
        // Best owned weapon for our level + armor pieces low->high per slot in one EquipSet: the server
        // ignores over-level pieces and later equips replace earlier ones, so each slot ends at the best
        // wearable item this character owns.
        byte lvl = p.World.MainJobLevel;
        ushort club = (inv.Has(HolyMaul) && lvl >= 38) ? HolyMaul
                    : (inv.Has(MelampusStaff) && lvl >= 29) ? MelampusStaff
                    : (inv.Has(PixieMace) && lvl >= 24) ? PixieMace
                    : (inv.Has(ChestnutClub) && lvl >= 16) ? ChestnutClub : AshClub;
        var set = new List<(byte, uint)>();
        if (inv.Has(club)) set.Add(((byte)EquipSlot.Main, club));
        if (inv.Has(BrassHairpin)) set.Add(((byte)EquipSlot.Head, BrassHairpin));
        if (inv.Has(BattleGloves)) set.Add(((byte)EquipSlot.Hands, BattleGloves));
        if (inv.Has(SilverHairpin)) set.Add(((byte)EquipSlot.Head, SilverHairpin));
        if (inv.Has(BaronsSaio)) set.Add(((byte)EquipSlot.Body, BaronsSaio));
        if (inv.Has(BaronsSlops)) set.Add(((byte)EquipSlot.Legs, BaronsSlops));
        if (inv.Has(HolyPhial)) set.Add(((byte)EquipSlot.Neck, HolyPhial));
        if (inv.Has(DevoteesMitts)) set.Add(((byte)EquipSlot.Hands, DevoteesMitts));
        if (inv.Has(SeersTunic)) set.Add(((byte)EquipSlot.Body, SeersTunic));
        if (inv.Has(SeersPumps)) set.Add(((byte)EquipSlot.Feet, SeersPumps));
        if (inv.Has(PeacockCharm)) set.Add(((byte)EquipSlot.Neck, PeacockCharm));
        if (set.Count > 0)
        {
            int n = await gear.EquipSet(set, ct);
            Console.WriteLine($"[whm] equipped {n}/{set.Count} (lvl {p.World.MainJobLevel}, club skill {gear.SkillLevel(ClubSkill)})");
        }
        await LearnIfReady(ScrollDia, Spell.Dia, 3, ct);
        await LearnIfReady(ScrollCure, Spell.Cure, 5, ct);
    }

    async Task LearnIfReady(ushort scrollId, Spell spell, int reqLevel, CancellationToken ct)
    {
        if (p.World.MainJobLevel < reqLevel) return;
        await MagicRoutines.LearnFromScroll(inv, magic, p, scrollId, spell, ct, "whm");
    }

    // Pull with Dia from range (also a DoT). Only if learned + affordable + the mob is within casting range.
    async Task Pull(uint mobId, CancellationToken ct)
    {
        if (!magic.Ready(Spell.Dia)) return;
        if (!p.World.Entities.TryGetValue(mobId, out var e) || p.DistanceTo(e.X, e.Z) > 20f) return;
        Console.WriteLine($"[whm] Dia pull on 0x{mobId:X}");
        magic.Cast(Spell.Dia, mobId);
        await Task.Delay(3000, ct);
    }

    // After a kill (mob dead -> no cast interruption), Cure self up toward full FAST instead of slow /heal
    // regen that adds keep interrupting. Stop if MP gets low (keep enough for the next fight's emergency cure).
    async Task PostKillHeal(CancellationToken ct)
    {
        for (int i = 0; i < 4 && p.World.Hpp < 85 && p.World.Mpp > 20 && magic.Ready(Spell.Cure) && !ct.IsCancellationRequested; i++)
        {
            Console.WriteLine($"[whm] post-kill Cure (HP {p.World.Hpp}% MP {p.World.Mpp}%)");
            magic.Cast(Spell.Cure, p.World.MyId);
            await Task.Delay(2500, ct);
        }
    }

    // Self-Cure when getting low. Trigger at 60% (not 40%): a cast takes ~2.5s and ANY hit during it can
    // interrupt the spell, so curing only at critical HP means it's usually interrupted and we die anyway.
    // Curing with more buffer lands more heals and keeps us off the floor. Target is self (MyId).
    async Task<bool> EmergencyHeal(CancellationToken ct)
    {
        if (p.World.Hpp >= 60 || !magic.Ready(Spell.Cure)) return false;
        Console.WriteLine($"[whm] Cure self (HP {p.World.Hpp}% MP {p.World.Mpp}%)");
        magic.Cast(Spell.Cure, p.World.MyId);
        await Task.Delay(2500, ct);
        return true;
    }
}
