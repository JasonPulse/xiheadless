using System.Runtime.InteropServices;

namespace XiHeadless;

/// Owns one bot's lifecycle: login -> lobby -> select-or-create char -> zone-in ->
/// run the chosen brain until shutdown (SIGTERM/Ctrl-C/duration), then clean-logout.
/// Program is just a shell that parses inputs and calls this. The only runtime inputs are
/// account, password, and brain — everything a brain does is coded in the brain. An empty
/// account auto-creates a character (one-step deploy), so a fresh account just works.
public static class BotHost
{
    const string Host = "ffxi.network-gnomes.com"; // the server (fixed); not a runtime input
    const string ClientVer = "30251101_2";         // matches the running server's settings/login.lua

    /// Login + lobby handshake shared by Run and Cleanup: TLS auth, data/view connect, the
    /// 0x26/0x1F negotiation, and the initial char-list fetch. Returns a lobby-ready client.
    static async Task<XiClient> ConnectLobby(string account, string password)
    {
        var client = new XiClient(Host, ClientVer);
        await client.LoginAsync(account, password);
        client.LobbyDataConnect();
        client.LobbyView_0x26();
        client.LobbyView_0x1F();
        client.InitialCharList();
        return client;
    }

    public static async Task<int> Run(string account, string password, string brainName, int? runSeconds)
    {
        var client = await ConnectLobby(account, password);
        bool justCreated = client.SelectOrCreate();   // select the account's char, or create one if empty
        client.RequestZoneServer();

        var resDir = Path.Combine(AppContext.BaseDirectory, "res");
        var sessionKey = new byte[20]; if (justCreated) sessionKey[16] = 6; // +6 byte16 for a fresh char
        var conn = new MapConnection(client.MapServer, client.CharId, sessionKey, resDir);
        conn.State.MyName = client.CharName;   // so brains know their own char (e.g. gil-grant target)

        // Graceful stop signal. k8s SIGTERM (pod delete/scale-down) and Ctrl-C set it; we then cancel
        // the brain and clean-logout. Cancel the default termination so the runtime lets us finish the
        // ~40s logout (the pod's terminationGracePeriodSeconds must be >= ~45s for it to complete).
        using var stop = new ManualResetEventSlim(false);
        using var onTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Set(); });
        using var onInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.Set(); });

        Console.WriteLine("zoning in...");
        bool zoned = conn.ZoneInSync();
        Console.WriteLine(zoned ? $"IN ZONE: {conn.State}" : "did not receive zone-in");
        conn.Start();

        // onLogout = the same stop signal SIGTERM uses, so a brain can end its own session (ILifecycle).
        var caps = new CapabilitySet(conn, LoadZoneMesh(conn.State.ZoneId), stop.Set);
        // On every zone change, hot-swap the navmesh so navigation works in the new zone.
        conn.ZoneChanged += zid => caps.SwapMesh(LoadZoneMesh(zid));

        // Headless event auto-completer (CORE/system, runs the whole session): there is no human to
        // dismiss cutscenes, so any server-pushed event left open freezes the bot "in event". This
        // finishes anything that lingers — including the New Character Cutscene, which calls
        // setHomePoint(). A fresh char's first login is at its starting city at level 1 (before the ROV
        // mission cutscene exists at level 3), so the new-char CS fires and gets auto-finished here ->
        // home point set with no special setup code. Setting up / navigating is brain activity, not
        // system logic, and the bot takes no config beyond account/password/brain.
        using var autoCts = new CancellationTokenSource();
        var autoEvents = AutoCompleteEvents(caps, autoCts.Token);

        var brain = BrainRegistry.Create(brainName, caps);
        Console.WriteLine($"running brain: {brain.GetType().Name}");
        var runner = new BotRunner(brain);
        runner.Start();

        // Run until a stop signal — bounded by runSeconds for dev (logging state each tick), or
        // indefinitely for the fleet. stop.Wait returns true the moment a signal arrives.
        if (runSeconds is int sec)
            for (int i = 0; i < sec && !stop.Wait(1000); i++) Console.WriteLine($"  state: {conn.State}");
        else
            stop.Wait();

        // Graceful shutdown: stop the brain first (so it isn't acting mid-logout), then clean-logout.
        Console.WriteLine("stopping -> cancel brain + graceful logout");
        runner.Stop();
        autoCts.Cancel();   // stop the event auto-completer so it isn't finishing events mid-logout
        // Never log out while KO'd — a dead logout re-strands the char in zone-0 limbo on next login.
        // Revive at the home point first (home point must be set; see EnsureNewCharSetup).
        if (caps.Combat.Dead)
        {
            Console.WriteLine("stopping while KO'd -> homepoint-revive before logout");
            caps.Combat.Homepoint().GetAwaiter().GetResult();
            Thread.Sleep(8000);   // let the warp/revive land before we log out
        }
        conn.Stop();   // sends 0x0E7 and holds ~40s for the server to complete the logout
        Console.WriteLine("session ended cleanly.");
        return 0;
    }

    // Continuously auto-complete any event the server pushes that the brain isn't deliberately driving.
    // A headless client never clicks through cutscenes, so an unhandled event (level-up CS, mission CS,
    // ROV, a stray NPC menu) leaves the char "in event" and suppresses everything else. We finish any
    // event that has lingered past a grace period since the last time the brain touched an event
    // (Examine/Finish stamp World.LastEventDrivenUtc), so deliberate quest/AH/job-change dialogs — which
    // respond well within the grace — are never stomped. For chained cutscenes the next event re-arms
    // EventActive and we finish that too.
    static async Task AutoCompleteEvents(CapabilitySet caps, CancellationToken ct)
    {
        var w = caps.Perception.World;
        const int graceMs = 7000;
        DateTime activeSince = DateTime.MaxValue;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                if (!w.EventActive) { activeSince = DateTime.MaxValue; continue; }
                if (activeSince == DateTime.MaxValue) activeSince = DateTime.UtcNow;   // rising edge of an active event
                var reference = w.LastEventDrivenUtc > activeSince ? w.LastEventDrivenUtc : activeSince;
                if ((DateTime.UtcNow - reference).TotalMilliseconds < graceMs) continue; // brain may be driving it
                ushort ev = w.EventId;
                Console.WriteLine($"[auto-event] event {ev} lingered with no brain action -> auto-finishing");
                await caps.Events.FinishEvent(0, ct);
                activeSince = DateTime.MaxValue;
            }
        }
        catch (OperationCanceledException) { }
    }

    static NavMesh? LoadZoneMesh(ushort zoneId)
    {
        var dir = Environment.GetEnvironmentVariable("XIBOT_NAVMESH_DIR")
                  ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Code/Lua/personal/temp/server/navmeshes");
        var name = Game.Zonelines.Name(zoneId);
        var path = Path.Combine(dir, name + ".nav");
        if (!File.Exists(path)) { Console.WriteLine($"navmesh not found for zone {zoneId} ({name}): {path}"); return null; }
        Console.WriteLine($"loaded navmesh {name}.nav for zone {zoneId}");
        return NavMesh.Load(path);
    }

    /// Delete every character on the account (frees slots / clears test junk).
    public static async Task<int> Cleanup(string account, string password)
    {
        var client = await ConnectLobby(account, password);
        client.DeleteAllChars();
        Console.WriteLine("cleanup done.");
        return 0;
    }

    /// Fleet provisioning / create-path test: create one character on the account (the same path an
    /// empty-account deploy uses). No zone handoff, so it won't leave a session to clear.
    public static async Task<int> Provision(string account, string password)
    {
        var client = await ConnectLobby(account, password);
        client.CreateChar();   // generates a name, creates, verifies by re-selecting; throws on failure
        Console.WriteLine("provision: character created (now appears in the account's char-list).");
        return 0;
    }
}
