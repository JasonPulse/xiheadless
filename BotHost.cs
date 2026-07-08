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
        // The lobby handshake intermittently drops its socket mid-fetch ("Broken pipe" at Send_0xA1/FetchCharList)
        // — at this site it fails on nearly every first attempt and aborts the process (exit 134). Retry on a
        // transient socket/IO error with a fresh client instead of crashing; the next attempt connects cleanly.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var client = new XiClient(Host, ClientVer);
                await client.LoginAsync(account, password);
                client.LobbyDataConnect();
                client.LobbyView_0x26();
                client.LobbyView_0x1F();
                client.InitialCharList();
                return client;
            }
            catch (Exception ex) when (attempt < 6 && (ex is System.Net.Sockets.SocketException || ex is System.IO.IOException || ex.InnerException is System.Net.Sockets.SocketException))
            {
                Log.Info($"[lobby] connect attempt {attempt} failed ({ex.GetType().Name}: {ex.Message}) — retrying in 3s");
                await Task.Delay(3000);
            }
        }
    }

    public static async Task<int> Run(string account, string password, string brainName, int? runSeconds)
    {
        XiClient client;
        bool justCreated;
        try
        {
            client = await ConnectLobby(account, password);
            justCreated = client.SelectOrCreate();     // select the account's char, or create one if empty
            client.RequestZoneServer();                // 0xA2 zone handoff — throws on a stale/duplicate session
        }
        catch (Exception ex) when (ex.Message.Contains("0xA2") || ex.Message.Contains("stale/duplicate"))
        {
            // CRITICAL PATH, now HANDLED (was an unhandled crash, exit 134): a stale/duplicate session means the
            // server still holds this char online. Exit CLEANLY with a distinct code + one clear line so the
            // operator/babysitter paces the dirty cooldown (>=5 min) — a rapid relaunch just re-hits the held
            // session and 0xA2s again. (The babysitter's pgrep guard prevents the duplicate login up front.)
            Log.Always($"[fatal] session declined (0xA2 stale/duplicate) — server still holds this char; needs a cooldown before relaunch, NOT a rapid retry. [{ex.Message.Split('\n')[0]}]");
            return 75;
        }

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

        Log.Info("zoning in...");
        bool zoned = conn.ZoneInSync();
        Log.Info(zoned ? $"IN ZONE: {conn.State}" : "did not receive zone-in");
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
        var autoParty = AutoAcceptParty(caps, autoCts.Token);
        var autoDeath = AutoHandleDeath(caps, autoCts.Token);   // CORE: any death -> Home Point (every brain)
        var logToggle = LogToggle(caps, autoCts.Token);         // CORE: /tell "log on"/"log off" flips verbose logging live

        // New-char home point (CORE setup): a freshly-created char lands in its start city and that zone's
        // onZoneIn starts an opening cutscene whose finish calls setHomePoint(). We can't SEE that event
        // (0x32 recv gap), so the auto-completer never finishes it — the char sits frozen "in event" with NO
        // home point (death -> zone-0 limbo). Blind-finish it by id (per start zone) to set the home point
        // and unfreeze. Harmless no-op (event-id mismatch) once the char has already seen it.
        if (Game.NewCharCutscene.EventFor(conn.State.ZoneId) is int csEv and >= 0)
        {
            await Task.Delay(2000);
            Log.Info($"[newchar] blind-finishing start-city cutscene {csEv} in zone {conn.State.ZoneId} -> setHomePoint");
            await caps.Events.Finish(conn.State.MyId, 0, (ushort)csEv, 0);
        }

        var brain = BrainRegistry.Create(brainName, caps);
        Log.Info($"running brain: {brain.GetType().Name}");
        var runner = new BotRunner(brain);
        runner.Start();

        // Run until a stop signal — bounded by runSeconds for dev (logging state each tick), or
        // indefinitely for the fleet. stop.Wait returns true the moment a signal arrives.
        if (runSeconds is int sec)
            for (int i = 0; i < sec && !stop.Wait(1000); i++) Log.Info($"  state: {conn.State}");
        else
            stop.Wait();

        // Graceful shutdown: stop the brain first (so it isn't acting mid-logout), then clean-logout.
        Log.Always("stopping -> cancel brain + graceful logout");
        runner.Stop();
        autoCts.Cancel();   // stop the event auto-completer so it isn't finishing events mid-logout
        // Never log out while KO'd — a dead logout re-strands the char in zone-0 limbo on next login.
        // Revive at the home point first (home point must be set; see EnsureNewCharSetup).
        if (caps.Combat.Dead)
        {
            Log.Always("stopping while KO'd -> homepoint-revive before logout");
            caps.Combat.Homepoint().GetAwaiter().GetResult();
            Thread.Sleep(8000);   // let the warp/revive land before we log out
        }
        // RETREAT BEFORE LOGOUT: the ~40s logout hold leaves the char standing defenseless, and several
        // logout-window deaths came from mobs within aggro range at SIGTERM time (a goblin killed the WHM
        // standing still, live-observed). Step away from the nearest mob for up to ~12s before starting it.
        else
        {
            var st = caps.Perception.World;
            var near = caps.Perception.Nearest(e => e.IsMob && e.Hpp > 0 && caps.Perception.DistanceTo(e.X, e.Z) < 30f);
            if (near is not null)
            {
                float dx = st.X - near.X, dz = st.Z - near.Z;
                float len = MathF.Max(0.5f, MathF.Sqrt(dx * dx + dz * dz));
                Log.Info($"stopping near '{near.Name}' ({caps.Perception.DistanceTo(near.X, near.Z):F0}y) -> retreating before the logout hold");
                caps.Nav.MoveTo(st.X + dx / len * 35f, st.Z + dz / len * 35f);
                for (int t = 0; t < 12000 && caps.Nav.IsMoving; t += 400) Thread.Sleep(400);
                caps.Nav.Stop();
            }
        }
        conn.Stop();   // sends 0x0E7 and holds ~40s for the server to complete the logout
        Log.Always("session ended cleanly.");
        return 0;
    }

    // Continuously auto-complete any event the server pushes that the brain isn't deliberately driving.
    // A headless client never clicks through cutscenes, so an unhandled event (level-up CS, mission CS,
    // ROV, a stray NPC menu) leaves the char "in event" and suppresses everything else. We finish any
    // event that has lingered past a grace period since the last time the brain touched an event
    // (Examine/Finish stamp World.LastEventDrivenUtc), so deliberate quest/AH/job-change dialogs — which
    // respond well within the grace — are never stomped. For chained cutscenes the next event updates
    // w.EventId (parsed from its 0x32/0x34) and we finish that too — always by the parsed id, never a list.
    // Auto-accept party invites (private fleet server — only our bots are online, so any invite is a teammate).
    // Lets a grinding bot (e.g. the WAR tank) join the party a leech bot (WHM) forms, without brain-specific code.
    static async Task AutoAcceptParty(CapabilitySet caps, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                if (caps.Party.InvitePending)
                {
                    Log.Info($"[auto-party] invite from '{caps.Party.InviterName}' -> accepting");
                    caps.Party.AcceptInvite();
                    await Task.Delay(2000, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // CORE death rule (every brain, no per-brain duplication): a KO'd character does NOTHING until it recovers —
    // it must never lie dead or try to move/act while dead. Rule: dead -> Home Point, UNLESS a party HEALER that
    // can cast Raise is alive (then wait for the raise; the healer raises us when combat ends). If the healer is
    // also dead, everyone Home Points. Our current healer is a lv9 WHM with NO Raise spell, so the raise branch
    // can't apply yet -> we Home Point immediately (loop until actually revived). [Raise-wait = future once a
    // healer has Raise: check party members for a living Raise-capable WHM before homepointing.]
    static async Task AutoHandleDeath(CapabilitySet caps, CancellationToken ct)
    {
        // A KO'd character does NOTHING until it recovers. No healer can Raise us (WHM Raise is lv25, ours is lower),
        // so: dead -> Home Point (revive at the staging town). The party reunites there — the live member follows to
        // town on our death and we cross back into the farm zone TOGETHER (see PartySupport's dead-tank regroup),
        // which is how we avoid the one-sided-death desync (WAR re-crossing to the zone-in ~200y from the field WHM).
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);
                if (!caps.Combat.Dead) continue;
                caps.Nav.Stop();
                Log.Always("[death] KO'd -> Home Point (core death handler)");
                while (caps.Combat.Dead && !ct.IsCancellationRequested)
                {
                    await caps.Combat.Homepoint(ct);
                    await Task.Delay(8000, ct);
                }
                caps.Perception.World.RevivedMs = caps.Perception.World.NowMs;   // starts the weakness hold (no new pulls)
                Log.Always("[death] revived at home point");
            }
        }
        catch (OperationCanceledException) { }
    }

    // CORE runtime log toggle (every brain, no per-brain code): the fleet runs QUIET (XIBOT_VERBOSE=0), but an
    // operator can turn a bot verbose live — without restarting it — by sending it an in-game /tell "log on"
    // (or "debug on"/"verbose on"; "log off" to re-quiet). Any tell works; the reply goes to whoever asked.
    // Reuses the already-parsed WorldState.Tells + IChat — no new chat parsing.
    static async Task LogToggle(CapabilitySet caps, CancellationToken ct)
    {
        var w = caps.Perception.World;
        var seen = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);   // last tell ms acted on, per sender
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                foreach (var (sender, tell) in w.Tells.ToArray())
                {
                    if (seen.TryGetValue(sender, out var last) && tell.ms <= last) continue;
                    seen[sender] = tell.ms;
                    var m = tell.msg.Trim().ToLowerInvariant();
                    bool? on = m is "log on" or "debug on" or "verbose on" ? true
                             : m is "log off" or "debug off" or "verbose off" ? false
                             : (bool?)null;
                    if (on is not bool set) continue;   // not a log command — ignore
                    Log.SetVerbose(set);
                    caps.Chat.Tell(sender, $"verbose logging {(set ? "ON" : "OFF")}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    static async Task AutoCompleteEvents(CapabilitySet caps, CancellationToken ct)
    {
        var w = caps.Perception.World;
        const int graceMs = 7000;
        DateTime activeSince = DateTime.MaxValue;
        bool csAnnounced = false;   // announced the start-city blind-finish for THIS status-4 episode (dedupe spam)
        int csAttempts = 0;         // sends of the zone's KNOWN cutscene id this episode (escalate to sweep after a few)
        int sweepIdx = -1;          // rotating index into NewCharCutscene.KnownBlockers (-1 = not sweeping)
        ushort lastTried = 0;       // last id sent, to IDENTIFY the real event on the falling edge
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                // "In an event" is detected from the SERVER STATUS (animation 4 = ANIMATION_EVENT), which the
                // bot reliably receives (0x037), OR from EventActive (a parsed-but-unfinished 0x32/0x34 menu,
                // which may not raise status 4). Status 4 is the load-bearing signal — a cutscene the bot never
                // parsed a start for still shows status 4, so it's still detected and cleared below.
                bool inEvent = w.EventActive || w.ServerStatus == 4;
                if (!inEvent)
                {
                    // Falling edge while sweeping: the server ends an event ONLY on a matching id, so the LAST
                    // id sent is the real stuck event we could never parse — log it as discovered knowledge.
                    if (sweepIdx >= 0) Log.Always($"[auto-event] status cleared after EVENTEND {lastTried} in zone {w.ZoneId} — that was the real stuck event id (unparsed 0x32)");
                    activeSince = DateTime.MaxValue; csAnnounced = false; csAttempts = 0; sweepIdx = -1; lastTried = 0;
                    continue;   // episode ended -> re-arm the announce
                }
                if (activeSince == DateTime.MaxValue) activeSince = DateTime.UtcNow;   // rising edge of an active event
                var reference = w.LastEventDrivenUtc > activeSince ? w.LastEventDrivenUtc : activeSince;
                if ((DateTime.UtcNow - reference).TotalMilliseconds < graceMs) continue; // brain may be driving it

                // GENERIC auto-finish (NO hardcoded event-id list — hard user rule): end WHATEVER event the
                // server last told us about, by its real parsed id (w.EventId), regardless of EventActive.
                // EventActive is unreliable as the "which id" signal — our own FinishEvent/Examine clear it,
                // so after one finish attempt it's false while status may still be 4 (chained CS, or a finish
                // that didn't take). w.EventId, by contrast, persists as the last parsed event id. The server's
                // EVENTEND handler (0x05b_eventend) ends the event ONLY when the sent id matches
                // currentEvent->eventId, so the correct parsed id ends it and a stale/wrong id is a harmless
                // no-op. Every cutscene/menu/level-up/mission/ROV/proximity-gate CS clears through this one path.
                if (w.EventId != 0)
                {
                    Log.Info($"[auto-event] event {w.EventId} lingered (status={w.ServerStatus}) with no brain action -> auto-finishing by parsed id");
                    await caps.Events.FinishEvent(0, ct);   // FinishEvent(selection) ends st.EventId
                }
                else
                {
                    // status==4 but we NEVER parsed an event-start id: its 0x32/0x34 was lost in the login/
                    // zone-in recv gap. We can't generically end an event whose id we never received — EXCEPT
                    // a START CITY's onZoneIn home-point cutscene, whose id is deterministic per zone. It fires
                    // on EVERY entry (initial login AND recovery/seesaw RE-ENTRY), and if left unfinished the
                    // status=4 blocks the next zone-line crossing -> the bot freezes mid-travel (the fleet
                    // stopper). BotHost blind-finishes it once at login; do the SAME here for re-entries, by the
                    // zone's known id from the existing NewCharCutscene table (NOT a new hardcoded end-list —
                    // it's the same documented recv-gap band-aid, just applied on re-entry too).
                    int csEv = Game.NewCharCutscene.EventFor(w.ZoneId);
                    if (csEv >= 0 && csAttempts < 3)
                    {
                        // Announce ONCE per episode (Always); retry the zone's own cutscene a few times quietly.
                        if (!csAnnounced) { Log.Always($"[auto-event] stuck status=4 in start-city zone {w.ZoneId}, no parsed id -> blind-finishing its known onZoneIn cutscene {csEv} to clear the travel block"); csAnnounced = true; }
                        else Log.Info($"[auto-event] re-attempting cutscene {csEv} clear (still status=4) in zone {w.ZoneId}");
                        csAttempts++;
                        lastTried = (ushort)csEv;
                        await caps.Events.Finish(w.MyId, 0, (ushort)csEv, 0, ct);
                    }
                    else
                    {
                        // The zone's own cutscene didn't clear it (or the zone has none): the char carries a
                        // DIFFERENT unparsed event. BURST every known blocker id back-to-back — the current
                        // event id MOVES with zone transitions (map log: 30000 -> 839 mid-sweep on Zzshekashi),
                        // so a one-id-per-cycle rotation chases a moving target and never lands. A mismatched
                        // EVENTEND is rejected at packet validation (harmless), and the map server logs the
                        // REAL current id in each rejection ("Event ID mismatch <current> != <sent>").
                        var blockers = Game.NewCharCutscene.KnownBlockers;
                        if (sweepIdx < 0) Log.Always($"[auto-event] zone {w.ZoneId}'s known cutscene didn't clear status=4 (or none known) -> bursting all {blockers.Length} known blocker ids");
                        sweepIdx++;
                        foreach (var id in blockers)
                        {
                            if (w.ServerStatus != 4 && !w.EventActive) break;   // cleared mid-burst — stop
                            lastTried = id;
                            await caps.Events.Finish(w.MyId, 0, id, 0, ct);
                            await Task.Delay(300, ct);
                        }
                        Log.Info($"[auto-event] blocker burst #{sweepIdx} done (status={w.ServerStatus}, zone {w.ZoneId})");
                    }
                }
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
        if (!File.Exists(path)) { Log.Info($"navmesh not found for zone {zoneId} ({name}): {path}"); return null; }
        Log.Info($"loaded navmesh {name}.nav for zone {zoneId}");
        return NavMesh.Load(path);
    }

    /// Delete every character on the account (frees slots / clears test junk).
    public static async Task<int> Cleanup(string account, string password)
    {
        var client = await ConnectLobby(account, password);
        client.DeleteAllChars();
        Log.Info("cleanup done.");
        return 0;
    }

    /// Fleet provisioning / create-path test: create one character on the account (the same path an
    /// empty-account deploy uses). No zone handoff, so it won't leave a session to clear.
    public static async Task<int> Provision(string account, string password, byte job = 1, int nation = -1)
    {
        var client = await ConnectLobby(account, password);
        client.CreateChar(job, nation);   // generates a name, creates, verifies by re-selecting; throws on failure
        Log.Info($"provision: character created (job={job} nation={nation}; now in the account's char-list).");
        return 0;
    }
}
