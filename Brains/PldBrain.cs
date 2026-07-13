namespace XiHeadless.Brains;

/// PALADIN life path (the WAR char's destiny per the user's roster: PLD/WAR) — brain = config only.
/// Full arc via the shared JobLifecycle: WAR sub -> 30 (already there) -> unlock quest (A Squire's Test I/II
/// in San d'Oria + Ordelle's Caves, then A Knight's Test) -> switch to PLD/WAR -> seesaw PLD main / WAR sub.
/// The Ordelle's pool/dew qm sit past a navmesh gap that recast dropped; a per-zone off-mesh drop link
/// (Navigation/NavLinks) now bridges the shelf -> pool descent, so QuestRunner can reach and trigger them
/// (the qm use messageSpecial — pure Talk trigger, retried). Needs a Revival Tree Root (940) for Squire I.
public sealed class PldBrain(
    IPerception p, INavigation nav, ICombat combat, IMagic magic, IZoning zoning, IGear gear, IAuctionHouse ah,
    IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs, IQuests quests, ITradeNpc trade, IEvents events,
    ILifecycle lifecycle, IChat chat) : IBrain
{
    const byte SwordSkill = 3;
    const string AhZone = "Windurst Woods";   // home-nation AH (char is Windurst)

    static readonly (ushort item, byte slot, byte lvl)[] Gear =
    {
        (15180, EquipSlot.Head, 1),    // Cache-nez
        (16279, EquipSlot.Neck, 3),    // Pile Chain
        (16611, EquipSlot.Main, 11),   // Bee Spatha +1
        (13240, EquipSlot.Waist, 15),  // Warrior's Belt +1
        (15486, EquipSlot.Back, 18),   // Breath Mantle
        (17708, EquipSlot.Main, 19),   // Auriga Xiphos
        (13061, EquipSlot.Neck, 21),   // Spike Necklace
        (12306, EquipSlot.Sub, 28),    // Kite Shield (also the unlock-quest reward)
        (15171, EquipSlot.Head, 29),   // Kampfschaller
        (14435, EquipSlot.Body, 29),   // Kampfbrust
        (14863, EquipSlot.Hands, 29),  // Kampfhentzes
        (14332, EquipSlot.Legs, 29),   // Kampfdiechlings
        (15321, EquipSlot.Feet, 29),   // Kampfschuhs
    };

    // Full arc via JobLifecycle. The unlock chain runs in San d'Oria/Ordelle's, so stealth-trek there first.
    public async Task RunAsync(CancellationToken ct)
    {
        // PARK: the unlock is blocked by an intermittent, unidentifiable in-event state (ServerStatus=4) at
        // the Southern San d'Oria WEST gate (230->100) — the char carries a stuck event the auto-completer's
        // known-id sweep can't clear, and we have no map-server log to read the real csid. Rather than burn
        // ~40-min trek-and-fail retry cycles, idle ONLINE until the blocker is resolved (server-log access to
        // ID the event, OR provision PLD chars in San d'Oria to skip the cross-continent trek). rm the flag
        // to resume. See memory: pld-westgate-event-block.
        if (File.Exists("/tmp/xibot_pld_hold"))
        {
            Log.Info("[pld] HOLD — unlock blocked at the S.San d'Oria west-gate event; parked (rm /tmp/xibot_pld_hold to resume)");
            // KEEP THE CONNECTION ALIVE while parked: a pure Task.Delay idle got dropped by the server, which
            // left a stale session -> next login 0xA2/0x24 crash -> self-perpetuating loop (junk-char risk).
            // A small periodic nav nudge keeps position packets flowing so the session stays valid.
            var (hx, hz) = (p.World.X, p.World.Z);
            bool toggle = false;
            while (File.Exists("/tmp/xibot_pld_hold") && !ct.IsCancellationRequested)
            {
                toggle = !toggle;
                nav.MoveTo(hx + (toggle ? 2f : -2f), hz);   // shuffle ~2y in place — keepalive, stays in the city
                await Task.Delay(20000, ct);
            }
            if (ct.IsCancellationRequested) return;
            Log.Info("[pld] HOLD cleared — exiting to relaunch into the unlock attempt");
            lifecycle.Logout();
            return;
        }
        await RunLifecycle(ct);
    }

    Task RunLifecycle(CancellationToken ct) =>
        new JobLifecycle(p, nav, combat, zoning, gear, ah, delivery, inv, shop, jobs, quests, trade, events,
            new JobLifecycle.Config
            {
                MainJob = Job.Pld, SubJob = Job.War, Advanced = true,
                UseGmGrant = true,   // ask the central GM bot to unlock PLD (fast path); quest is the fallback
                // The unlock is a 3-quest San d'Oria chain (done-bitmap port 0x90). Grouping the steps by
                // quest + done-bit lets JobLifecycle skip whichever quests are already complete, so a death
                // mid-chain RESUMES instead of re-walking Squire I/II from scratch. Prereqs[Pld] = A Squire's
                // Test (2 steps) then A Squire's Test II (4 steps); Unlock[Pld] = A Knight's Test (5 steps).
                UnlockChain = new[]
                {
                    new JobLifecycle.UnlockQuest(10, 0x90, QuestDefs.Prereqs[Job.Pld].Take(2).ToArray()),  // A Squire's Test
                    new JobLifecycle.UnlockQuest(19, 0x90, QuestDefs.Prereqs[Job.Pld].Skip(2).ToArray()),  // A Squire's Test II
                    new JobLifecycle.UnlockQuest(29, 0x90, QuestDefs.Unlock[Job.Pld]),                     // A Knight's Test
                },
                StealthUnlock = true,                       // the overland trek to San d'Oria crosses lv30-40 aggro
                UnlockTrekZone = "Southern_San_dOria", UnlockTrekZoneId = 230,
                GrindCfgFor = GrindCfg, Tag = "pld",
            }, chat: chat, magic: magic).RunAsync(ct);

    LevelGrind.Config GrindCfg(byte job) => new()
    {
        HomeNation = Nation.Windurst,
        AhZone = AhZone,
        BuyItems = GearRoutines.BuyList(Gear).ToArray(),
        // Keep the Revival Tree Root (940, Squire I trade item) and the stealth stock (else the junk-sell
        // dumps the oils and the AH re-buys them — a bag-pinning churn loop).
        Keep = GearRoutines.KeepSet(Gear, 1126, 1127, 940, StealthRoutines.SilentOil, StealthRoutines.PrismPowder),
        Equip = Equip,
        WepSkillForLevel = _ => SwordSkill,   // PLD rides swords; the WAR sub phase keeps its Great Axe (Equip)
        ConMin = 1, ConMax = 3,
        CleanPullNeighborCon = 3,
        SkipMobNames = new[] { "Saplin", "Mandragora" },
        RestHpTrigger = 70, RestHpTarget = 90,
        Tag = "pld",
    };

    async Task Equip(CancellationToken ct)
    {
        // WAR sub-to-30 phase keeps the proven Great Axe kit (owned already).
        (byte slot, ushort item)? phase = p.World.MainJob == Job.War ? (EquipSlot.Main, WarBrain.Weapon20) : null;
        var (n, total) = await GearRoutines.EquipByLevel(gear, p, Gear, ct, phase);
        Log.Info($"[pld] equipped {n}/{total} (job {p.World.MainJob} lvl {p.World.MainJobLevel}, sword={gear.SkillLevel(SwordSkill)})");
    }
}
