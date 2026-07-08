namespace XiHeadless.Routines;

/// The fleet bot's END-OF-DAY watcher: runs alongside a grinding brain (like ServiceBotGate does for GM/RMT)
/// and, when the SessionPlan's end time arrives, coordinates a SAFE logout:
///
///   * NEVER mid-combat — waits until disengaged AND nothing has attacked us recently.
///   * NEVER strand the group — parties converge on the group's LATEST end time ("ENDAT <unixmin>" on party
///     chat, announced periodically), so everyone goes done together; then each bot announces "DONE" and
///     waits for every fellow member's DONE before leaving. OPEN parties can hold real humans who will never
///     send DONE — a wait CAP (GroupWaitCapMin) breaks the deadlock: say goodbye like a person, leave anyway.
///   * The brain keeps RUNNING the whole time (a done healer keeps healing until the group is out) — this
///     watcher only acts at the very end: goodbye -> Leave() -> lifecycle.Logout() (BotHost then does the
///     existing retreat-from-mobs + 0x0E7 + 40s hold).
///
/// Behavior is CODE (consts); brains opt in by starting the watcher (fleet grind brains only — service/test
/// brains have their own end checks).
public static class FleetSchedule
{
    const int PollMs = 5000;
    const int AnnounceEveryMs = 240_000;   // re-announce ENDAT while partied (new members learn the group end)
    const int DoneEveryMs = 60_000;        // re-announce DONE while waiting on the group
    const int GroupWaitCapMin = 10;        // max minutes to wait for member DONEs (humans never send one)
    const int QuietMsBeforeLogout = 8000;  // "not in combat" = disengaged + no attacker for this long

    public static async Task WatchThenLogout(
        IPerception p, ICombat combat, IParty party, IChat chat, ILifecycle lifecycle,
        SessionPlan.Plan plan, string tag, CancellationToken ct)
    {
        var w = p.World;
        var end = plan.EndUtc;
        long lastAnnounceMs = 0, lastDoneMs = 0;
        DateTime doneAt = DateTime.MaxValue;
        Log.Info($"[{tag}] day plan: {plan.Mode}, done at {end:HH:mm}Z (~{(end - DateTime.UtcNow).TotalHours:F1}h)");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PollMs, ct);

                // GROUP CONVERGENCE: adopt the LATEST ENDAT any member announces (and re-announce ours so
                // late joiners converge too). "ENDAT <minutes-since-epoch>" rides party chat.
                foreach (var (sender, (msg, _)) in w.PartyChat.ToArray())
                {
                    if (sender.Equals(w.MyName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (msg.StartsWith("ENDAT ", StringComparison.OrdinalIgnoreCase)
                        && long.TryParse(msg.AsSpan(6), out var min))
                    {
                        var theirs = DateTime.UnixEpoch.AddMinutes(min);
                        if (theirs > end && theirs < DateTime.UtcNow.AddHours(20)) end = theirs;   // sanity cap
                    }
                }
                if (party.MemberCount > 0 && w.NowMs - lastAnnounceMs > AnnounceEveryMs)
                {
                    lastAnnounceMs = w.NowMs;
                    chat.Party($"ENDAT {(long)(end - DateTime.UnixEpoch).TotalMinutes}");
                }

                if (DateTime.UtcNow < end) { doneAt = DateTime.MaxValue; continue; }   // still playing

                // ---- done for the day ----
                if (doneAt == DateTime.MaxValue) { doneAt = DateTime.UtcNow; Log.Always($"[{tag}] done for the day ({plan.Mode}) — coordinating a safe logout"); }

                // 1) never mid-combat: engaged, or something hit us recently -> keep playing, check again.
                bool inCombat = combat.Engaged || p.AttackersOn(w.MyId, QuietMsBeforeLogout) > 0;
                if (inCombat) continue;

                // 2) group: announce DONE, wait for every fellow member's DONE (cap for human members).
                if (party.MemberCount > 0)
                {
                    if (w.NowMs - lastDoneMs > DoneEveryMs) { lastDoneMs = w.NowMs; chat.Party("DONE"); }
                    int doneVotes = w.PartyChat.ToArray().Count(kv =>
                        !kv.Key.Equals(w.MyName, StringComparison.OrdinalIgnoreCase)
                        && kv.Value.msg.Equals("DONE", StringComparison.OrdinalIgnoreCase));
                    bool allDone = doneVotes >= party.MemberCount;
                    bool waitedOut = (DateTime.UtcNow - doneAt).TotalMinutes >= GroupWaitCapMin;
                    if (!allDone && !waitedOut) continue;   // keep playing our role while the group finishes

                    chat.Party(waitedOut && !allDone ? "gotta go — thanks for the party!" : "good session — thanks all!");
                    await Task.Delay(1500, ct);
                    party.Leave();
                    await Task.Delay(1500, ct);
                }

                Log.Always($"[{tag}] safe to log out (disengaged, group clear) -> logging out for the day");
                lifecycle.Logout();
                return;
            }
        }
        catch (OperationCanceledException) { /* brain cancelled elsewhere */ }
    }
}
