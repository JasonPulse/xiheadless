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
        public Func<CancellationToken, Task> GoToHuntZone = _ => Task.CompletedTask;   // travel per the leveling guide
        public (float x, float z)? MeetSpot;   // formation anchor: SHOUT ONLY REACHES 180y (server), so everyone converges here first
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
                await hooks.GoToHuntZone(ct);
                if (hooks.MeetSpot is { } meet)   // converge into shout range (180y) before recruiting
                {
                    nav.MoveTo(meet.x, meet.z);
                    for (int t = 0; t < 120_000 && nav.IsMoving && !ct.IsCancellationRequested; t += 500) await Task.Delay(500, ct);
                }
                var finder = new PartyFinder(p, party, chat, nav, hooks.Tag);
                long jobAnnounceMs = 0;
                // FORM: listen/answer/recruit until a party exists. While seeking, hold near the zone-in/camp
                // (the brain's solo loop would wander us away from responders).
                while (!ct.IsCancellationRequested && !finder.Step())
                    await Task.Delay(3000, ct);
                // Wait for the START gate: minimum Tank+Healer+DD (recruiter counts promised roles), then run.
                while (!ct.IsCancellationRequested && finder.Recruiting && !finder.MinimumMet())
                {
                    PartyCombat.AnnounceJob(chat, p, ref jobAnnounceMs);
                    finder.TopUp();
                    await Task.Delay(3000, ct);
                }
                Log.Always($"[{hooks.Tag}] party up ({party.MemberCount + 1} incl. me) — waiting for the JOB roster before the puller vote");
                var plan2 = await VoteWhenRosterComplete(p, party, chat, hooks.Tag, ct);
                Log.Always($"[{hooks.Tag}] puller vote: puller={plan2.Puller} style={plan2.Style} tank={plan2.Tank ?? "?"}");
                int lastSize = party.MemberCount;
                while (!ct.IsCancellationRequested)
                {
                    PartyCombat.AnnounceJob(chat, p, ref jobAnnounceMs);
                    finder.TopUp();                                    // keep filling toward the full 6
                    if (party.MemberCount != lastSize && party.MemberCount > 0)
                    {
                        // Membership changed -> the whole party re-announces + RE-VOTES on a complete roster
                        // (user rule: no puller decision until every member's job is known).
                        lastSize = party.MemberCount;
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
