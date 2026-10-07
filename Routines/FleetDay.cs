using XiHeadless.Game;

namespace XiHeadless.Routines;

/// The fleet bot's DAY driver: composes SessionPlan (what today is) + PartyFinder (find/recruit a group in
/// the hunt zone) + PartyCombat (roster/puller doctrine) + FleetSchedule (group-safe end of day). A job brain
/// calls Run with its normal grind delegate; FleetDay routes the day:
///   Upkeep -> the brain's upkeep delegate (AH/restock), short day.
///   Solo   -> the brain's normal grind (exactly today's proven behavior).
///   Party  -> travel to the hunt zone FIRST (the caller's delegate — HuntZonePlan, the leveling guide),
///             then PartyFinder (listen -> answer -> or recruit); once formed, announce jobs, decide the
///             puller (BRD > SATA sub-tank > ranged tank), and run the caller's party-grind delegate.
/// FleetSchedule runs alongside the WHOLE day — the end-of-day logout is safe regardless of phase.
public static class FleetDay
{
    public sealed class Hooks
    {
        public Func<CancellationToken, Task<bool>> GoToHuntZone = _ => Task.FromResult(true);   // travel per the leveling guide; returns false ONLY if the zone is unreachable (no route)
        public Func<bool> AtHuntZone = () => true;   // true once we're IN the hunt zone — formation gates on this so a puller never parties in a city / en-route hub
        public (float x, float z)? MeetSpot;   // formation anchor: SHOUT ONLY REACHES 180y (server), so everyone converges here first
        public Func<CancellationToken, Task<bool>>? Defend;
        public Func<CancellationToken, Task>? PrepareTravel;   // before the walk to camp: ride, else Sneak/Invis (null = just try to mount)   // fight off an attacker mid-walk (true = fought); null = walk on
        public Func<CancellationToken, Task> SoloGrind = _ => Task.CompletedTask;      // the brain's normal loop
        public Func<PartyCombat.PullPlan, CancellationToken, Task> PartyGrind = (_, _) => Task.CompletedTask;
        public Func<CancellationToken, Task>? Upkeep;                                   // null = idle the short day
        public string Tag = "fleet";
    }

