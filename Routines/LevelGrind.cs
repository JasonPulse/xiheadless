namespace XiHeadless.Routines;

/// The job-agnostic grind loop — solo leveling AND party farming run THIS one implementation. Gear up
/// (optional AH buy), reach the hunt zone, then loop: pick a target by CON, close, fight (the shared
/// KillRoutine — weapon skills + abilities + clean engagement), rest, roam on. Movement between fights is
/// FREE ROAM (RoamController): committed hops steered by con — no camp anchors, no per-zone geometry.
///
/// Everything job-specific (gear, abilities, con band, rest targets) comes via Config; everything
/// party-specific comes via the optional party hooks (Reunion, PartyDuty, BeforePull, PullLeash,
/// PreferTarget) so a duo farm brain is just a Config, not a forked loop.
public sealed class LevelGrind(
    IPerception p, INavigation nav, ICombat combat, IZoning zoning, IGear gear,
    IAuctionHouse ah, IDelivery delivery, IInventory inv, IShop shop, LevelGrind.Config cfg)
{
    public sealed class Config
    {
        public Nation HomeNation;                 // selects the HuntZones leveling path (path mode)
        public string AhZone = "";                // MISC_AH zone to buy gear in (only used if BuyItems set)
        public ushort[] BuyItems = Array.Empty<ushort>();   // gear to buy from the AH (empty = skip the buy phase)
        public HashSet<ushort> Keep = new();      // items we never sell when freeing bag space
        public Func<CancellationToken, Task> Equip = _ => Task.CompletedTask;  // re-run on level-up
        public Func<byte, byte> WepSkillForLevel = _ => 1;  // skill id of the weapon equipped at this level
        public int ConMin = 2, ConMax = 2;        // con band to engage
        // SENTINEL defaults (shared statics, not fresh lambdas): JobLifecycle reference-compares against
        // these to know a brain wired NO kit, and injects the generic JobKits rotation for the job.
        public static readonly Func<uint, int, CancellationToken, Task> NoAbilities = (_, _, _) => Task.CompletedTask;
        public static readonly Func<CancellationToken, Task<bool>> NoHeal = _ => Task.FromResult(false);
        public Func<uint, int, CancellationToken, Task> UseAbilities = NoAbilities;
        public int RestHpTrigger = 50;            // rest after a kill if HP% below this...
        public int RestHpTarget = 75;             // ...up to this HP%
        public int RestMpPct = 0;                 // also recover MP to this % (mages); 0 = HP-only (melee)
        public bool SellJunkWhenFull = false;     // vendor round-trip when the bag fills (off: trips cost grind time)
        public int SellAtItems = 25;
        // In-place bag clearing (inv.SellAllJunk) for farms where drops must keep landing but a vendor trip
        // would strand the party (e.g. the duo in Buburimu). Checked before SellJunkWhenFull; rate-limited.
        public Func<CancellationToken, Task>? OnBagFull;
        public Func<CancellationToken, Task> OnRestock = _ => Task.CompletedTask;   // post-sell, at the vendor with fresh gil
        public string[] SkipMobNames = CombatRoutines.SleepLockMobs;   // universal hazards only — the sanctioned sleep-lock pair by default
        public Func<CancellationToken, Task> OnSetup = _ => Task.CompletedTask;     // one-time, after zone-in
        public Func<uint, CancellationToken, Task> Pull = (_, __) => Task.CompletedTask;   // ranged opener (WHM Dia)
        public Func<CancellationToken, Task<bool>> EmergencyHeal = NoHeal;
        public Func<CancellationToken, Task> PostKillHeal = _ => Task.CompletedTask;
        public Func<uint, CancellationToken, Task<bool>>? LedgePull;   // ranged hate yank for ledge mobs (WAR Provoke)

        // ---- FIXED-ZONE mode (party item farms): grind THIS zone instead of the nation's leveling path.
        // Zone ENTRY is owned by Reunion when set (the duo crosses together); never a solo march.
        public string FixedZone = "";
        public ushort FixedZoneId;

        // ---- Party hooks (all optional; defaults = solo behavior unchanged) ----
        public Reunion? Reunion;                                    // split/reunite owner, polled at loop top
        public Func<CancellationToken, Task<bool>>? PartyDuty;      // pre-target duty (peel a mob off the healer); true = acted, re-loop
        public Func<CancellationToken, Task<bool>>? BeforePull;     // tether/ready-check right before engaging; false = abort this pull
        public float PullLeash = 0f;                                // >0: only select targets within this range (don't outrun the healer)
        public Func<(float x, float z)?>? GuardSpot;                // a spot that must also be threat-free before a pull (the healer's position)
        public Func<Entity, bool>? PreferTarget;                    // priority targets (item droppers): engaged first, allowed below
                                                                    // ConMin (we want the DROP), bypasses the NM skip (explicit ask) —
                                                                    // but NEVER the too-tough gate (con > ConMax still skips)
        public float RoamHop = 150f;                                // roam step length (short ~22y in a party so the healer keeps up)
        public string[] SeedNames = Array.Empty<string>();          // dropper names whose spawn clusters seed the roam memory at ANY level
        public Func<string[]>? NeededDroppers;                      // dropper names STILL NEEDED (subset of SeedNames); null = all of SeedNames
        public int CleanPullNeighborCon = 5;                        // reject pulls with a neighbor conning >= this (solo fragile brains: use ConMax)
        public bool CommitDropperTreks = false;                     // opt-in: dropper steers MARCH (no ordinary pulls until arrival)
        public bool PreferredOnly = false;                          // opt-in farm endgame: engage ONLY preferred targets (droppers);
                                                                    // ordinary exp mobs are ignored entirely (forced fights still happen)
        public bool StealthTravel = false;                          // opt-in fragile bots: Sneak/Invis (consumables permitting) on zone-travel legs
        public ushort TravelVia;                                    // opt-in: route hunt-zone returns THROUGH this zone (0 = direct). Which zone line you enter by decides which mobs you spawn next to — a lvl-1 entering West Saruta by the east gate walks the goblin belt; entering from Windurst lands on the bee ground.
        public Func<CancellationToken, Task>? RecoveryTravel;       // opt-in: brain OWNS the hunt-zone return (revive far away, cross hostile ground). Called instead of the default travel; must leave the bot IN the hunt zone. For a baby whose home point is unreachable-safely (crystal relocation is unreliable) — e.g. switch to a strong job, walk, switch back.
        public Func<bool>? Done;                                    // optional exit condition (e.g. all farm items collected)
        public Func<CancellationToken, Task>? OnKill;               // after each confirmed kill (e.g. log farm-item tallies)
        public string Tag = "bot";                                  // log prefix
    }

    readonly HashSet<uint> _skip = new();
    int _kills, _tooWeak;
    long _lastBagClearMs, _lastSkipClearMs;
    byte _lastHpp = 100;   // to detect "HP dropped while not engaged" = something aggroed us
    float _trackX, _trackZ; double _walkedSinceFight;   // distance-between-pulls metric (wandering waste, per user)
    long _lastKillMs;                                    // hunger clock: dry spells force a deep trek
    long _lastWeakLogMs;                                 // throttle for the post-revive weakness hold log line
    string? _lastSteerTarget;                            // last dropper-seek species — a fruitless repeat yields its turn
    int _dryTreks;                                       // consecutive hunger treks with no kill — escalates trek DISTANCE
    long _campUntilMs;                                   // camp window after a needed-dropper kill (wait out repops)
    bool _hadNeeded;                                     // edge-detect: needed set just went empty -> release stale priority ground
    readonly Dictionary<string, long> _dropperKillMs = new();   // PER-SPECIES dropper-seek clocks. A single shared
                                                         // clock was reset by ANY dropper kill — plentiful Dhalmels
                                                         // fed it forever and the Rarab clusters were never trekked
                                                         // (41 kills, zero Rarab engagements in one overnight log).

    void Log(string m) => XiHeadless.Log.Auto($"[{cfg.Tag}] {m}");

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(3000, ct);
        // Gil + inventory stream in over the first few seconds after zone-in; wait for gil before any
        // buy phase or every bid reads 0 ("out of budget") and we buy nothing.
        for (int i = 0; i < 30 && p.World.Gil == 0 && !ct.IsCancellationRequested; i++) await Task.Delay(500, ct);
        Log($"char='{p.World.MyName}' job={p.World.MainJob}/{p.World.SubJob} lvl={p.World.MainJobLevel} gil={p.World.Gil} zone={zoning.CurrentZone}");

        // If we logged in KO'd, homepoint FIRST — otherwise we'd walk the corpse around before the loop's
        // Dead check runs.
        if (combat.Dead)
        {
            Log("logged in KO'd — homepointing to revive before grinding");
            await combat.Homepoint(ct);
            await Task.Delay(5000, ct);
        }

        // Logged in inside the Mog House (zone 0)? Step out — every travel needs a real source zone.
        if (zoning.CurrentZone == 0)
        {
            Log("inside the Mog House (zone 0) — exiting to the city");
            await delivery.ExitMogHouse(ct);
            await Task.Delay(2500, ct);
        }

        await cfg.OnSetup(ct);

        Task<int> SellJunk(CancellationToken c) => ShopRoutines.SellNearby(shop, nav, zoning, inv, p, cfg.Keep, c);

        // 1) Optional: gear up from the AH, then carry it to the hunt zone.
        if (cfg.BuyItems.Length > 0)
        {
            if (!Game.Zonelines.HasAuctionHouse(zoning.CurrentZone))
            {
                Log($"traveling to {cfg.AhZone} for gear");
                await zoning.GoTo(cfg.AhZone, ct);
            }
            foreach (var item in cfg.BuyItems)
            {
                if (ct.IsCancellationRequested) return;
                await ShopRoutines.BuyItem(ah, p, inv, item, cfg.Keep, SellJunk, ct);
            }
        }

        // 2) Reach the hunt zone. Path mode travels solo; fixed-zone mode with a Reunion defers entry to the
        //    rally protocol below (the duo crosses TOGETHER — a solo march into a party zone is the old desync).
        bool fixedMode = cfg.FixedZoneId != 0;
        var hunter = fixedMode ? null : new Hunter(nav, p, cfg.HomeNation);
        ushort ZoneNow() => fixedMode ? cfg.FixedZoneId : hunter!.TargetZone();
        string ZoneNowName() => fixedMode ? cfg.FixedZone : hunter!.TargetZoneName();
        if (!fixedMode && zoning.CurrentZone != ZoneNow() && zoning.CurrentZone != 0)
        {
            await zoning.ToZone(ZoneNow(), ct);
            await hunter!.GoToCamp(ct);   // arrival hint only: walk off the sparse zone-in ledge toward mob density
        }
        await cfg.Equip(ct);

        // Free-roam controller: shares its con cache with target selection, steers hops away from known
        // con>=5 mobs and toward in-band prey. This replaces camp anchors entirely. Prey memory persists
        // per zone across sessions (learned at runtime — the anti-hardcoding way to "know the camps").
        var roam = new RoamController(nav, p, combat, new RoamController.Config
        {
            HopLength = cfg.RoamHop, ConMin = cfg.ConMin, ConMax = cfg.ConMax, Tag = cfg.Tag,
            MemoryFile = $"/tmp/xibot_prey_{ZoneNow()}.mem",
            CommitPriorityTreks = cfg.CommitDropperTreks,
            AvoidNames = cfg.SkipMobNames,   // sleep-lock mobs: steer around their aggro radius, don't just skip-target
        });

        var killHooks = new KillRoutine.Hooks
        {
            UseAbilities = cfg.UseAbilities,
            EmergencyHeal = cfg.EmergencyHeal,
            WepSkillForLevel = cfg.WepSkillForLevel,
            LedgePull = cfg.LedgePull,
            Tag = cfg.Tag,
        };

        // Seed the roam memory from GENERATED spawn data for this zone: clusters of mobs in our engage band
        // (mob level ≈ [char-5, char-1] cons ~2-4) plus the brain's droppers at any level. Re-seeded on every
        // level-up as the band shifts — the dynamic, all-zones answer to "where are the camps" with zero
        // hardcoding; live /check cons at engage time still decide every fight.
        void SeedFromSpawnData()
        {
            // Band floors: at lvl 1 the naive [lvl-5, lvl-1] band is EMPTY (no lv<=0 mobs exist), leaving only
            // stale persistent memory to steer — a fresh BLM was led to mid-zone lv10+ camps and found nothing
            // it could legally fight for an hour. [max(1,..), max(2,..)] seeds the gate-side lv1-2 clusters.
            roam.SeedMemory(Game.SpawnClusters.For(ZoneNow(),
                System.Math.Max(1, p.World.MainJobLevel - 5), System.Math.Max(2, p.World.MainJobLevel - 1), cfg.SeedNames));
            if (cfg.SeedNames.Length > 0)
                // Needed droppers only — the raw SeedNames pool trekked a lv23 duo toward Bogy (lv23-25) ground.
                roam.SetPriority(Game.SpawnClusters.ForNames(ZoneNow(), cfg.NeededDroppers?.Invoke() ?? cfg.SeedNames));
        }
        SeedFromSpawnData();

        // Shared safe-rest: step DIRECTLY AWAY from any threat first (never sit where an aggressive wanderer
        // can catch us seated), bail to the fight loop the instant anything is attacking us. Called post-kill
        // AND from the loop top whenever HP is under the rest line (rest used to be post-kill only, so an
        // interrupted rest let the loop chain pulls at 37% HP — that killed the lv4 THF twice).
        async Task RestSafely(CancellationToken c)
        {
            for (int s = 0; s < 3 && !c.IsCancellationRequested; s++)
            {
                if (p.AttackersOn(p.World.MyId) > 0) break;   // being chased — fight now, don't shuffle
                // 40y (was 25y): the deadly window is fight-end-at-low-HP -> seated; patrollers 25-40y out
                // kept wandering in mid-rest and killing the resting bot (three THF deaths, same shape).
                if (await roam.RestThreat(40f, c) is not { } rt) break;
                Log("rest spot unsafe — stepping away before sitting");
                if (!RestRoutines.StepAway(nav, p, rt.X, rt.Z, 30f)) await roam.StepAsync(c);
                for (int t = 0; t < 30 && nav.IsMoving && !c.IsCancellationRequested; t++) await Task.Delay(400, c);
            }
            if (p.AttackersOn(p.World.MyId) > 0) { Log("aggro caught us before resting — fighting first"); return; }
            Log($"resting (HP {p.World.Hpp}% MP {p.World.Mpp}%)");
            nav.Stop();
            if (await combat.Rest(cfg.RestHpTarget, cfg.RestMpPct, null, c)) Log($"rested to HP {p.World.Hpp}% MP {p.World.Mpp}%");
            else Log($"rest interrupted (add on us?) — back to the loop (HP {p.World.Hpp}%)");
        }

        Log($"hunting in {ZoneNowName()} ({ZoneNow()}) — free roam, hop={cfg.RoamHop}y");

        byte lastLevel = p.World.MainJobLevel;
        _trackX = p.World.X; _trackZ = p.World.Z;
        while (!ct.IsCancellationRequested)
        {
            // Distance-between-pulls metric: accumulate actual ground covered each tick. High values between
            // fights = the roam is wandering, not hunting — the number that makes it visible.
            {
                float ddx = p.World.X - _trackX, ddz = p.World.Z - _trackZ;
                _walkedSinceFight += Math.Sqrt(ddx * ddx + ddz * ddz);
                _trackX = p.World.X; _trackZ = p.World.Z;
            }
            if (cfg.Done is { } done && done()) { Log("goal reached — grind complete"); nav.Stop(); return; }

            // ANY split (our death, partner dead/zoned, RALLY chat, forced) → the shared reunion protocol.
            if (cfg.Reunion is { } ru && ru.SplitDetected())
            {
                nav.Stop();
                await ru.RunAsync(ct);
                _lastHpp = p.World.Hpp;
                continue;
            }

            if (p.World.MainJobLevel > lastLevel)
            {
                Log($"LEVEL UP -> {p.World.MainJobLevel}, re-equipping");
                lastLevel = p.World.MainJobLevel;
                _skip.Clear();   // re-judge everything by con at the new level
                await cfg.Equip(ct);
                SeedFromSpawnData();   // the engage band moved — newly-viable populations enter the map
            }

            // Dead (KO'd): homepoint to revive (the core handler also drives this), then loop back to travel.
            if (combat.Dead)
            {
                nav.Stop();
                Log("KO'd — returning to home point to revive");
                await combat.Homepoint(ct);
                await Task.Delay(5000, ct);
                continue;
            }

            // Out of the hunt zone (revived / sell trip / advancing legs). With a Reunion the duo must enter
            // TOGETHER — force a rally instead of marching in solo.
            if (zoning.CurrentZone != ZoneNow() && zoning.CurrentZone != 0)
            {
                if (cfg.Reunion is { } ru2) { Log($"out of {ZoneNowName()} — forcing a rally so the duo re-enters together"); ru2.Force(); continue; }
                // RecoveryTravel hook: a fragile bot whose home point is across a hostile zone (baby BLM
                // revives in Mhaura, must cross Buburimu) can't survive the return as its grind job and the
                // crystal home-point relocation proved unreliable. When set, the brain owns the whole return
                // (e.g. switch to a strong job, walk, switch back) and we skip the default travel entirely.
                if (cfg.RecoveryTravel is { } recov)
                {
                    Log($"out of {ZoneNowName()} — delegating return to the brain's RecoveryTravel");
                    await recov(ct);
                    _tooWeak = 0;
                    await cfg.Equip(ct);
                    if (hunter != null && zoning.CurrentZone == ZoneNow()) await hunter.GoToCamp(ct);
                    continue;
                }
                // TravelVia: enter the hunt zone from the SAFE side. This leg goes to the waypoint zone;
                // the loop comes back around and the next leg crosses waypoint -> hunt zone.
                ushort dest = cfg.TravelVia != 0 && zoning.CurrentZone != cfg.TravelVia ? cfg.TravelVia : ZoneNow();
                Log(dest == ZoneNow() ? $"traveling to hunt zone {ZoneNowName()} ({ZoneNow()})"
                                      : $"traveling to hunt zone {ZoneNowName()} via zone {dest} (safe-side entry)");
                // StealthTravel bots that run out of powders must NOT cross unstealthed (the silent
                // HasPowders fallthrough was a level-1 death loop: revive at Mhaura -> bare crossing -> die).
                // The revive town has an AH — restock there before the leg.
                if (cfg.StealthTravel && !StealthRoutines.HasPowders(inv) && Game.Zonelines.HasAuctionHouse(zoning.CurrentZone))
                {
                    Log("stealth stock empty — restocking at the local AH before the crossing");
                    await StealthRoutines.EnsureStock(ah, p, inv, 12, cfg.Keep, SellJunk, ct);
                }
                if (cfg.StealthTravel && !StealthRoutines.HasPowders(inv)) Log("NO stealth stock and no AH here — crossing bare (dangerous)");
                if (cfg.StealthTravel && StealthRoutines.HasPowders(inv))
                {
                    // Opt-in for fragile bots: a lvl-1 whose HOME POINT sits across a hostile zone died on
                    // every post-revive return leg. APPLY WHILE STANDING STILL FIRST — item use is
                    // interrupted by movement, so the old walk-concurrent apply never landed (bot died in
                    // ~30s "stealthed"). The background maintainer is best-effort top-up only.
                    Log("stealth travel leg (applying before moving)");
                    nav.Stop();
                    await StealthRoutines.Apply(inv, p, ct);
                    // Re-apply at EVERY zone line via BeforeLeg (standing still) — the old background
                    // Maintain re-applied mid-walk, movement interrupted the item use, and stealth lapsed
                    // partway through every multi-zone crossing (the level-1 death loop).
                    zoning.BeforeLeg = c2 => StealthRoutines.HasPowders(inv) ? StealthRoutines.Apply(inv, p, c2) : Task.CompletedTask;
                    try { await zoning.ToZone(dest, ct); }
                    finally { zoning.BeforeLeg = null; }
                    await Task.Delay(2000, ct);
                }
                else await zoning.ToZone(dest, ct);
                _tooWeak = 0;
                await cfg.Equip(ct);
                if (hunter != null) await hunter.GoToCamp(ct);
                continue;
            }

            // Bag filling with drops: clear IN PLACE when the brain provides a way (party farms mustn't leave
            // the zone), else optionally bank them at a vendor.
            if (cfg.OnBagFull is { } bagFull && CountItems() >= cfg.SellAtItems && p.World.NowMs - _lastBagClearMs > 60_000)
            {
                Log($"bag at {CountItems()} items — clearing junk in place to keep room for drops");
                _lastBagClearMs = p.World.NowMs;
                await bagFull(ct);
                continue;
            }
            if (cfg.SellJunkWhenFull && CountItems() >= cfg.SellAtItems)
            {
                Log($"bag at {CountItems()} items — selling drops at nearest vendor for gil");
                nav.Stop();
                await SellJunk(ct);
                await cfg.OnRestock(ct);
                continue;
            }

            // HUNGER: a dry spell means this ground can't feed us — trek to a deeper seed. Checked at loop
            // top (NOT only when no target is visible): standing in a pocket of findable-but-unkillable
            // con-5s skipped every mob each loop and the old empty-branch check never ticked.
            if (_lastKillMs == 0) _lastKillMs = p.World.NowMs;
            // CAMP a producing dropper ground: a needed-species kill extends the hunger window past the
            // ~5-min repop, so the farm waits for respawns instead of trekking away after clearing 1-2
            // spawns (the whole run managed FIVE Rarab kills because every visit self-evicted at 150s).
            long hungerWindow = p.World.NowMs < _campUntilMs ? 420_000 : 150_000;
            if (p.World.NowMs - _lastKillMs > hungerWindow)
            {
                // ESCALATE after 3 fruitless treks: nearest-first kept shuffling adjacent guarded spots
                // (night-skeleton pocket) — from the 3rd dry trek on, jump to the FARTHEST known ground.
                _dryTreks++;
                // ZONE-RESET after 6: far treks can't escape a DISCONNECTED MESH POCKET (live fleet NIN in
                // S.Gustaberg: every mob 30-50y away "couldn't close", every trek re-rolled inside the same
                // pocket, for an hour). Hop through the nearest zone line — the re-entry point is on-mesh by
                // construction and outside the pocket; the existing wrong-zone handling (RecoveryTravel /
                // Hunter.TargetZone) owns the walk back to the hunt ground.
                if (_dryTreks >= 6 && Game.Zonelines.All.FirstOrDefault(z => z.From == zoning.CurrentZone) is { To: not 0 } exitLine)
                {
                    Log($"still dry after {_dryTreks} treks — zone-resetting via {Game.Zonelines.Name(exitLine.To)} (mesh-pocket escape)");
                    await zoning.ToZone(exitLine.To, ct);
                    _dryTreks = 0;
                    _lastKillMs = p.World.NowMs;
                    continue;
                }
                Log($"hunt is dry here (150s, no kill) — forcing a deep trek{(_dryTreks >= 3 ? " [FAR — escaping fruitless ground]" : "")}");
                roam.ForceTrek(far: _dryTreks >= 3);
                _lastKillMs = p.World.NowMs;
            }
            // DROPPER-SEEK (farms only): good exp ground held the duo for HOURS with zero Rarab kills —
            // the hunger clock never fires while ordinary kills flow. If no PREFERRED-target kill lands
            // within the window, trek onward regardless; the cooldown rotation reaches the dropper clusters.
            string[] neededNow = cfg.SeedNames.Length > 0 ? (cfg.NeededDroppers?.Invoke() ?? cfg.SeedNames) : Array.Empty<string>();
            if (cfg.SeedNames.Length > 0)
            {
                var needed = neededNow;
                // NOTHING farmable right now (e.g. only a NOCTURNAL dropper left, and it's day): clear the
                // stale priority pool — hunger treks otherwise marched between EMPTY day-time Bogy clusters
                // forever (three frozen checks, zero kills). Ordinary hunting resumes below until night.
                if (needed.Length == 0 && _hadNeeded) { Log("no dropper species farmable right now — releasing priority ground until one returns"); roam.SetPriority(Array.Empty<(float, float)>()); }
                _hadNeeded = needed.Length > 0;
                foreach (var n in needed) _dropperKillMs.TryAdd(n, p.World.NowMs);
                var starved = needed.Where(n => p.World.NowMs - _dropperKillMs[n] > 600_000).ToArray();
                if (starved.Length > 0)
                {
                    // Steer toward the NEAREST starved species' ground (user: farm what's close first — no
                    // dying on long marches to the far Rarab camps while cup/robe are still missing; when the
                    // tail is the LAST need, Rarab is the only starved species and the steering aims there).
                    // The trek hunts normally en route — steering, not a forced march.
                    var byDist = starved
                        .OrderBy(n => Game.SpawnClusters.ForNames(ZoneNow(), new[] { n })
                            .Select(c => { float dx = c.x - p.World.X, dz = c.z - p.World.Z; return dx * dx + dz * dz; })
                            .DefaultIfEmpty(float.MaxValue).Min())
                        .ToArray();
                    // ALTERNATE when the same species re-fires without producing a kill: nocturnal Bogy is
                    // empty by day, stays starved, and being nearest it won EVERY steer — the far Rarab
                    // ground never got a turn (livelock). A fruitless steer yields the next pick to #2.
                    var target = byDist[0] == _lastSteerTarget && byDist.Length > 1 ? byDist[1] : byDist[0];
                    _lastSteerTarget = target;
                    Log($"10 min without a kill of: {string.Join("/", starved)} — steering to {target} ground");
                    roam.SetPriority(Game.SpawnClusters.ForNames(ZoneNow(), new[] { target }));
                    roam.ForceTrek();
                    foreach (var n in starved) _dropperKillMs[n] = p.World.NowMs;   // re-arm all for the travel time
                }
            }

            // Party duty (e.g. a mob is beating on the healer — peel it) outranks our own hunting.
            if (cfg.PartyDuty is { } duty && await duty(ct)) { _lastHpp = p.World.Hpp; continue; }

            // Aggro defense: HP dropped while not engaged = something jumped us. You can't run from FFXI
            // hate — fight the ACTUAL attacker (0x028 tracking), falling back to the nearest mob in range.
            bool underAttack = !combat.Dead && p.World.ServerStatus != 1 && p.World.Hpp > 0
                && (p.World.Hpp < _lastHpp || p.AttackersOn(p.World.MyId) > 0);
            _lastHpp = p.World.Hpp;
            long now = p.World.NowMs;

            // NEVER choose a new fight below the rest line. Rest was post-kill only, so an interrupted or
            // skipped rest let the loop chain pulls at 37% HP — a lv4 THF picked a con-2 at 37% and died.
            if (!underAttack && !combat.Dead && p.World.Hpp > 0 && p.World.Hpp < cfg.RestHpTrigger)
            {
                await RestSafely(ct);
                continue;
            }
            // POST-REVIVE WEAKNESS hold (~5 min of gutted stats): re-engaging right after a home-point revive
            // lost a con-3 fight the same character wins healthy (mob at 27%). Sit it out; aggro still fights.
            if (!underAttack && p.World.RevivedMs > 0 && now - p.World.RevivedMs < 300_000)
            {
                if (now - _lastWeakLogMs > 30_000) { _lastWeakLogMs = now; Log($"weakened after revive — holding {(300_000 - (now - p.World.RevivedMs)) / 1000}s more (no pulls)"); }
                // Hold SOMEWHERE SAFE: a bare in-place wait left the weakened THF standing in a goblin's
                // sight line — RestSafely steps away from wanderers and sits (regen while we wait it out).
                // Only if there's something to regen: at full vitals it looped rest/wake ~10k times live.
                if (p.World.Hpp < cfg.RestHpTarget || (cfg.RestMpPct > 0 && p.World.Mpp < cfg.RestMpPct)) await RestSafely(ct);
                await Task.Delay(3000, ct);
                continue;
            }

            Entity? mob;
            bool preferred = false;
            bool NotObject(Entity e) => CombatRoutines.NotObject(e);   // shared: never fight chests/books/???s
            bool LeashOk(Entity e) => cfg.PullLeash <= 0f || p.DistanceTo(e.X, e.Z) <= cfg.PullLeash;
            bool BaseOk(Entity e) => e.IsMob && e.Hpp > 0 && e.Y < 100 && NotObject(e)
                && !cfg.SkipMobNames.Any(n => e.Name.Contains(n, StringComparison.OrdinalIgnoreCase))
                && !_skip.Contains(e.Id)
                && (now - e.LastSeenMs) < 20000;
            if (underAttack)
            {
                // The fallback had NO object filter and the WAR "fought" a Treasure_Casket for 15 seconds
                // while the real attacker killed the healer.
                // Even under attack, NEVER engage a sleep-lock mob (Saplin/Mandragora) — fighting back just
                // feeds the sleep→death; a THF died engaging an aggro'd Strolling_Saplin here. Excluding it
                // lets the loop pick a real attacker or fall through to disengage/rest.
                bool NotSleepLock(Entity e) => !cfg.SkipMobNames.Any(n => e.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
                mob = p.Nearest(e => e.IsMob && e.Hpp > 0 && e.Y < 100 && NotSleepLock(e)
                        && p.World.Attackers.TryGetValue(e.Id, out var a) && a.target == p.World.MyId && now - a.ms <= 6000)
                    ?? p.Nearest(e => e.IsMob && e.Hpp > 0 && e.Y < 100 && NotObject(e) && NotSleepLock(e)
                        && (now - e.LastSeenMs) < 10000 && p.DistanceTo(e.X, e.Z) <= 16f);
            }
            else
            {
                // Priority targets first (item droppers — the point of a farm). They may bypass the NM name
                // skip (the brain explicitly asked for this mob) but never the too-tough con gate.
                mob = cfg.PreferTarget is { } pref
                    ? p.Nearest(e => BaseOk(e) && pref(e) && LeashOk(e) && p.DistanceTo(e.X, e.Z) <= 50f)
                    : null;
                preferred = mob is not null;
                // PreferredOnly (farm endgame, user call): ONLY droppers are worth engaging — but when NO
                // needed species is farmable right now (nocturnal-only need during day), ordinary hunting
                // resumes rather than idling. A dropper march is COMMITTED either way.
                if (!preferred && ((cfg.PreferredOnly && neededNow.Length > 0) || roam.OnDropperTrek)) { mob = null; }
                else
                // Con is the SOLE arbiter (BaseOk's con band) — the Quadav and NmNames name-blocks that used
                // to sit here violated the hard rule and are gone (a lost fight recovers via the death path).
                mob ??= p.Nearest(e => BaseOk(e) && LeashOk(e) && p.DistanceTo(e.X, e.Z) <= 50f);
            }

            if (mob is null)
            {
                // Re-judge skipped mobs only PERIODICALLY — clearing on every empty pick instantly resurrected
                // the just-skipped con-5 dropper and the loop thrashed pick→skip→clear→pick forever.
                if (p.World.NowMs - _lastSkipClearMs > 120_000) { _skip.Clear(); _lastSkipClearMs = p.World.NowMs; }
                // STICK TOGETHER while roaming (user rule: never pull OR ADVANCE until the party is good).
                // Without this gate the WAR out-roamed the casting/resting WHM hop by hop until the healer
                // fell out of view range (the dist=166 stale-tether). Same gate as pulls: it walks toward
                // the partner and waits out rests; on timeout it forces a rally and we re-loop.
                // HALT any in-flight trek when the gate fails — a committed long leg otherwise keeps walking
                // through the gate-check latency and leaves a casting healer behind (user-observed).
                if (cfg.BeforePull is { } roamGate && !await roamGate(ct)) { if (nav.IsMoving) nav.Stop(); continue; }
                await roam.StepAsync(ct);   // free roam: steered by con, never parked at a camp
                await Task.Delay(1000, ct);
                continue;
            }
            nav.Stop();

            int fightCon = 4;   // aggro/unknown mobs are treated as a real threat (EvenMatch) for ability decisions
            if (underAttack)
            {
                Log($"UNDER ATTACK (HP {p.World.Hpp}%) — fighting 0x{mob.Id:X} '{mob.Name}' regardless of con");
            }
            else
            {
                fightCon = await roam.ConsiderCached(mob.Id, ct);
                int floor = preferred ? 0 : cfg.ConMin;   // droppers are worth killing below the exp band
                // NOTE: no unarmed con cap — the band is the SAME for everyone (user rule). A con<=1 "prey"
                // cap is an EMPTY band for a fresh char (no mob can be below level 1, so a lvl-1 sees
                // everything at con>=3) — three mages stood targetless a whole session under it. Unhittable
                // INDIVIDUALS are handled downstream (zero-damage kite -> unkillable give-up -> entity skip).
                if (fightCon < floor || fightCon > cfg.ConMax)
                {
                    Log($"skip 0x{mob.Id:X} '{mob.Name}' con={fightCon} (want {floor}-{cfg.ConMax}{(preferred ? ", dropper" : "")})");
                    _skip.Add(mob.Id);
                    // Sustained TOO-WEAK cons = we've out-levelled this zone -> advance a leg (path mode only).
                    if (!preferred && fightCon >= 0 && fightCon < cfg.ConMin)
                    {
                        if (++_tooWeak >= 6 && hunter != null && hunter.ForceAdvance()) { _tooWeak = 0; Log($"mobs too weak — advancing toward {hunter.TargetZoneName()}"); }
                    }
                    else _tooWeak = 0;
                    continue;
                }
                _tooWeak = 0;
                // Clean-pull gate: don't start a fight inside a known con>=5 mob's aggro envelope (add-cascade).
                // Skip the target too (con-based: its NEIGHBOR is too tough — not a failure blacklist; cleared
                // on level-up/empty-view) so Nearest moves on instead of re-picking the same dirty mob forever.
                if (!await roam.CleanPull(mob, cfg.GuardSpot?.Invoke(), ct, cfg.CleanPullNeighborCon)) { _skip.Add(mob.Id); await roam.StepAsync(ct); await Task.Delay(1500, ct); continue; }
                Log($"target 0x{mob.Id:X} '{mob.Name}' con={fightCon}{(preferred ? " [dropper]" : "")} hpp={mob.Hpp} dist={p.DistanceTo(mob.X, mob.Z):F0}");
            }

            // Start every fight near-full: rest first when low (nothing's on us yet; Rest bails if an add is).
            if (!underAttack && (p.World.Hpp < 70 || (cfg.RestMpPct > 0 && p.World.Mpp < cfg.RestMpPct)))
            { nav.Stop(); await combat.Rest(90, cfg.RestMpPct, null, ct); }

            // Party ready-check (tether): never pull until the healer is close + topped. Skipped when we're
            // already being hit — never stand still while beaten.
            if (!underAttack && cfg.BeforePull is { } gate && !await gate(ct)) continue;

            // Ranged opener (WHM Dia) — no-op for melee.
            if (!underAttack) await cfg.Pull(mob.Id, ct);

            // Close to melee. Bail if we die en route or lose the target.
            nav.Follow(mob.Id);
            for (int i = 0; i < 80 && !ct.IsCancellationRequested; i++)
            {
                if (mob.Hpp == 0 || combat.Dead || (p.World.NowMs - mob.LastSeenMs) > 20000 || p.DistanceTo(mob.X, mob.Z) <= 2.0f) break;
                await Task.Delay(250, ct);
            }
            if (combat.Dead) continue;
            nav.Stop();
            if (mob.Hpp == 0 || (p.World.NowMs - mob.LastSeenMs) > 20000) { Log("target lost during approach"); continue; }
            float reach = p.DistanceTo(mob.X, mob.Z);
            if (reach > 3.5f) { Log($"couldn't close ({reach:F0}y) — skipping 0x{mob.Id:X}"); _skip.Add(mob.Id); continue; }

            // Fight — the ONE shared kill loop (WS + abilities + ledge handling). Always to the death:
            // you can't outrun hate, and a dropper's roll needs a finished kill.
            Log($"walked {_walkedSinceFight:F0}y since last fight");
            _walkedSinceFight = 0;
            // Droppers fight at con>=3 for ABILITY decisions: Bull_Dhalmel is an NM that cons 2 at WAR 23 but
            // hits far above it — the con-gated kit (Berserk/Mighty Strikes) sat unused while the WAR bled out
            // 46%->0 against its 89%->73%. A farm target is by definition a fight worth the full kit.
            bool killed = await KillRoutine.Fight(combat, p, nav, gear, mob, preferred ? Math.Max(fightCon, 3) : fightCon, killHooks, breakOffHpp: 0, ct);

            if (killed) { _lastKillMs = p.World.NowMs; _dryTreks = 0; }
            else if (mob.Hpp > 0)
            {
                // Fight broke off with the mob alive (KillRoutine's unkillable give-up, or death). Skip THIS
                // entity for a while — entity-id based and periodically cleared, NOT a name list (con rule).
                Log($"fight broke off with '{mob.Name}' alive — skipping 0x{mob.Id:X} for now");
                _skip.Add(mob.Id);
            }
            if (killed)
                foreach (var n in cfg.SeedNames.Where(n => mob.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
                {
                    _dropperKillMs[n] = p.World.NowMs;   // THIS species is producing here — only its own clock resets
                    // Still needed? CAMP this ground through the repop cycle instead of self-evicting.
                    if ((cfg.NeededDroppers?.Invoke() ?? cfg.SeedNames).Contains(n))
                    {
                        _campUntilMs = p.World.NowMs + 600_000;
                        Log($"'{n}' ground is producing — camping for repops (~10 min)");
                    }
                }
            if (killed && cfg.OnKill is { } onKill) await onKill(ct);
            if (killed && !combat.Dead) await cfg.PostKillHeal(ct);
            if (killed && !combat.Dead && (p.World.Hpp < cfg.RestHpTrigger || (cfg.RestMpPct > 0 && p.World.Mpp < cfg.RestMpPct)))
                await RestSafely(ct);
            // Range the zone instead of camping: relocate to fresh ground every few kills.
            if (killed && (++_kills % 4) == 0) { Log("roaming to fresh hunting ground"); await roam.StepAsync(ct); }
            await Task.Delay(1000, ct);
        }
    }

    // Occupied main-inventory (container 0) slots, gil-slot 0 excluded — drives the "bag full, go sell" trip.
    int CountItems() => inv.CountSlots();
}
