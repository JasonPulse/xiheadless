namespace XiHeadless.Brains;

/// FLEET INTEGRATION TEST (dev-only, like PldTestBrain): exercises the fleet day machinery end-to-end with
/// a SHORT forced day plan — travel to the meet zone, PartyFinder shout/tell/invite formation, JOB roster +
/// puller vote, then the FleetSchedule ENDAT/DONE group logout — WITHOUT field combat (that's the next,
/// field-tested pass). Meet zone: Windurst Woods (241). Chars far away travel as their STRONGEST job first
/// (the existing safe-travel pattern), so a lvl-1 main doesn't die on the trek.
public sealed class FleetTestBrain(
    IPerception p, INavigation nav, ICombat combat, IZoning zoning, IParty party, IChat chat, IMagic magic,
    IJobChange jobs, ILifecycle lifecycle) : IBrain
{
    const string MeetZone = "Windurst_Woods";
    const ushort MeetZoneId = 241;
    const int PlanMinutes = 14;   // short day: form + a few minutes partied + the group logout, all observable

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Always($"[fleettest] char='{p.World.MyName}' job={p.World.MainJob}/{p.World.MainJobLevel} zone={zoning.CurrentZone}");

        // Travel safety: if our MAIN is a baby but another job is strong, travel as the strong one (the
        // JobLifecycle safe-travel rule). Change back at the meet zone's Mog House so the roster sees the
        // real job. Skip entirely when already at the meet zone.
        if (zoning.CurrentZone != MeetZoneId)
        {
            var strongest = p.World.JobLevels.OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (strongest.Key != 0 && strongest.Key != p.World.MainJob && strongest.Value >= p.World.MainJobLevel + 10)
            {
                Log.Always($"[fleettest] traveling as job {strongest.Key} lv{strongest.Value} (main is lv{p.World.MainJobLevel})");
                byte realMain = p.World.MainJob;
                await JobRoutines.ChangeJobViaMogHouse(jobs, zoning, strongest.Key, 0, MeetZone, ct);
                if (!await zoning.GoTo(MeetZone, ct)) { Log.Always("[fleettest] couldn't reach the meet zone"); lifecycle.Logout(); return; }
                await JobRoutines.ChangeJobViaMogHouse(jobs, zoning, realMain, strongest.Key, MeetZone, ct);
            }
            else if (!await zoning.GoTo(MeetZone, ct)) { Log.Always("[fleettest] couldn't reach the meet zone"); lifecycle.Logout(); return; }
        }

        // Short forced plan (test): PARTY day ending in PlanMinutes — exercises formation AND the group logout.
        var plan = new Routines.SessionPlan.Plan(
            Routines.SessionPlan.DayMode.Party, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(PlanMinutes));

        await Routines.FleetDay.Run(p, combat, party, chat, magic, nav, lifecycle, new Routines.FleetDay.Hooks
        {
            Tag = "fleettest",
            GoToHuntZone = _ => Task.CompletedTask,   // we're already at the meet zone
            PartyGrind = async (pull, c) =>
            {
                // No combat in this test — just hold formation and let the roster/puller machinery run.
                await Task.Delay(5000, c);
            },
        }, plan, ct);
    }
}