    public static async Task Run(IPerception p, ICombat combat, IParty party, IChat chat, IMagic magic,
                                 INavigation nav, ILifecycle lifecycle, Hooks hooks,
                                 SessionPlan.Plan? planOverride, CancellationToken ct)
    {
        var plan = planOverride ?? SessionPlan.ForToday(p.World.MyId);
        _ = FleetSchedule.WatchThenLogout(p, combat, party, chat, lifecycle, plan, hooks.Tag, ct);

        switch (plan.Mode)
        {
            case SessionPlan.DayMode.Upkeep:
                Log.Always($"[{hooks.Tag}] today is an UPKEEP day");
                if (hooks.Upkeep is { } up) await up(ct);
                await IdleUntilLogout(ct);   // short day; FleetSchedule ends it
                return;

            case SessionPlan.DayMode.Solo:
                Log.Always($"[{hooks.Tag}] today is a SOLO day");
                await hooks.SoloGrind(ct);
                return;

            case SessionPlan.DayMode.Party:
                Log.Always($"[{hooks.Tag}] today is a PARTY day — heading to the hunt zone to group up");
                // Reach the ACTUAL hunt zone BEFORE forming. Party joining must never trigger in a city or an
                // en-route hub: a puller stranded in Port Jeuno con'd only city objects and killed nothing all
                // session (user 2026-09-26). TRAVEL IS NOT TIME-BUDGETED: chocobo/airship-less chars can take
                // 30+ min of overland hops just to arrive, so the formation budget must NOT start until we're
                // there (user 2026-09-26). Keep travelling as long as we make zone progress; only SOLO if the
                // zone is unreachable (no route) or we're genuinely stuck (no zone change over many attempts).
                // Arrive = travel to the hunt zone, then walk to the camp. Re-run whenever we end up outside the
                // zone (a death on the walk-in home-points us to town: Nutha then recruited from Windurst Woods,
                // 2026-10-07). False = no route / stuck: solo for the day.
                async Task<bool> Arrive()
                {
                    ushort lastZone = 0; int noProgress = 0;
                    while (!ct.IsCancellationRequested && !hooks.AtHuntZone())
                    {
                        if (combat.Dead) { await Task.Delay(2000, ct); continue; }   // the core death rule homepoints us first
                        if (!await hooks.GoToHuntZone(ct))
                        {
                            Log.Always($"[{hooks.Tag}] no route to the hunt zone — SOLO grind for the day (never partying in a city)");
                            return false;
                        }
                        if (hooks.AtHuntZone()) break;
                        if (p.World.ZoneId == lastZone) noProgress++; else { noProgress = 0; lastZone = p.World.ZoneId; }
                        if (noProgress >= 6)   // 6 full travel attempts with zero zone change = stuck en route, not slow
                        {
                            Log.Always($"[{hooks.Tag}] stuck en route to the hunt zone (no zone progress) — SOLO grind for the day");
                            return false;
                        }
                        await Task.Delay(5000, ct);
                    }
                    if (hooks.MeetSpot is { } meet)   // converge into shout range (180y) before recruiting
                    {
                        // Keep walking until we ARRIVE: one 120s leg covered ~500y on foot, and a zone-in can sit
                        // 1000y from the camp (Meriphataud's south edge), so members recruited wherever the leg ran
                        // out, 300-500y apart, out of each other's 180y shout range (live, 2026-10-07).
                        if (hooks.PrepareTravel is { } prep) await prep(ct); else nav.TryMount();
                        await NavRoutines.WalkTo(nav, p, meet.x, meet.z, within: 3f, ct, legs: 8, legTimeoutMs: 120_000,
                            defend: hooks.Defend);
                    }
                    nav.Dismount();   // at camp: on foot to cast, rest and fight
                    return true;
                }
                do { if (!await Arrive()) { await hooks.SoloGrind(ct); return; } }
                while (!ct.IsCancellationRequested && !hooks.AtHuntZone());
                var finder = new PartyFinder(p, party, chat, nav, hooks.Tag);
                long jobAnnounceMs = 0;
                // FORMATION BUDGET (user spec: SOLO BACKUP after 30 minutes). The whole form phase — seeking
                // AND waiting on the minimum comp — shares one 30-min deadline. On expiry: a partial party
                // plays with what it has (a duo beats a solo); an empty roster falls back to the brain's solo
                // grind for the rest of the day (BotHost still auto-accepts a late invite).
                long formDeadline = Environment.TickCount64 + 1_800_000;
                // FORM: listen/answer/recruit until a party exists. While seeking, hold near the zone-in/camp
                // (the brain's solo loop would wander us away from responders).
                while (!ct.IsCancellationRequested && !finder.Step())
                {
                    if (!hooks.AtHuntZone())
                    {
                        long away = Environment.TickCount64;
                        if (!await Arrive()) { await hooks.SoloGrind(ct); return; }
                        formDeadline += Environment.TickCount64 - away;   // travel never burns the formation budget
                        continue;
                    }
                    if (Environment.TickCount64 > formDeadline)
                    {
                        Log.Always($"[{hooks.Tag}] no party after 30 min — SOLO fallback for the rest of the day");
                        await hooks.SoloGrind(ct);
                        return;
                    }
                    await Task.Delay(3000, ct);
                }
                // Wait for the START gate: minimum Tank+Healer+DD (recruiter counts promised roles), then run
                // — or the budget expires and we play with whoever joined.
                while (!ct.IsCancellationRequested && finder.Recruiting && !finder.MinimumMet())
                {
                    if (PartyUnderAttack(p)) { Log.Always($"[{hooks.Tag}] party is already fighting — starting below minimum comp"); break; }
                    if (Environment.TickCount64 > formDeadline)
                    {
                        if (party.MemberCount > 0) { Log.Always($"[{hooks.Tag}] 30-min budget: starting with {party.MemberCount + 1} (below minimum comp)"); break; }
                        Log.Always($"[{hooks.Tag}] no joiners after 30 min — SOLO fallback for the rest of the day");
                        await hooks.SoloGrind(ct);
                        return;
                    }
                    PartyCombat.AnnounceJob(chat, p, ref jobAnnounceMs);
                    finder.TopUp();
                    await Task.Delay(3000, ct);
                }
                // The START gate is the PARTY's, not just the recruiter's: the recruiter announces START when
                // its gate passes, and a joiner holds until it hears one. Without this the joiner skipped the
                // gate, voted itself puller and pulled while the recruiter sat in the comp wait and never
                // swung at the camp mob (Drusho/Nutha live test, 2026-10-07).
                if (finder.Recruiting) chat.Party(StartWord);
                else await AwaitPartyStart(p, chat, hooks.Tag, formDeadline, ct);
                Log.Always($"[{hooks.Tag}] party up ({party.MemberCount + 1} incl. me) — waiting for the JOB roster before the puller vote");
                var plan2 = await VoteWhenRosterComplete(p, party, chat, hooks.Tag, ct);
                Log.Always($"[{hooks.Tag}] puller vote: puller={plan2.Puller} style={plan2.Style} tank={plan2.Tank ?? "?"}");
                // No valid puller (roster is all casters + healer, nobody can pull) = the party can't hunt.
                // Solo grind the rest of the day instead of holding an idle camp (user 2026-09-16). Same
                // fallback as the 30-min no-party path above.
                if (string.IsNullOrEmpty(plan2.Puller))
                {
                    Log.Always($"[{hooks.Tag}] no pull-capable job in the party — SOLO grind for the day");
                    await hooks.SoloGrind(ct);
                    return;
                }
                int lastSize = party.MemberCount;
                while (!ct.IsCancellationRequested)
                {
                    // REUNITE after a death: the home point warps us to town, but the party is at camp. Travel
                    // back before playing the role (live: a homepointed BRD puller kept "pulling" and recruiting
                    // from Windurst Woods while its party waited in Meriphataud, 2026-10-07). Runs FIRST so TopUp
                    // never recruits from a city.
                    if (!hooks.AtHuntZone())
                    {
                        Log.Info($"[{hooks.Tag}] away from the hunt zone — travelling back to the party");
                        if (!await Arrive()) { await hooks.SoloGrind(ct); return; }
                        continue;
                    }
                    PartyCombat.AnnounceJob(chat, p, ref jobAnnounceMs);
                    finder.TopUp();                                    // keep filling toward the full 6
                    if (party.MemberCount != lastSize && party.MemberCount > 0)
                    {
                        // Membership changed -> the whole party re-announces + RE-VOTES on a complete roster
                        // (user rule: no puller decision until every member's job is known).
                        lastSize = party.MemberCount;
                        chat.Party(StartWord);   // a member who joined after the first START hears the party is running
                        plan2 = await VoteWhenRosterComplete(p, party, chat, hooks.Tag, ct);
                        Log.Always($"[{hooks.Tag}] re-vote ({lastSize + 1} incl. me): puller={plan2.Puller} style={plan2.Style} tank={plan2.Tank ?? "?"}");
                    }
                    await hooks.PartyGrind(plan2, ct);                 // one grind beat in role
                    if (party.MemberCount == 0)                        // disbanded on us -> back to seeking
                    {
                        Log.Info($"[{hooks.Tag}] party dissolved — seeking again");
                        while (!ct.IsCancellationRequested && !finder.Step()) await Task.Delay(3000, ct);
                        lastSize = party.MemberCount;
                        plan2 = await VoteWhenRosterComplete(p, party, chat, hooks.Tag, ct);
                    }
                }
                return;
        }
    }

