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

        // First-login setup: a headless char never ran the New Character Cutscene (setHomePoint + clears
        // notSeen + unblocks events). It re-triggers on zone-in to a starting zone and leaves the char
        // "in event", suppressing all other NPC events. Blind-finish the zone's cutscene id (the server
        // matches currentEvent==csid; we don't parse the CS start). Idempotent: a done char (notSeen=0)
        // has no active CS, so the EVENTEND is a harmless "Not in an event".
        EnsureNewCharSetup(caps, conn.State.ZoneId);

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

    // Navmesh file = the zone's canonical name (from ZoneGraph) + ".nav", so every zone with a mesh
    // file in the navmesh dir is automatically walkable — no per-zone map to maintain.
    // New Character Cutscene csid per starting zone (New_Character_Cutscenes.lua). Finishing it runs
    // setHomePoint() + clears notSeen + unblocks NPC events. Core operating data (not a brain concern).
    static readonly Dictionary<ushort, ushort> CutsceneId = new()
    {
        [234] = 1,   [236] = 1,   [235] = 0,     // Bastok: Mines / Port / Markets(0->7)
        [238] = 531, [241] = 367, [240] = 305,   // Windurst: Waters / Woods / Port
        [230] = 503, [231] = 535, [232] = 500,   // San d'Oria: Southern / Northern / Port
    };

    // Complete the New Character Cutscene (which calls setHomePoint()) on login. Two obstacles, both
    // solved by finishing the ACTUAL active event (read from the parsed event-start packet) rather than
    // blind-firing a guessed id:
    //  1. The headless char never completed the CS at creation, so the home point is unset.
    //  2. Once the char is level >= 3, ROV mission 1-01 (event 30035) auto-fires on every city zone-in
    //     and wins the event slot, BLOCKING the new-char CS. (Confirmed via map logs: "Event ID
    //     mismatch 30035 != 367" — the old blind-fire of 367 always lost to the active 30035.)
    // So we DRAIN auto-firing zone-in events: finish whatever is active; if finishing a blocker leaves
    // no event, re-zone (out a zoneline and back) to re-trigger onZoneIn, until the new-char CS fires
    // and sets the home point. Idempotent: a set-up char (notSeen=0, ROV done) fires no zone-in event,
    // so this returns after one short wait. CutsceneId[zone] is the event whose onEventFinish calls
    // setHomePoint (Bastok Markets chains 0 -> 7, so its done-id is 7).
    static void EnsureNewCharSetup(CapabilitySet caps, ushort startZone)
    {
        if (!CutsceneId.TryGetValue(startZone, out var csid)) return;   // only starting cities run this
        ushort hpDoneId = startZone == 235 ? (ushort)7 : csid;
        var w = caps.Perception.World;
        bool blockerCleared = false;

        for (int pass = 0; pass < 8; pass++)
        {
            if (WaitFor(() => w.EventActive, 6000))
            {
                ushort ev = w.EventId;
                Console.WriteLine($"[setup] active zone-in event {ev} -> finishing (home-point CS is {hpDoneId})");
                caps.Events.FinishEvent(0).GetAwaiter().GetResult();
                Thread.Sleep(2500);
                if (w.EventId == ev) w.EventActive = false;   // parser doesn't clear it; reset unless a chained event replaced it
                if (ev == hpDoneId) { Console.WriteLine("[setup] new-character cutscene finished -> HOME POINT SET"); return; }
                blockerCleared = true;   // finished a blocker (ROV 30035 / Bastok intro 0); a chain may auto-start, else re-zone
                continue;
            }
            if (!blockerCleared) { Console.WriteLine("[setup] no zone-in event active -> char already set up"); return; }
            // A blocker was cleared but the new-char CS hasn't fired -> re-zone to re-trigger onZoneIn.
            var nb = Zonelines.All.FirstOrDefault(l => l.From == startZone && !CutsceneId.ContainsKey(l.To));
            if (nb.To == 0) { Console.WriteLine("[setup] no non-city neighbor to bounce off; stopping setup"); return; }
            Console.WriteLine($"[setup] re-zoning {startZone} -> {nb.To} -> {startZone} to re-trigger the new-char CS");
            caps.Zoning.ToZone(nb.To).GetAwaiter().GetResult();
            caps.Zoning.ToZone(startZone).GetAwaiter().GetResult();
            w.EventActive = false;
            blockerCleared = false;
        }
        Console.WriteLine("[setup] WARNING: drained events but never saw the new-char CS finish; home point may be unset");
    }

    static bool WaitFor(Func<bool> cond, int timeoutMs)
    {
        for (int i = 0; i < timeoutMs / 100 && !cond(); i++) Thread.Sleep(100);
        return cond();
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
