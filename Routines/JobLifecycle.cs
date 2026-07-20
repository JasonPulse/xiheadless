namespace XiHeadless.Routines;

/// The ONE full-lifecycle arc every job brain composes (user, 2026-07-05). A character is created as a
/// FRESH LEVEL-1 WAR (see Net/LobbyClient.CreateCharBody); this routine drives the whole path from that
/// fresh start so a brain is nothing but config:
///
///   ADVANCED jobs (PLD/DRK/NIN/… — need an unlock quest):
///     1. Level the designated SUBJOB (a basic-6 job) from 1 to 30 as MAIN. (30 satisfies BOTH the
///        character-level-30 unlock-quest gate AND yields a full sub usable to main 60.)
///     2. Complete the unlock quest (Prereqs+Unlock via QuestRunner). BLOCKED unlocks fail GRACEFULLY —
///        we log and HOLD by leveling the sub open-ended instead of crash-looping the process.
///     3. Switch MAIN to the advanced job with the basic job as SUB (packet-set), then SEESAW main/sub
///        (JobLeveling keeps the sub >= ceil(main/2)).
///
///   BASIC jobs (WAR/MNK/WHM/…): skip straight to the seesaw — level the main from 1 with the chosen sub
///   kept at half. (No unlock step.)
///
/// Every phase reuses the existing engine — JobLeveling (seesaw), LevelGrind (grind loop), QuestRunner
/// (quests), JobRoutines.ChangeJobViaMogHouse — and folds in the hard-won BlmBrain/PldBrain patterns so
/// EVERY brain gets them for free:
///   * BABY PHASE (< BabyUntil): a level-1 (25 HP) dies before landing a kill, and a lv1 mob cons 4 (Even
///     Match) so a <4 cap skips forever — so the baby engages con 0-4 with short hops.
///   * LEVEL-GATED HUNT ZONES: a fresh job death-loops in a too-tough zone (a lv9 BLM dies NET-ZERO in
///     Tahrongi — its Cure I can't out-heal the lv12-18 mobs). HuntZonePlan maps level -> fixed zone
///     (Windurst default: ~1-11 West Sarutabaruta, ~12-14 East Sarutabaruta, ~15-17 Tahrongi, 18+ nation
///     path), advancing the band as the char levels. West Sarutabaruta is entered from the SAFE (Port
///     Windurst) gate, not the goblin-belt east gate (LevelGrind.TravelVia).
///   * SAFE-JOB RECOVERY: home-point crystal relocation is unreliable and a baby can't cross hostile
///     ground, so a post-death return travels AS A STRONG JOB (the 30 sub, once it exists), switches back
///     at the Mog House, and re-enters the CURRENTLY-ACTIVE gated zone (RecoverToHuntZone, wired to
///     LevelGrind's RecoveryTravel hook). Recovery and the grind's FixedZone read the SAME HuntZonePlan, or
///     LevelGrind loops "out of <zone> — delegating return" forever (that exact bug bit BLM).
///   * Mage subs self-heal in-fight via the brain's own EmergencyHeal (kept in the per-job GrindCfg).
public sealed class JobLifecycle(
    IPerception p, INavigation nav, ICombat combat, IZoning zoning, IGear gear,
    IAuctionHouse ah, IDelivery delivery, IInventory inv, IShop shop, IJobChange jobs,
    IQuests? quests, ITradeNpc? trade, IEvents? events, JobLifecycle.Config cfg, ILifecycle? lifecycle = null,
    IChat? chat = null, IMagic? magic = null, IParty? party = null)
{
    public sealed class Config
    {
        public byte MainJob;                  // the job this brain ultimately mains (WAR, PLD, BLM, …)
        public byte SubJob;                   // the basic-6 sub: advanced prereq-to-30 AND seesaw partner
        public bool Advanced;                 // true = needs an unlock quest (phases 1+2 run first)
        public byte MainTarget;               // stop level for the main (0 = open-ended; the user steers)
        public IReadOnlyList<QuestStep>? UnlockSteps;   // Prereqs+Unlock concatenated (advanced only)

        // Optional: UnlockSteps grouped by their constituent quest + done-bit, in chain order. When set,
        // TryUnlock DROPS the steps of any quest already COMPLETE in the quest-log (WorldState.QuestLog) so a
        // death mid-chain RESUMES instead of replaying from step 0 (QuestRunner has no quest-state awareness).
        // Empty = run UnlockSteps whole. Generic: any nation's chain supplies its (questId, donePort) groups.
        public IReadOnlyList<UnlockQuest> UnlockChain = System.Array.Empty<UnlockQuest>();

        // Opt-in: before the unlock quest, ASK the central GM bot to grant the job (a /tell to the GM char).
        // If it lands, the normal Mog House change applies it and we skip the whole quest trek; if not, we
        // fall through to the quest. Requires a chat capability passed to JobLifecycle. Sanctioned only for
        // job unlocks (the GM bot grants nothing else). Off by default — a brain opts in.
        public bool UseGmGrant;

        // Per-job base grind config (gear/skills/abilities/con/rest/funding). Called with the job being
        // leveled so a brain can flip the weapon-skill/gear between its main and its sub. JobLifecycle only
        // OVERLAYS the nursery/baby/recovery/Done fields on top of whatever the brain returns.
        public Func<byte, LevelGrind.Config> GrindCfgFor = null!;

        // ---- Unlock-trek helpers (advanced only; all optional) ----
        public (ushort item, int qty)[] UnlockStockItems = System.Array.Empty<(ushort, int)>();  // AH-buy before the trek
        public bool StealthUnlock;            // also buy + apply Sneak/Invis consumables for the trek
        public string? UnlockTrekZone;        // stealth-trek to this zone before running the quest steps
        public ushort UnlockTrekZoneId;       // skip the trek if already here (0 = always attempt)
        public Func<CancellationToken, Task>? BeforeUnlock;   // escape hatch for anything the above can't express
        // Central hub to home-point at before an unlock quest (the leveling-guide standard: a lv30 runs to
        // Jeuno and home-points there — central, short clean hops to all nations). Default: Lower Jeuno.
        public string HubZone = "Lower_Jeuno";
        public ushort HubZoneId = 245;

        // ---- Level-gated hunt zones + recovery (Windurst-nation defaults) ----
        public Nation HomeNation = Nation.Windurst;
        public string HomeCity = "Windurst_Woods";
        public ushort HomeCityId = 241;
        // LEVEL -> fixed hunt zone (null result = the brain's nation path / HuntZones takes over). The SINGLE
        // source of truth for BOTH the grind's FixedZone AND the recovery destination — hardcoding one while
        // the other advances is the "delegating return" death-loop. A fresh job needs low->mid->high gating
        // (a lv9 BLM dies net-zero in Tahrongi; 117 is too tough below ~12-13).
        public Func<int, (string zone, ushort id)?> HuntZonePlan = DefaultWindurstPlan;
        public ushort SafeGateZoneId = 115;     // this zone must be ENTERED from the safe gate...
        public ushort SafeGateVia = 240;        // ...Port Windurst (bee ground), not the east goblin belt.
        public byte BabyUntil = 6;              // below this: con 0-4 + short hops (a lv1 mob cons 4 = Even Match)

        public string Tag = "job";

        // Per-nation NURSERY plans (a char is born in a RANDOM nation — fleet provisioning — and the engine
        // detects it at runtime from 0x061 and swaps these in; see ApplyDetectedNation). A Bastok-born baby
        // given the Windurst plan death-marched across Pashhow at lvl 1 (live pre-flight, Griasha).
        public static (string zone, ushort id)? DefaultSandoriaPlan(int lvl) =>
            lvl < 12 ? ("West_Ronfaure", (ushort)100)
          : lvl < 19 ? ("La_Theine_Plateau", (ushort)102)
          : lvl < 25 ? ("Valkurm_Dunes", (ushort)103)
          : null;
        public static (string zone, ushort id)? DefaultBastokPlan(int lvl) =>
            lvl < 12 ? ("South_Gustaberg", (ushort)107)
          : lvl < 19 ? ("Konschtat_Highlands", (ushort)108)
          : lvl < 25 ? ("Pashhow_Marshlands", (ushort)109)
          : null;

        // Windurst nursery progression: ~1-11 West Sarutabaruta (baby + main), ~12-14 East Sarutabaruta,
        // ~15-17 Tahrongi Canyon, 18+ nation path (HuntZones routes Tahrongi/Buburimu, with ForceAdvance).
        public static (string zone, ushort id)? DefaultWindurstPlan(int lvl) =>
            lvl < 12 ? ("West_Sarutabaruta", (ushort)115)
          : lvl < 15 ? ("East_Sarutabaruta", (ushort)116)
          : lvl < 19 ? ("Tahrongi_Canyon", (ushort)117)      // LIVE EVIDENCE 2026-07-05/06: THF gained a full level at 18 in Tahrongi on con-3 bats, but at 19 Tahrongi's common mobs con 0 (too weak -> net-zero starve). Meriphataud at 18 was a death-wall; enter it at 19 (a level stronger + con-skipping avoids the worst). So: Tahrongi through 18, Meriphataud from 19.
          : lvl < 25 ? ("Meriphataud_Mountains", (ushort)119)  // ~18-30 mobs; entered at 19 (18 was a death-wall; 19+ survivable with con-skipping)
          : null;
    }

    /// One quest in an UnlockChain: its quest id + the COMPLETE-bitmap port to read its done-bit from, and
    /// the QuestSteps that perform it. TryUnlock skips Steps when QuestComplete(QuestId, DonePort) is set.
    public readonly record struct UnlockQuest(int QuestId, ushort DonePort, IReadOnlyList<QuestStep> Steps);

    static string JN(byte j) => Game.PartyRoles.NameOf(j);

    void Log(string m) => XiHeadless.Log.Auto($"[{cfg.Tag}] {m}");
    int LevelOf(byte job) => p.World.JobLevels.TryGetValue(job, out var l) ? l : 0;
    byte SubFor(byte job) => job == cfg.MainJob ? cfg.SubJob : (byte)0;   // sub set only when leveling the main

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(4000, ct);
        await ApplyDetectedNation(ct);
        var seesaw = new JobLeveling(p, jobs, zoning);
        Log($"lifecycle start: {(cfg.Advanced ? "ADVANCED" : "basic")} main={JN(cfg.MainJob)} sub={JN(cfg.SubJob)} " +
            $"(now {JN(p.World.MainJob)} {p.World.MainJobLevel} / levels {JN(cfg.MainJob)}={LevelOf(cfg.MainJob)} {JN(cfg.SubJob)}={LevelOf(cfg.SubJob)})");

        if (cfg.Advanced)
        {
            // UNLOCK VIA THE GM (user rule 2026-07-14: "you don't need the unlock quest — that's the point
            // of the GM character"). The advanced job plays FROM LEVEL 1 (a lvl-1 BST has Charm; the seesaw
            // levels the sub later like any other pairing). Probe by attempting the job change: it succeeds
            // iff already unlocked; on refusal, ask the GM (retry-until-acked) and change again. If the GM
            // never acks (offline), fall back to leveling the SUB this session — no wasted login, and the
            // next session re-probes. (The old quest-chain phases — sub-to-30 gate + QuestRunner unlock —
            // are superseded; UnlockSteps configs remain unused by this path.)
            if (!await JobRoutines.ChangeJobViaMogHouse(jobs, zoning, cfg.MainJob, cfg.SubJob, cfg.HomeCity, ct))
            {
                bool granted = chat is not null
                    && await GmGrant.RequestJob(p, chat, Game.PartyRoles.NameOf(cfg.MainJob), cfg.Tag, ct)
                    && await JobRoutines.ChangeJobViaMogHouse(jobs, zoning, cfg.MainJob, cfg.SubJob, cfg.HomeCity, ct);
                if (!granted)
                {
                    Log($"{JN(cfg.MainJob)} locked and no GM ack — leveling {JN(cfg.SubJob)} this session (re-probe next login)");
                    await EnsureMain(cfg.SubJob, 0, ct);
                    await RunGrindStint(cfg.SubJob, () => false, ct);
                    return;
                }
            }
            Log($"{JN(cfg.MainJob)} unlocked — playing the MAIN from level {LevelOf(cfg.MainJob)} ({JN(cfg.SubJob)} sub at {LevelOf(cfg.SubJob)})");
        }

        // PHASE 3 — level the main. With a seesaw partner (SubJob != 0) JobLeveling owns the switching
        // policy and keeps the sub at ceil(main/2); with no partner (SubJob == 0) it's a single-job grind to
        // the target. RunGrindStint runs each stint with the level-gated-zone/baby/recovery overlay.
        if (cfg.SubJob == 0)
        {
            await EnsureMain(cfg.MainJob, 0, ct);
            await RunGrindStint(cfg.MainJob, () => cfg.MainTarget > 0 && LevelOf(cfg.MainJob) >= cfg.MainTarget, ct);
        }
        else
        {
            await seesaw.RunAsync(new JobLeveling.Config
            {
                MainJob = cfg.MainJob, SubJob = cfg.SubJob, MainTarget = cfg.MainTarget,
                HomeCity = cfg.HomeCity, Tag = cfg.Tag,
                RunGrind = async (job, c) =>
                {
                    int startMain = LevelOf(cfg.MainJob);
                    await RunGrindStint(job, () => job == cfg.SubJob
                        // sub stint: grind until the sub reaches half of the main
                        ? LevelOf(cfg.SubJob) >= JobLeveling.SubNeededFor(LevelOf(cfg.MainJob))
                        // main stint: stop at the target, or when the sub falls behind, or after +5 levels (re-decide)
                        : (cfg.MainTarget > 0 && LevelOf(cfg.MainJob) >= cfg.MainTarget)
                          || LevelOf(cfg.SubJob) < JobLeveling.SubNeededFor(LevelOf(cfg.MainJob))
                          || LevelOf(cfg.MainJob) >= startMain + 5, c);
                },
            }, ct);
        }

        if (cfg.MainTarget > 0 && LevelOf(cfg.MainJob) >= cfg.MainTarget)
        {
            Log($"goal reached: {JN(cfg.MainJob)} {LevelOf(cfg.MainJob)}");
            lifecycle?.Logout();
        }
    }

    // Fleet chars are born in a RANDOM nation, but every brain's config defaults to Windurst. Detect the REAL
    // nation from 0x061 (parsed shortly after zone-in) and swap the nation-dependent engine config: nursery
    // plan, home city (Mog House/recovery), safe-gate (Windurst-only), and the HuntZones path. Only swaps
    // when the brain left the DEFAULT Windurst plan in place — a brain that overrides HuntZonePlan is
    // presumed deliberate. (Live pre-flight: Bastok-born Griasha death-marched toward West Saruta at lvl 1.)
    async Task ApplyDetectedNation(CancellationToken ct)
    {
        for (int t = 0; t < 20000 && p.World.NationId == 255 && !ct.IsCancellationRequested; t += 500)
            await Task.Delay(500, ct);   // 0x061 arrives a few send-cycles after zone-in
        if (p.World.NationId is not (0 or 1) || cfg.HuntZonePlan != (Func<int, (string, ushort)?>)Config.DefaultWindurstPlan)
            return;   // Windurst (2) or unknown -> defaults stand; custom plan -> brain's choice stands

        var nation = (Nation)p.World.NationId;
        if (nation == Nation.SanDoria)
        { cfg.HomeCity = "Southern_San_dOria"; cfg.HomeCityId = 230; cfg.HuntZonePlan = Config.DefaultSandoriaPlan; }
        else
        { cfg.HomeCity = "Bastok_Mines"; cfg.HomeCityId = 234; cfg.HuntZonePlan = Config.DefaultBastokPlan; }
        cfg.SafeGateZoneId = 0; cfg.SafeGateVia = 0;   // the safe-gate rule is Windurst-specific (east goblin belt)
        _detectedNation = nation;                      // RunGrindStint also overlays the per-job GrindCfg
        Log($"nation detected: {nation} — nursery/home-city swapped from the Windurst defaults (home={cfg.HomeCity})");
    }

    Nation? _detectedNation;   // non-null when ApplyDetectedNation swapped away from the Windurst defaults

    // One grind stint for `job` (which must already be MAIN — the caller ensures it). While HuntZonePlan
    // returns a gated zone we fix to it (+ baby con band below BabyUntil) and own the post-death return; when
    // it returns null we fall through to the brain's nation-path config. Loops internally across the band
    // boundaries so a single stint carries a baby up through the whole nursery to its `done`.
    async Task RunGrindStint(byte job, Func<bool> done, CancellationToken ct)
    {
        // PROACTIVE home-point set: a stint begins right after the seesaw's job change (JobLeveling) or
        // EnsureMain — both route through the HomeCity Mog House, so on a FRESH run the char is standing in
        // the HomeCity, alive, here. Set the revive point NOW, before the first death — otherwise a stale
        // distant default (e.g. BLM's farm-era Mhaura) sends every revive across a hostile-zone gauntlet, and
        // the home point only got corrected AFTER a death dragged us back to Windurst (the re-trek death-loop).
        // Self-gates on being in the HomeCity + a once-only flag, so field stints and later runs are no-ops.
        await EnsureHomePointAtHomeCity(ct);
        while (!ct.IsCancellationRequested && !done())
        {
            var g = cfg.GrindCfgFor(job);
            // SKILL-UP DROPBACK (user: easy prey must EXIST — con is relative, so an at-level zone may con
            // nothing low and a skill-lagged bot would roam dry all session). The zone plan follows the
            // EFFECTIVE level — what the lagging weapon supports (skill/2; trigger mirrors LevelGrind's
            // skill-up mode at skill < level*2) — stepping back a tier or two to where easy prey is the
            // local population, and advancing again automatically as the skill climbs.
            byte EffectiveLevel()
            {
                byte lvl = p.World.MainJobLevel;
                if (lvl < 5) return lvl;
                int wep = gear.SkillLevel(g.WepSkillForLevel(lvl));
                return wep < lvl * 2 ? (byte)Math.Clamp(wep / 2, 1, lvl) : lvl;
            }
            byte effLvl = EffectiveLevel();
            if (effLvl < p.World.MainJobLevel)
                Log($"skill-up dropback: hunting at effective lvl {effLvl} (weapon skill lags lvl {p.World.MainJobLevel})");
            var plan = cfg.HuntZonePlan(effLvl);
            if (_detectedNation is { } dn)   // char born in a non-Windurst nation: overlay the brain's Windurst grind defaults
            {
                g.HomeNation = dn;
                g.AhZone = cfg.HomeCity;     // both swapped home cities have the AH (misc 0x200 verified)
            }
            // COMBAT KIT injection: most fleet brains are gear+quest configs with NO UseAbilities/EmergencyHeal
            // — they pure-melee'd every fight (live: a lvl-1 BRD never sang, lost to a con-2 rabbit 17x/h).
            // Inject the generic per-job kit when the brain left the sentinels in place; curated brains
            // (WarBrain/BlmBrain) keep their own rotations untouched.
            JobKits.Apply(g, job, combat, magic, p, cfg.Tag, inv);
            // (The 2026-07-12 role-aware con caps were REVERTED: the death-loopers' real defect was fighting
            // with NO JOB KIT — a lvl-1 BRD that actually sings kills far above these caps (user). The kit
            // injection below is the fix; con bands stay as the brains tuned them.)
            if (plan is (string zone, ushort id))
            {
                g.FixedZone = zone; g.FixedZoneId = id;
                g.TravelVia = id == cfg.SafeGateZoneId ? cfg.SafeGateVia : (ushort)0;
                g.RecoveryTravel = c => RecoverToHuntZone(job, c);
                if (p.World.MainJobLevel < cfg.BabyUntil) { g.ConMin = 0; g.ConMax = 4; g.RoamHop = 25f; }
                // Exit when the gated band should ADVANCE (level crossed into another zone) or `done` fires.
                g.Done = () => done() || cfg.HuntZonePlan(EffectiveLevel())?.id != id;   // advance as skill catches up
            }
            else g.Done = done;   // nation path (HuntZones) selects zones itself
            // PARTY DAYS (user rule 2026-07-14: bots try to PARTY above level 10). On a Party-plan day a
            // capable bot runs the fleet day instead of the solo grind: travel to the hunt zone, form via
            // zone shout (PartyFinder), play its ROLE at camp (PartyGrind — stations, puller doctrine, the
            // one shared KillRoutine), group-safe logout (FleetSchedule). Solo/Upkeep days = the grind below.
            if (party is not null && chat is not null && magic is not null && lifecycle is not null
                && p.World.MainJobLevel > 10
                && SessionPlan.ForToday(p.World.MyId).Mode == SessionPlan.DayMode.Party)
            {
                Log($"PARTY day (lvl {p.World.MainJobLevel}) — grouping up in the hunt zone");
                var pg = new PartyGrind(p, combat, magic, nav, gear, chat, g, cfg.Tag);
                // The plan handed to FleetDay carries the EFFECTIVE session end (BotHost's 2-6h clamp), so
                // the ENDAT group-convergence protocol aims at the real logout, not the raw seeded day-end.
                var basePlan = SessionPlan.ForToday(p.World.MyId);
                var effEnd = BotHost.SessionEndUtc < basePlan.EndUtc ? BotHost.SessionEndUtc : basePlan.EndUtc;
                var effPlan = new SessionPlan.Plan(basePlan.Mode, basePlan.StartUtc, effEnd);
                await FleetDay.Run(p, combat, party, chat, magic, nav, lifecycle, new FleetDay.Hooks
                {
                    GoToHuntZone = async c => { if (plan is (string pz, ushort pid) && zoning.CurrentZone != pid) await zoning.GoTo(pz, c); },
                    SoloGrind = c => new LevelGrind(p, nav, combat, zoning, gear, ah, delivery, inv, shop, g).RunAsync(c),
                    PartyGrind = (pp, c) => pg.Beat(pp, c),
                    Tag = cfg.Tag,
                }, effPlan, ct);
                continue;   // the fleet day ends via session logout / cancellation
            }
            await new LevelGrind(p, nav, combat, zoning, gear, ah, delivery, inv, shop, g).RunAsync(ct);
            if (plan is null) break;   // nation-path LevelGrind only returns when done() (or cancelled)
        }
    }

    async Task EnsureMain(byte job, byte sub, CancellationToken ct)
    {
        if (p.World.MainJob == job && (sub == 0 || p.World.SubJob == sub)) return;
        Log($"setting {JN(job)}/{(sub == 0 ? "--" : JN(sub))} (was {JN(p.World.MainJob)}/{JN(p.World.SubJob)})");
        if (!await JobRoutines.ChangeJobViaMogHouse(jobs, zoning, job, sub, cfg.HomeCity, ct))
            Log($"could not set {JN(job)} — will retry on the next attempt");
    }

    // Probe the advanced job change via the Mog House (a street ChangeJob ALWAYS fails on this server, so a
    // raw probe would false-report "locked"). On a real lock: stock/trek, run the quest steps, retry the
    // change. Returns true once the char is actually the advanced main.
    async Task<bool> TryUnlock(CancellationToken ct)
    {
        // The unlock quest operates out of the QUEST nation, NOT the far home city. A Windurst char doing the
        // San d'Oria PLD quest must probe/change jobs, home-point, and revive in San d'Oria — never trek
        // cross-continent to Windurst (and die on the way). Route Mog House ops to the quest nation when set;
        // same-nation unlocks (UnlockTrekZone null) fall back to HomeCity unchanged.
        string unlockCity = cfg.UnlockTrekZone ?? cfg.HomeCity;

        // GM-grant FAST PATH (opt-in): ask the central GM bot to unlock the job, then let the normal Mog House
        // change apply it — skips the whole cross-continent quest trek when it lands; falls through if it
        // doesn't. RequestJob RETRIES until the GM's reply acks it (the GM bot can log in minutes after us).
        if (cfg.UseGmGrant && chat is not null && !await GmGrant.RequestJob(p, chat, JN(cfg.MainJob), cfg.Tag, ct))
            Log($"GM grant unacked — {JN(cfg.MainJob)} unlock falls back to the quest");

        if (await JobRoutines.ChangeJobViaMogHouse(jobs, zoning, cfg.MainJob, cfg.SubJob, unlockCity, ct))
        {
            if (cfg.UseGmGrant) Log($"{JN(cfg.MainJob)} UNLOCKED (GM grant or already had it) — skipping the quest");
            return true;
        }
        Log($"{JN(cfg.MainJob)} locked — attempting the unlock quest");

        // Stock quest items + stealth at the home AH before the trek (fragile 30-40 zones one-shot a death
        // back to Windurst, losing the whole trek — the PLD lesson).
        if ((cfg.UnlockStockItems.Length > 0 || cfg.StealthUnlock)
            && (Game.Zonelines.HasAuctionHouse(zoning.CurrentZone) || zoning.CurrentZone == cfg.HomeCityId))
        {
            var keep = cfg.GrindCfgFor(cfg.SubJob).Keep;
            foreach (var (item, qty) in cfg.UnlockStockItems)
                await ShopRoutines.BuyAtLeast(ah, p, inv, item, qty, keep, ShopRoutines.NoFree, ct);
            if (cfg.StealthUnlock)
                await StealthRoutines.EnsureStock(ah, p, inv, 12, keep, ShopRoutines.NoFree, ct);
        }

        // Stealth-trek to the quest start (apply STANDING STILL — item use is interrupted by movement; a
        // background maintainer tops up best-effort).
        if (cfg.UnlockTrekZone is { } trekZone && zoning.CurrentZone != cfg.UnlockTrekZoneId && StealthRoutines.HasPowders(inv))
        {
            Log($"stealth trek to {trekZone} (applying before moving)");
            await StealthRoutines.StealthCross(zoning, nav, inv, p, trekZone, ct);
        }

        // HOME POINT at the quest nation (user rule): set it EVERY unlock run so every death mid-quest revives
        // HERE — a short clean hop to the quest + cave — instead of re-trekking cross-continent from the far
        // default. NOT flag-gated: the blind set is unverified, and a stale "done" flag from a set that never
        // took is exactly why the char kept reviving at its old Mhaura/Windurst default and re-trekking (user:
        // "it can't ever home point"). For PLD that's Southern San d'Oria (230, next to Balasiel, one region
        // from Ordelle's). Falls back to the Jeuno hub if the quest zone has no mapped crystal.
        if (events is not null)
        {
            // ALWAYS the quest nation (not the current zone): the char must revive at San d'Oria for a San
            // d'Oria quest even if a death dragged it to a different crystal city (Windurst) first — else it
            // home-points on the wrong continent and re-treks. Only fall back to current/hub if the quest zone
            // has no mapped crystal.
            ushort hpZone = HomePointRoutines.Crystal.ContainsKey(cfg.UnlockTrekZoneId) ? cfg.UnlockTrekZoneId
                          : HomePointRoutines.Crystal.ContainsKey(zoning.CurrentZone) ? zoning.CurrentZone
                          : cfg.HubZoneId;
            if (zoning.CurrentZone != hpZone && Game.Zonelines.Name(hpZone) is { } hz) { Log($"routing to {hz} ({hpZone}) to set the home point"); await zoning.GoTo(hz, ct); }
            if (zoning.CurrentZone == hpZone) await HomePointRoutines.SetHere(p, nav, events, combat, hpZone, ct);
        }

        if (cfg.BeforeUnlock is { } bu) await bu(ct);

        // Keep stealth on for the QUEST-INTERNAL treks too, not just the trek to the quest city. The PLD
        // quest sends the char San d'Oria -> Ordelle's THROUGH La Theine (lv30-40 aggro); that leg was bare
        // and a WAR 32 got ganged to death there mid-quest (status=3), looping the whole quest. BeforeLeg
        // re-applies Sneak/Invis standing-still at every zone line for the duration of the quest.
        if (cfg.StealthUnlock)
            zoning.BeforeLeg = c2 => StealthRoutines.HasPowders(inv) ? StealthRoutines.Apply(inv, p, c2) : Task.CompletedTask;

        // RESUME MID-CHAIN: QuestRunner replays from step 0 (it has no quest-state awareness), so a death
        // mid-unlock re-walked already-done quests. When the brain supplies UnlockChain metadata, drop the
        // steps of any quest already COMPLETE in the live quest-log and run only what remains.
        IReadOnlyList<QuestStep> steps;
        if (cfg.UnlockChain.Count > 0)
        {
            var remaining = new List<QuestStep>();
            foreach (var q in cfg.UnlockChain)
            {
                if (QuestState.QuestComplete(p.World, q.DonePort, q.QuestId))
                    Log($"unlock-resume: quest {q.QuestId} already complete (port 0x{q.DonePort:X2}) — skipping its {q.Steps.Count} steps");
                else remaining.AddRange(q.Steps);
            }
            steps = remaining;
        }
        else steps = cfg.UnlockSteps ?? (IReadOnlyList<QuestStep>)System.Array.Empty<QuestStep>();

        // quests/trade/events are non-null on any Advanced brain (the ones that reach TryUnlock).
        try
        {
            if (steps.Count > 0)
                await new QuestRunner(p, nav, zoning, quests!, trade!, combat, gear, events!, inv).Run(steps, cfg.Tag, ct);
        }
        finally { zoning.BeforeLeg = null; }

        // Post-quest: the quest ends far from town — route to the NEAREST Mog House city to apply the change
        // (ChangeJobViaMogHouse picks the closest city itself; unlockCity is just the fallback hint).
        return await JobRoutines.ChangeJobViaMogHouse(jobs, zoning, cfg.MainJob, cfg.SubJob, unlockCity, ct);
    }

    // Post-death return for a baby whose home point is across hostile ground (crystal relocation is
    // unreliable). Delivers to the CURRENTLY-ACTIVE gated zone (same HuntZonePlan the grind fixed to — or
    // LevelGrind loops "delegating return"). Travel AS A STRONG JOB when one exists (the 30 sub), switch back
    // at the Mog House, and re-enter via the safe gate. With no strong job yet (initial sub baby), a plain
    // walk from the revive town suffices. Wired to LevelGrind.RecoveryTravel — MUST leave us IN that zone.
    async Task RecoverToHuntZone(byte job, CancellationToken ct)
    {
        if (cfg.HuntZonePlan(p.World.MainJobLevel) is not (string zone, ushort id)) return;   // plan now says nation path
        if (zoning.CurrentZone == id) return;
        byte safe = SafeTravelJobFor(job);
        byte sub = SubFor(job);
        if (safe != 0)
        {
            if (p.World.MainJob != safe)
            {
                Log($"recovery: switching to {JN(safe)} {LevelOf(safe)} for safe travel (was {JN(p.World.MainJob)}/{JN(p.World.SubJob)})");
                await jobs.ChangeJob(safe, job, ct);   // sub = the grind job (harmless while we travel)
                for (int t = 0; t < 20000 && p.World.MainJob != safe && !ct.IsCancellationRequested; t += 500) await Task.Delay(500, ct);
            }
            if (zoning.CurrentZone != cfg.HomeCityId)
            {
                Log($"recovery: walking to {cfg.HomeCity} as {JN(safe)}");
                await zoning.GoTo(cfg.HomeCity, ct);
            }
            Log($"recovery: changing back to {JN(job)}/{(sub == 0 ? "--" : JN(sub))} at the Mog House");
            await JobRoutines.ChangeJobViaMogHouse(jobs, zoning, job, sub, cfg.HomeCity, ct);   // waits for the job to apply
        }
        else if (zoning.CurrentZone != cfg.HomeCityId && zoning.CurrentZone != cfg.SafeGateVia)
        {
            Log($"recovery: walking to {cfg.HomeCity}");
            await zoning.GoTo(cfg.HomeCity, ct);
        }
        // Home-point at the HomeCity while we're here — piggybacks on this recovery (already at Windurst), no
        // extra dangerous trek. Same flow the stint start uses proactively (see EnsureHomePointAtHomeCity).
        await EnsureHomePointAtHomeCity(ct);
        // Enter a safe-gated zone (West Sarutabaruta) via Port Windurst, not the east goblin belt.
        Log($"recovery: heading to the active hunt zone {zone} ({id})");
        if (id == cfg.SafeGateZoneId && cfg.SafeGateVia != 0 && zoning.CurrentZone != cfg.SafeGateVia && zoning.CurrentZone != id)
            await zoning.ToZone(cfg.SafeGateVia, ct);
        await zoning.GoTo(zone, ct);
    }

    // Set the home point at the HomeCity crystal ONCE (flag-gated), reusing the shared HomePointRoutines.SetHere
    // (do NOT re-fork it — it was just de-duplicated). No-op unless we're standing in the HomeCity: called both
    // PROACTIVELY at stint start (fresh run, char just changed jobs in the HomeCity — set the revive point
    // BEFORE the first death) and during recovery (the safe-job return lands us in the HomeCity anyway). Once
    // set, every death resets HERE, adjacent to the gated hunt zones — user rule: home-point at a safe city
    // near the hunt grounds, NOT the field or a stale distant default like BLM's farm-era Mhaura. Generic:
    // reads cfg.HomeCity/HomeCityId, so it targets whatever nation-city a brain configures (Windurst by default).
    async Task EnsureHomePointAtHomeCity(CancellationToken ct)
    {
        string lvlHpFlag = $"/tmp/xibot_lvlhp_{p.World.MyId:X}.done";
        if (events is null || zoning.CurrentZone != cfg.HomeCityId || File.Exists(lvlHpFlag)) return;
        Log($"setting home point at {cfg.HomeCity} (safe city near the hunt grounds)");
        if (await HomePointRoutines.SetHere(p, nav, events, combat, cfg.HomeCityId, ct)) File.WriteAllText(lvlHpFlag, "set");
    }

    // A job already leveled high enough to cross hostile ground aggro-free (mobs ignore chars far above them).
    // During the advanced MAIN's baby phase the 30 sub is the strong job; during the initial SUB baby phase no
    // strong job exists yet (return is a plain safe-gate walk from the revive town).
    byte SafeTravelJobFor(byte grindJob)
    {
        foreach (var cand in new[] { cfg.SubJob, Job.War, Job.Whm })
            if (cand != 0 && cand != grindJob && LevelOf(cand) >= 20) return cand;
        return 0;
    }
}