    const string StartWord = "START";

    // A joiner's half of the START gate: hold until any member announces START (on party chat, after we joined),
    // the shared 30-min formation deadline passes, or something is already attacking the party (a human-led
    // party never says START; it just fights).
    static async Task AwaitPartyStart(IPerception p, IChat chat, string tag, long deadline, CancellationToken ct)
    {
        long jobAnnounceMs = 0;
        long since = p.World.NowMs - 60_000;   // a START sent while our join was landing still counts
        Log.Info($"[{tag}] joined — holding for the party's START");
        while (!ct.IsCancellationRequested && Environment.TickCount64 < deadline)
        {
            ChatLine[] log;
            try { log = p.World.ChatLog.ToArray(); } catch (ArgumentException) { log = []; }   // receive thread appends
            if (log.Any(c => c.Kind is 4 or 15 && c.Ms >= since && c.Message.Trim().Equals(StartWord, StringComparison.OrdinalIgnoreCase)))
            { Log.Info($"[{tag}] party START heard"); return; }
            if (PartyUnderAttack(p)) { Log.Info($"[{tag}] party is already fighting — starting"); return; }
            PartyCombat.AnnounceJob(chat, p, ref jobAnnounceMs);
            await Task.Delay(3000, ct);
        }
        Log.Info($"[{tag}] no START before the formation deadline — starting with the party as is");
    }

    // Something is hitting us or a party member right now: the party is effectively running, so a START wait ends.
    static bool PartyUnderAttack(IPerception p)
    {
        var w = p.World;
        return w.Attackers.ToArray().Any(a => w.NowMs - a.Value.ms < 10_000
            && (a.Value.target == w.MyId || w.PartyMembers.ContainsKey(a.Value.target)));
    }

    static async Task IdleUntilLogout(CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested) await Task.Delay(5000, ct); }
        catch (OperationCanceledException) { }
    }

    // The puller vote runs ONLY on a COMPLETE roster (user rule): announce our JOB immediately, then wait
    // until every party member's job is known (roster == size). Humans never announce, so a 2-min cap breaks
    // the wait and we vote with what we have (humans are never assigned duty anyway — right for OPEN parties).
    const int RosterWaitCapMs = 120_000;
    static async Task<PartyCombat.PullPlan> VoteWhenRosterComplete(IPerception p, IParty party, IChat chat, string tag, CancellationToken ct)
    {
        long force = 0;   // 0 => AnnounceJob's cadence gate passes immediately
        PartyCombat.AnnounceJob(chat, p, ref force);
        long startMs = Environment.TickCount64;
        while (!ct.IsCancellationRequested)
        {
            int size = party.MemberCount + 1, known = PartyCombat.Roster(p).Count;
            if (known >= size) { Log.Info($"[{tag}] roster complete ({known}/{size} jobs known)"); break; }
            if (Environment.TickCount64 - startMs > RosterWaitCapMs)
            { Log.Info($"[{tag}] roster incomplete after cap ({known}/{size} — human members?) — voting with what we know"); break; }
            await Task.Delay(3000, ct);
        }
        return PartyCombat.DecidePuller(PartyCombat.Roster(p));
    }
}
