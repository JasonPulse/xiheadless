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
/// An unreadable API HOLDS (never logs out on a failed read). Tunables via env (all optional).
public static class ServiceBotGate
{
    static int Env(string k, int dflt) => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) && v > 0 ? v : dflt;

    public static async Task WatchThenLogout(WorldApi world, ILifecycle lifecycle, string tag, CancellationToken ct)
    {
        int baseline = Env("XIBOT_IDLE_BASELINE", 2);       // the standing service accounts: GM + RMT
        int graceSec = Env("XIBOT_IDLE_GRACE_SEC", 120);    // let the peer service bot finish logging in first
        int pollSec  = Env("XIBOT_IDLE_POLL_SEC", 30);
        int debSec   = Env("XIBOT_IDLE_DEBOUNCE_SEC", 60);  // sessions<=baseline must hold this long

        try
        {
            await Task.Delay(graceSec * 1000, ct);
            Log.Info($"[{tag}] idle-gate armed: log out when sessions <= {baseline} held {debSec}s (poll {pollSec}s, grace {graceSec}s done)");
            long idleSinceMs = -1;
            while (!ct.IsCancellationRequested)
            {
                int sessions = await world.Sessions(ct);
                long now = Environment.TickCount64;
                if (sessions < 0 || sessions > baseline)
                {
                    idleSinceMs = -1;   // API unreadable, OR someone else is online -> reset, stay logged in
                }
                else                    // only the service accounts remain (sessions <= baseline)
                {
                    if (idleSinceMs < 0) idleSinceMs = now;
                    else if (now - idleSinceMs >= debSec * 1000L)
                    {
                        Log.Always($"[{tag}] only service accounts remain (sessions={sessions} <= {baseline}) for {debSec}s -> logging out");
                        lifecycle.Logout();
                        return;
                    }
                }
                await Task.Delay(pollSec * 1000, ct);
            }
        }
        catch (OperationCanceledException) { /* brain cancelled elsewhere — nothing to do */ }
    }
}
