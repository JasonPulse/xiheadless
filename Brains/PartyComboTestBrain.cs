using XiHeadless.Game;

namespace XiHeadless.Brains;

/// PARTY COMBAT integration test (dev-only). Unlike FleetTestBrain (formation only, no-op grind), this runs
/// the REAL PartyGrind.Beat so we finally watch a party FIGHT: travel to a hunt zone, form via PartyFinder,
/// vote the puller, then puller-pulls / DDs kill the camp mob / healer tops the party — the whole combat
/// doctrine end to end. Short forced Party day so it's observable. Run on the local trio (tank/dps/healer).
/// Zone/con: the puller's Consider gates pulls, so a level-mismatched trio may dry-pull — that's a finding.
public sealed class PartyComboTestBrain(
    IPerception p, INavigation nav, ICombat combat, IZoning zoning, IGear gear, IParty party, IChat chat,
    IMagic magic, IJobChange jobs, ILifecycle lifecycle) : IBrain
{
    // Tahrongi Canyon (123): a real Windurst-adjacent hunt zone with live mobs (unlike the Woods plaza).
    const string HuntZone = "Tahrongi_Canyon";
    const ushort HuntZoneId = 123;
    const int PlanMinutes = 20;   // form + several pull/kill cycles + the group logout, all observable

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Always($"[partytest] char='{p.World.MyName}' job={p.World.MainJob}/{p.World.MainJobLevel} zone={zoning.CurrentZone}");

        if (zoning.CurrentZone != HuntZoneId && !await zoning.GoTo(HuntZone, ct))
        { Log.Always("[partytest] couldn't reach the hunt zone"); lifecycle.Logout(); return; }

        // Minimal per-job grind config: the generic kit + a wide con band so the mixed-level trio engages
        // what it can. JobKits injects songs/nukes/JAs/heal for whatever job this char is.
        var g = new Routines.LevelGrind.Config
        {
            ConMin = 1, ConMax = 5, RestHpTrigger = 50, RestHpTarget = 80,
            RestMpPct = magic is not null ? 40 : 0, Tag = "partytest",
            WepSkillForLevel = _ => 1,   // test: WS may not fire; the party PATH is what we're verifying
        };
        Routines.JobKits.Apply(g, p.World.MainJob, combat, magic, p, "partytest", null);

        var pg = new Routines.PartyGrind(p, combat, magic, nav, gear, chat, g, "partytest");
        var plan = new Routines.SessionPlan.Plan(
            Routines.SessionPlan.DayMode.Party, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(PlanMinutes));

        await Routines.FleetDay.Run(p, combat, party, chat, magic, nav, lifecycle, new Routines.FleetDay.Hooks
        {
            Tag = "partytest",
            GoToHuntZone = _ => Task.CompletedTask,     // already in the hunt zone
            MeetSpot = null,                            // form where we stand (all three travel to the same zone)
            PartyGrind = (pull, c) => pg.Beat(pull, c), // THE real combat beat
        }, plan, ct);
    }
}
