namespace XiHeadless.Brains;

/// The GM grant bot — the fleet counterpart to RmtBrain, but its request channel is IN-GAME TELLS (not an
/// HTTP intake): a dedicated GM character (permission=1) logs in and listens for /tell requests from fleet
/// bots. Tells ride the game server, so this works across hosts/pods with no localhost coordination and no
/// second HTTP surface next to the RMT bot — and the tell's SENDER is authoritative, so a bot can only ever
/// request a grant FOR ITSELF (no target-name spoofing).
///
/// A requesting bot sends: /tell <GmCharName> "grantjob PLD"   (or "unlock PLD" / "job PLD")
///                         /tell <GmCharName> "setcap 50"      (or "cap 50" / "limit 50")
/// This bot then issues the matching persistent server command targeting the sender:
///   "!grantjob <sender> <job>"  /  "!setcap <sender> <level>"
/// spaced out (the server processes ~one command per tick), and tells the requester back with the result.
///
/// Behavior is CODE (consts below). Reuses IChat + WorldState.Tells (already parsed) — no new chat/movement.
public sealed class GmBrain(IPerception p, IChat chat, ILifecycle lifecycle, WorldApi world) : IBrain
{
    const int CommandSpacingMs = 1500; // gap between GM commands (server processes ~one per tick)
    const int MaxGrants = 0;           // end check: log out after this many grants (0 = run until stopped)

    // request-keyword -> command. First token of the tell (lowercased) selects the grant kind.
    static readonly string[] JobWords = { "grantjob", "job", "unlock" };
    static readonly string[] CapWords = { "setcap", "cap", "limit", "limitbreak" };
    // BLU learns spells by BEING HIT by mob abilities — impractical for bots, so the GM grants them via the
    // STOCK !addspell (permission=1, handles blue magic). The requester then SETS them itself (IMagic.SetBlueSpell).
    static readonly string[] SpellWords = { "addspell", "spell", "learnspell" };

    // Last tell timestamp (WorldState.NowMs) we've already acted on, per sender — so a NEW tell is processed
    // but the same one isn't re-issued every poll (WorldState.Tells keeps only the latest per sender).
    readonly Dictionary<string, long> _seen = new(StringComparer.OrdinalIgnoreCase);

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Always($"[gm] grant bot up (char='{p.World.MyName}') — listening for /tell requests: 'grantjob <JOB>' or 'setcap <1-99>'");
        // Enable the GM icon on login (user rule) — VERIFIED via our own 0x037 (flags0 GmLevel bits), because
        // !togglegm is a TOGGLE: blind re-sends could flip it back off. Send only while the flag still reads 0.
        for (int t = 0; t < 5 && p.World.VisibleGmLevel == 0 && !ct.IsCancellationRequested; t++)
        {
            chat.Say("!togglegm");
            await Task.Delay(4000, ct);   // let the command apply + the next 0x037 arrive
        }
        Log.Always($"[gm] visible GM level = {p.World.VisibleGmLevel} {(p.World.VisibleGmLevel > 0 ? "(icon on)" : "(FAILED to enable — !togglegm not applying)")}");
        // Self-stop: log out once only the service accounts (GM + RMT) remain online. Runs alongside; when it
        // fires, lifecycle.Logout() cancels ct and the loop below exits.
        _ = ServiceBotGate.WatchThenLogout(world, lifecycle, "gm", ct);
        int grants = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Snapshot new tell requests (sender + message) that we haven't acted on yet.
                foreach (var (sender, (msg, ms)) in p.World.Tells.ToArray())
                {
                    if (_seen.TryGetValue(sender, out var last) && ms <= last) continue; // already handled this tell
                    _seen[sender] = ms;

                    if (!TryParse(msg, out string kind, out string value))
                        continue; // not a grant request (some other tell) — ignore silently

                    // The SENDER is the grant target — a bot can only request for itself.
                    // NOTE argument orders differ: !grantjob/<setcap> take <player> <value>; the stock
                    // !addspell takes <spellID/Name> <player>.
                    string cmd = kind switch
                    {
                        "job" => $"!grantjob {sender} {value}",
                        "spell" => $"!addspell {value} {sender}",
                        _ => $"!setcap {sender} {value}",
                    };
                    chat.Say(cmd);
                    grants++;
                    Log.Always($"[gm] {sender} requested '{msg}' -> issued: {cmd} ({grants} issued)");
                    chat.Tell(sender, kind switch { "job" => $"granted job {value}", "spell" => $"added spell {value}", _ => $"set cap {value}" });

                    await Task.Delay(CommandSpacingMs, ct); // let the server process one command per tick

                    if (MaxGrants > 0 && grants >= MaxGrants)
                    {
                        Log.Always($"[gm] issued {grants} grants (quota {MaxGrants}) -> logging out");
                        lifecycle.Logout();
                        return;
                    }
                }

                await Task.Delay(1000, ct); // poll interval; idle in place (stay logged in)
            }
        }
        finally
        {
            Log.Always("[gm] stopped");
        }
    }

    // Parse a tell into (kind, value). "grantjob PLD"/"unlock PLD"/"job PLD" -> ("job","PLD");
    // "setcap 50"/"cap 50"/"limit 50" -> ("cap","50"). Returns false for anything else.
    static bool TryParse(string msg, out string kind, out string value)
    {
        kind = ""; value = "";
        var parts = msg.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        string verb = parts[0].ToLowerInvariant();
        value = parts[1];
        if (System.Array.IndexOf(JobWords, verb) >= 0) { kind = "job"; return true; }
        if (System.Array.IndexOf(CapWords, verb) >= 0) { kind = "cap"; return true; }
        if (System.Array.IndexOf(SpellWords, verb) >= 0) { kind = "spell"; return true; }
        return false;
    }
}
