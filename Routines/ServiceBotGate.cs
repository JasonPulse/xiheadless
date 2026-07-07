using XiHeadless.Net;

namespace XiHeadless.Routines;

/// Self-STOP for the standing service bots (GM + RMT). They log IN whenever there is any connection (an init
/// container gates login), and must log OUT once only the service accounts themselves remain online — i.e.
/// when total sessions fall to the service baseline (GM + RMT = 2). Run this alongside the brain's main loop;
/// when it fires it calls lifecycle.Logout() -> the host cancels the brain -> clean ~40s FFXI logout -> the
/// k8s Job completes. The init container gates the next login.
///
/// THE FLAP GUARD (the hard part the user called out): the session COUNT alone is ambiguous while a service
/// bot is still logging in — "1 real user + 1 service bot" reads sessions==2, identical to "just the two
/// service bots". So this does NOT act on a bare count:
///   (a) STARTUP GRACE — wait after our own login before evaluating, so the PEER service bot has time to come
///       up and the baseline is genuinely both of them (not us + a real user); and
///   (b) DEBOUNCE — sessions<=baseline must hold continuously before we log out (rides over a user zoning for
///       a moment / an accounts_sessions row lagging).
/// An unreadable API HOLDS (never logs out on a failed read). Behavior is CODE (consts below), no env.
public static class ServiceBotGate
{
    const int Baseline = 2;      // the standing service accounts that are last to leave: GM + RMT
    const int GraceSec = 120;    // wait after our own login before evaluating (let the peer service bot come up)
    const int PollSec  = 30;     // how often to re-check the world session count
    const int DebounceSec = 60;  // sessions<=Baseline must hold this long before we log out

    public static async Task WatchThenLogout(WorldApi world, ILifecycle lifecycle, string tag, CancellationToken ct)
    {
        try
        {
            await Task.Delay(GraceSec * 1000, ct);
            Log.Info($"[{tag}] idle-gate armed: log out when sessions <= {Baseline} held {DebounceSec}s (poll {PollSec}s, grace {GraceSec}s done)");
            long idleSinceMs = -1;
            while (!ct.IsCancellationRequested)
            {
                int sessions = await world.Sessions(ct);
                long now = Environment.TickCount64;
                if (sessions < 0 || sessions > Baseline)
                {
                    idleSinceMs = -1;   // API unreadable, OR someone else is online -> reset, stay logged in
                }
                else                    // only the service accounts remain (sessions <= Baseline)
                {
                    if (idleSinceMs < 0) idleSinceMs = now;
                    else if (now - idleSinceMs >= DebounceSec * 1000L)
                    {
                        Log.Always($"[{tag}] only service accounts remain (sessions={sessions} <= {Baseline}) for {DebounceSec}s -> logging out");
                        lifecycle.Logout();
                        return;
                    }
                }
                await Task.Delay(PollSec * 1000, ct);
            }
        }
        catch (OperationCanceledException) { /* brain cancelled elsewhere — nothing to do */ }
    }
}
