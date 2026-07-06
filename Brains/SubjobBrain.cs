namespace XiHeadless.Brains;

/// Subjob unlock, end to end: farm the three quest items in Buburimu as a WAR+WHM duo, then run the
/// "The Old Lady" quest chain (accept ev131 -> trade tail/cup/robe to Vera in Mhaura -> ev137 unlock).
///
/// THIN BRAIN: the whole farm/grind is the shared party LevelGrind (KillRoutine fights, RoamController
/// free-roam, Reunion splits, WaitAllGood tether) — this class only supplies WAR config + the item goals.
/// The droppers (tail=Mighty_Rarab, cup=Bull_Dhalmel, robe=Bogy) sit deeper in the zone and mostly con
/// too tough today; the loop LEVELS on con-2..4 mobs while free-roaming, and the con cache re-judges
/// everything on each level-up — so the droppers become engageable (PreferTarget grabs them the moment
/// their con enters range) and the roam reaches their clusters without any hardcoded camp/corridor.
public sealed class SubjobBrain(
    IPerception p, INavigation nav, IZoning zoning, IQuests quests, ITradeNpc trade, ICombat combat,
    IGear gear, IInventory inv, IAuctionHouse ah, IShop shop, IParty party, IChat chat,
    IDelivery delivery, ILifecycle lifecycle, IEvents events) : IBrain
{
    const string AhZone = "Windurst Woods";
    const uint WhmId = 32;                    // the WHM party member (Zzshekashi)
    const string WhmName = "Zzshekashi";
    const byte GreatAxeSkill = 6;             // WAR weapon = Great Axe (drives the WS auto-pick)
    const string GrindZone = "Buburimu_Peninsula";
    const ushort GrindZoneId = 118;
    const ushort MhauraZone = 249;
    // Quest items are RARE/EX (user): each character can hold exactly ONE, no trading/bazaaring — the party
    // treasure pool routes the second drop to the other member automatically. So the WAR farms until ITS set
    // is complete (1 each) AND the WHM has REPORTED its own set complete over party chat ("SJITEMS t c r").
    const int ItemTarget = 1;

    // Dropper name -> quest item (verified against xiserver mob_groups/mob_droplist, zone 118).
    // Bull_Dhalmel is an NM: PreferTarget bypasses the NM name-skip (we explicitly want its drop) but the
    // con gate still applies — it's engaged only when it cons winnable (<= ConMax), NM or not.
    static readonly (string mob, ushort item)[] FarmItems =
    {
        ("Rarab",   QuestDefs.WildRabbitTail),      // Mighty_Rarab
        ("Dhalmel", QuestDefs.CupOfDhalmelSaliva),  // Bull_Dhalmel (10%)
        ("Bogy",    QuestDefs.BloodyRobe),
    };

    static readonly HashSet<ushort> Keep = new()
        { StealthRoutines.SilentOil, StealthRoutines.PrismPowder,
          QuestDefs.WildRabbitTail, QuestDefs.CupOfDhalmelSaliva, QuestDefs.BloodyRobe,
          1126, 1127,     // Beastmen's/Kindred's Seals — unsellable BCNM currency; a bag-clear DROPPED one (never again)
          940 };          // Revival Tree Root — Bogy 10% side-drop; it's the PLD unlock quest item (free banking)

    bool NeedItems() => FarmItems.Any(i => inv.CountOf(i.item) < ItemTarget) || !WhmSetComplete();

    // The WHM broadcasts "SJITEMS <tail> <cup> <robe>" over party chat (cross-zone) — Rare/EX means we can
    // never see its bag directly. Missing report = assume incomplete (keep farming).
    bool WhmSetComplete() => WhmQtyOf(0) >= 1 && WhmQtyOf(1) >= 1 && WhmQtyOf(2) >= 1;

    // WHM's count of FarmItems[idx] from its SJITEMS report (same tail/cup/robe order); -1 = no report yet.
    // PartyChat keeps only the sender's LATEST line, so a "RALLY" overwrote the report and every species
    // briefly read as "needed" (spurious Dhalmel steers post-rally) — cache the last line that parsed.
    string _lastSjitems = "";
    int WhmQtyOf(int idx)
    {
        if (p.World.PartyChat.TryGetValue(WhmName, out var c)
            && System.Text.RegularExpressions.Regex.IsMatch(c.msg, @"SJITEMS \d+ \d+ \d+"))
            _lastSjitems = c.msg;
        var m = System.Text.RegularExpressions.Regex.Match(_lastSjitems, @"SJITEMS (\d+) (\d+) (\d+)");
        return m.Success ? int.Parse(m.Groups[idx + 1].Value) : -1;
    }

    // A species is needed while EITHER character lacks its item (missing WHM report = assume needed).
    // Rare/EX routing makes this correct: a duplicate drop the holder can't take goes to the other char.
    bool SpeciesNeeded(int idx) => inv.CountOf(FarmItems[idx].item) < ItemTarget || WhmQtyOf(idx) < 1;

    // Feeds LevelGrind's per-species dropper-seek. Bogy is lv23-25 undead AND nocturnal: it joins only at
    // 24+ AND at in-game night (20:00-04:00 Vana) — a daytime Bogy steer marches to EMPTY ground and, being
    // nearest, it kept stealing every turn from the far Rarab camps (user-spotted).
    string[] NeededDroppers() => FarmItems
        .Select((f, i) => (f.mob, i))
        .Where(t => SpeciesNeeded(t.i)
                    // Bogy window opens at DUSK (18:00, spawns at 20:00): the steer clock + march ate most
                    // of each 19-real-minute night before arrival — pre-staging turns the whole night into
                    // kills (an entire session passed with ZERO Bogy kills under the 20:00 gate).
                    && (t.mob != "Bogy" || (p.World.MainJobLevel >= 24 && (Game.VanaTime.Hour >= 18 || Game.VanaTime.Hour < 4))))
        .Select(t => t.mob).ToArray();

    // WAR gear/abilities — REUSED from WarBrain (no duplicate sets/rotations).
    async Task EquipWarSet(CancellationToken ct)
    {
        ushort wep = p.World.MainJobLevel >= 20 ? WarBrain.Weapon20
                   : p.World.MainJobLevel >= 5 ? WarBrain.Weapon : WarBrain.EarlyWeapon;
        var gset = new List<(byte slot, uint item)> { (EquipSlot.Main, wep) };
        gset.AddRange(WarBrain.Armor.Select(g => (g.slot, (uint)g.item)));
        gset.AddRange(WarBrain.Armor21.Select(g => (g.slot, (uint)g.item)));
        int eq = await gear.EquipSet(gset, ct);
        Console.WriteLine($"[subjob] equipped {eq}/{gset.Count} gear pieces (lvl {p.World.MainJobLevel})");
    }

    // The 21/24-bracket shopping list: Neckchopper (lv20 here) + the Beetle set + Spike Necklace (lv21),
    // bought SELF-FUNDED from the AH at the first round-start where we're 20+ and missing pieces — the
    // fleet-test way (no GM handouts): guide bracket -> AH -> equip on ding.
    ushort[] BracketBuys() =>
        p.World.MainJobLevel < 20
            ? Array.Empty<ushort>()
            : new[] { WarBrain.Weapon20 }.Concat(WarBrain.Armor21.Select(g => g.item)).Where(i => !inv.Has(i)).ToArray();

    KillRoutine.Hooks FightHooks() => new()
    {
        // Provoke fires at need, not on cooldown: only when a mob has peeled onto the healer.
        UseAbilities = (mob, con, ct) => WarBrain.UseWarAbilities(combat, mob, con, p.World.Hpp,
            provoke: party.MemberCount > 0 && p.AttackersOn(WhmId) > 0, ct),
        WepSkillForLevel = _ => GreatAxeSkill,
        LedgePull = (id, ct) => combat.UseAbility(Ability.Provoke, id, ct),   // ~16y hate yank for ledge mobs
        Tag = "subjob",
    };

    // PartyDuty: a mob is beating on the squishy WHM — TOP priority. Provoke transfers hate regardless of
    // con (saves the healer even from a mob we can't kill), then fight it off us with the shared kill loop
    // (break off at 30% — a peel we can't damage must not become a death; the WHM reheals us).
    async Task<bool> RescueWhm(CancellationToken ct)
    {
        if (party.MemberCount == 0 || p.AttackersOn(WhmId) == 0) return false;
        long now = p.World.NowMs;
        var atk = p.Nearest(e => e.IsMob && e.Hpp > 0
            && p.World.Attackers.TryGetValue(e.Id, out var a) && a.target == WhmId && now - a.ms <= 6000);
        if (atk is null) return false;
        Console.WriteLine($"[subjob] RESCUE WHM — '{atk.Name}' is on the healer ({p.DistanceTo(atk.X, atk.Z):F0}y) — Provoke-peeling it onto us");
        for (int t = 0; t < 25 && !combat.Dead && !ct.IsCancellationRequested; t++)
        {
            if (!p.World.Entities.TryGetValue(atk.Id, out var cur) || cur.Hpp == 0) return true;
            if (p.DistanceTo(cur.X, cur.Z) <= 14f) { nav.Stop(); nav.Face(atk.Id); break; }
            nav.Follow(atk.Id);
            await Task.Delay(300, ct);
        }
        if (await combat.UseAbility(Ability.Provoke, atk.Id, ct))
            Console.WriteLine($"[subjob] Provoke -> peeled '{atk.Name}' off the WHM onto the WAR");
        // FIGHT TO THE DEATH — hate is unshakeable, so a 30%-HP break-off just meant standing there taking
        // hits WITHOUT swinging (a guaranteed death vs a Gambler at 77%). Swinging at least finishes weaker
        // adds; a truly unwinnable one kills us either way and the reunion recovers it.
        await KillRoutine.Fight(combat, p, nav, gear, atk, fightCon: 4, FightHooks(), breakOffHpp: 0, ct);
        return true;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(4000, ct);
        Console.WriteLine($"[subjob] char='{p.World.MyName}' lvl={p.World.MainJobLevel} dead={combat.Dead} zone={zoning.CurrentZone}");
        if (p.World.MainJobLevel < 18) { Console.WriteLine("[subjob] need lv18; abort"); lifecycle.Logout(); return; }

        if (combat.Dead) { Console.WriteLine("[subjob] logged in dead — homepointing"); await combat.Homepoint(ct); await Task.Delay(6000, ct); }
        // Wait for the inventory to stream before ANY bag-derived decision — an early empty read made
        // BracketBuys() think the whole gear set was missing and sent the WAR on a phantom Windurst trip.
        for (int t = 0; t < 40 && !p.World.Inventory.ToArray().Any(kv => kv.Key.container == 0 && kv.Value != 0) && !ct.IsCancellationRequested; t++)
            await Task.Delay(500, ct);
        await EquipWarSet(ct);

        // NO TOWN STAGING — the party invite lands CROSS-ZONE by char id (proven live: the roster formed with
        // the WAR in Buburimu and the WHM in Mhaura), so walking both bots to Mhaura to shake hands burned 5+
        // minutes per launch for nothing. Party up from wherever we logged in; the Reunion protocol owns
        // physical co-location at the grind-zone zone-in.
        Console.WriteLine("[subjob] waiting for the WHM's party invite (cross-zone, no staging)");
        for (int t = 0; t < 180 && party.MemberCount == 0 && !ct.IsCancellationRequested; t++)
        {
            if (party.InvitePending) party.AcceptInvite();
            if (t % 15 == 0) Console.WriteLine($"[subjob] awaiting party... MemberCount={party.MemberCount} invitePending={party.InvitePending}");
            await Task.Delay(1000, ct);
        }
        Console.WriteLine($"[subjob] party={party.MemberCount} — proceeding");

        // The subjob-unlock quest ("The Old Lady") is a shared LIFE-GOAL routine — this brain only supplies
        // the farm; SubjobQuest owns quest state + the accept/trade-completion drive (same instance WhmBrain
        // uses). Read accepted-state from it to gate the farm.
        var sjq = new SubjobQuest(p, nav, zoning, quests, trade, combat, gear, inv, ah, shop, events);
        bool accepted = sjq.Accepted();
        if (accepted && NeedItems())
        {
            // FARM = the shared party grind. Reunion owns zone entry (the duo crosses together) and every
            // split; the tether gates each pull on the WHM being close + topped; free roam finds the mobs.
            var reunion = new Reunion(p, nav, zoning, party, combat, chat, new Reunion.Config
            {
                PartnerId = WhmId, PartnerName = WhmName,
                GrindZone = GrindZone, GrindZoneId = GrindZoneId,
                StagingZone = "Mhaura", StagingZoneId = MhauraZone,
                Inviter = false, Tag = "war-rally",
                // Town errand each rally: stash surplus seals in the MOG CASE (EX items can't be mailed —
                // the delivery box refused them live). Seals don't stack — 17 of them pinned the bag at
                // 30/30 and pool drops (the CUP!) bounced off the full bag.
                AtStaging = async c =>
                {
                    // In town: bank seals into the MOG SAFE (the field-reachable Mog Case filled up at ~20).
                    if (await delivery.EnterMogHouse(c))
                    {
                        await MailRoutines.StashExcess(inv, p, 1126, keepMax: 2, c, MailRoutines.MogSafe);
                        await MailRoutines.StashExcess(inv, p, 1127, keepMax: 2, c, MailRoutines.MogSafe);
                        await delivery.ExitMogHouse(c);
                    }
                },
            });
            var hooks = FightHooks();
            var cfg = new LevelGrind.Config
            {
                FixedZone = GrindZone, FixedZoneId = GrindZoneId,
                // Self-funded gear bracket: at 20+ with pieces missing, LevelGrind's buy phase runs the AH
                // trip (Windurst Woods) before grinding; Reunion brings the duo back in together afterward.
                AhZone = AhZone,
                BuyItems = BracketBuys(),
                Keep = Keep.Concat(new[] { WarBrain.Weapon20 }).Concat(WarBrain.Armor21.Select(g => g.item)).ToHashSet(),
                Equip = EquipWarSet,
                WepSkillForLevel = hooks.WepSkillForLevel,
                // ConMax 3 (was 4): at WAR 23 the con-4s are lv24-26 (and Bull_Dhalmel is an NM) — one overnight
                // session lost EVERY logged con-4 fight (Zu, Dhalmel ×2, three KOs) while con ≤3 fights were clean.
                // Con-4 mobs re-enter the band naturally as we level past them. Con stays the sole arbiter.
                ConMin = 2, ConMax = 3,
                UseAbilities = hooks.UseAbilities,
                LedgePull = hooks.LedgePull,
                SkipMobNames = new[] { "Saplin", "Mandragora" },   // sleep-lock: the only allowed name skip
                // Prefer a dropper while EITHER char needs its item (Rare/EX pool routes duplicates correctly).
                PreferTarget = e => FarmItems.Select((f, i) => (f.mob, i)).Any(t => SpeciesNeeded(t.i)
                                    && e.Name.Contains(t.mob, StringComparison.OrdinalIgnoreCase)),
                SeedNames = FarmItems.Select(i => i.mob).ToArray(),   // dropper clusters seed the map at any level
                NeededDroppers = NeededDroppers,                      // per-species starvation treks (tail vs cup vs robe)
                CommitDropperTreks = true,                            // march straight to the steered camp (user call at 27)
                PreferredOnly = true,                                 // ITEMS ONLY now (user, day 3): Rarabs + Bogys, nothing else
                // Never pull with a known-tough mob near the HEALER's spot either (an add on the WHM killed
                // it mid-fight, and the tank then lost a fight it was winning).
                GuardSpot = () => p.World.Entities.TryGetValue(WhmId, out var e) && (e.X != 0 || e.Z != 0)
                                  ? (e.X, e.Z) : null,
                PullLeash = 22f,                             // never sprint to a pull the healer can't follow
                RoamHop = 22f,                               // short hops so the following WHM stays in view+cure range
                Reunion = reunion,
                PartyDuty = RescueWhm,
                BeforePull = async c =>
                {
                    // minMp == the WHM's rest target (50) EXACTLY: any gap (45 vs 50, or 30 vs 50 before)
                    // opens a window where the gate passes while the healer is still seated — the WAR pulled
                    // mid-rest twice. The >= comparison resolves the boundary; the WHM's Rest abort stands it
                    // up instantly if a pull does land early.
                    // 28y: cure range is 20y but the WHM closes during the approach anyway, and a 22y gate
                    // right at the follow-trail distance made every 66y exploration leg stop-and-wait.
                    // 34y (was 28): the WHM follows ~10y behind + packet lag, so a marching WAR sat at
                    // dist 28-29 flapping the gate on/off — minutes of march time lost per leg.
                    bool good = await PartyRoutines.WaitAllGood(p, nav, party, WhmId, within: 34f, minHp: 70, minMp: 50, c);
                    // Picked up aggro during the wait: abort THIS pull without rallying — the loop's aggro
                    // defense fights the attacker first (never open a second fight while one is on us).
                    if (p.AttackersOn(p.World.MyId) > 0) return false;
                    if (!good)
                    {
                        Console.WriteLine("[subjob] tether timeout — forcing a rally to regroup");
                        reunion.Force();
                        return false;
                    }
                    return true;
                },
                // FULL keep-set including the gear bracket — passing the static Keep here let a bag-clear
                // sell the unequipped Spike Necklace (equipped pieces were server-protected; loose ones not).
                OnBagFull = async c =>
                {
                    // Stash BEFORE selling: seals are keep-set (unsellable junk-pass survivors) and the Mog
                    // Case move works in the field — this is what actually frees slots when seals pile up.
                    await MailRoutines.StashExcess(inv, p, 1126, keepMax: 2, c);
                    await MailRoutines.StashExcess(inv, p, 1127, keepMax: 2, c);
                    await inv.SellAllJunk(
                        Keep.Concat(new[] { WarBrain.Weapon20 }).Concat(WarBrain.Armor21.Select(g => g.item)).ToHashSet(), c);
                },
                SellAtItems = 24,
                OnKill = _ =>   // per-kill farm telemetry: drop progress visible mid-run, not just at the end
                {
                    Console.WriteLine($"[subjob] tally: tail={inv.CountOf(QuestDefs.WildRabbitTail)}/{ItemTarget} cup={inv.CountOf(QuestDefs.CupOfDhalmelSaliva)}/{ItemTarget} robe={inv.CountOf(QuestDefs.BloodyRobe)}/{ItemTarget} lvl={p.World.MainJobLevel}");
                    return Task.CompletedTask;
                },
                // Rest healthier than the solo defaults: the aggro belt between camps and the dropper clusters
                // forces fights — entering it at ~55% HP (old trigger 50) turned forced fights into KOs, and
                // every KO resets the duo to the eastern zone-in, undoing the march west.
                RestHpTrigger = 70, RestHpTarget = 90,
                Done = () => !NeedItems(),
                Tag = "subjob",
            };
            await new LevelGrind(p, nav, combat, zoning, gear, ah, delivery, inv, shop, cfg).RunAsync(ct);
            Console.WriteLine($"[subjob] farm done: tail={inv.CountOf(QuestDefs.WildRabbitTail)} cup={inv.CountOf(QuestDefs.CupOfDhalmelSaliva)} robe={inv.CountOf(QuestDefs.BloodyRobe)}");
        }

        // Drive the quest through the shared SubjobQuest.Advance — the powder-stock + stealth-cross to Mhaura
        // + accept-or-trade-completion sequence lives there ONCE (WhmBrain drives it the same way). No inline
        // copy of the crossing/QuestRunner call here.
        bool ok = await sjq.Advance(ct);
        Console.WriteLine($"[subjob] flow done (ok={ok}, dead={combat.Dead}, lastEvent={p.World.EventId})");
        lifecycle.Logout();
    }
}
