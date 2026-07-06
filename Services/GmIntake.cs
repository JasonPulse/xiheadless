using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace XiHeadless.Services;

/// In-bot intake for the GM grant bot — the fleet counterpart to RmtIntake. A fleet bot (or an
/// operator) POSTs a grant request here and the GmBrain drains the queue and issues the matching
/// server GM command via chat: kind="job" -> "!grantjob <player> <value>", kind="cap" ->
/// "!setcap <player> <value>". Accepts JSON POST {"player","kind","value"} at /request; GET / serves
/// a tiny console form. Binds all interfaces (same as RmtIntake, for in-pod reachability); if the
/// bind fails the queue still works for seeded requests.
public sealed class GmIntake(int port)
{
    readonly ConcurrentQueue<(string player, string kind, string value)> _q = new();
    HttpListener? _listener;

    public void Start()
    {
        _listener = new HttpListener();
        // LOCALHOST ONLY — unlike RmtIntake (a public gil storefront for players' browsers), this endpoint
        // ISSUES GM JOB/CAP GRANTS, so it must NEVER be network-reachable: anyone who could hit the port
        // would grant themselves jobs/level-caps. Only same-machine fleet bots (and the k8s sidecar) submit
        // requests. If the fleet spans hosts, front this with an authenticated in-cluster channel — do NOT
        // widen this bind.
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try { _listener.Start(); _ = Task.Run(Loop); Console.WriteLine($"[gm-intake] grant console on http://127.0.0.1:{port}/ (localhost only)"); }
        catch (Exception e) { Console.WriteLine($"[gm-intake] bind :{port} failed ({e.Message}); queue still works for seeded requests"); }
    }

    async Task Loop()
    {
        while (_listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { break; }
            try { await Handle(ctx); }
            catch (Exception e) { Console.WriteLine($"[gm-intake] request error: {e.Message}"); try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { } }
        }
    }

    async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        if (req.HttpMethod == "GET")
        {
            await Respond(ctx, 200, "text/html", ConsolePage(null));
            return;
        }
        if (req.HttpMethod == "POST")
        {
            using var sr = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = await sr.ReadToEndAsync();
            var (player, kind, value, jsonClient) = ParseRequest(body, req.ContentType);
            if (IsValid(player, kind, value))
            {
                _q.Enqueue((player, kind, value));
                Console.WriteLine($"[gm-intake] request queued: {kind} '{value}' -> '{player}'");
                if (jsonClient) await Respond(ctx, 202, "application/json", $"{{\"queued\":true,\"player\":\"{player}\",\"kind\":\"{kind}\",\"value\":\"{value}\"}}");
                else await Respond(ctx, 200, "text/html", ConsolePage((player, kind, value)));
                return;
            }
            await Respond(ctx, 400, "text/html", ConsolePage(null, "Need a character name, kind = job|cap, and a value (job short-name/id, or a level 1-99 for cap)."));
            return;
        }
        await Respond(ctx, 405, "text/plain", "method not allowed");
    }

    // A request is valid if it names a player, a known kind, and a non-empty value; for cap the value
    // must additionally parse to 1..99. Final authority is the server command itself.
    public static bool IsValid(string player, string kind, string value)
    {
        if (player.Length == 0 || value.Length == 0) return false;
        if (kind == "job") return true;
        if (kind == "cap") return int.TryParse(value, out var lvl) && lvl is >= 1 and <= 99;
        return false;
    }

    // Parse a request from either an HTML form (application/x-www-form-urlencoded) or a JSON body.
    static (string player, string kind, string value, bool jsonClient) ParseRequest(string body, string? contentType)
    {
        if (contentType is not null && contentType.Contains("json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var pl = (doc.RootElement.GetProperty("player").GetString() ?? "").Trim();
                var kd = (doc.RootElement.GetProperty("kind").GetString() ?? "").Trim().ToLowerInvariant();
                // value may be sent as a JSON string ("PLD"/"50") or a bare number (50) for cap.
                var ve = doc.RootElement.GetProperty("value");
                var vl = (ve.ValueKind == JsonValueKind.Number ? ve.GetInt32().ToString() : ve.GetString() ?? "").Trim();
                return (pl, kd, vl, true);
            }
            catch { return ("", "", "", true); }
        }
        // form-urlencoded: player=Foo&kind=job&value=PLD
        string player = "", kind = "", value = "";
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var key = pair[..eq];
            var val = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')).Trim();
            if (key == "player") player = val;
            else if (key == "kind") kind = val.ToLowerInvariant();
            else if (key == "value") value = val;
        }
        return (player, kind, value, false);
    }

    static async Task Respond(HttpListenerContext ctx, int code, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    // The operator console. `confirmed` set => show a queued banner; `error` set => show a hint.
    static string ConsolePage((string player, string kind, string value)? confirmed, string? error = null)
    {
        string banner = confirmed is { } c
            ? $"<div class='ok'>✅ Queued <b>{WebUtility.HtmlEncode(c.kind)}</b> = <b>{WebUtility.HtmlEncode(c.value)}</b> for <b>{WebUtility.HtmlEncode(c.player)}</b>. The GM bot will issue it shortly.</div>"
            : error is not null ? $"<div class='err'>{WebUtility.HtmlEncode(error)}</div>" : "";
        return $$"""
<!doctype html><html lang=en><head><meta charset=utf-8>
<meta name=viewport content="width=device-width,initial-scale=1"><title>GM Grant Console</title>
<style>
 body{margin:0;font:16px/1.5 system-ui,sans-serif;background:#0e1116;color:#e6edf3;display:flex;min-height:100vh;align-items:center;justify-content:center}
 .card{background:#161b22;border:1px solid #2b333d;border-radius:14px;max-width:440px;width:92%;padding:28px;box-shadow:0 8px 40px #0008}
 h1{margin:0 0 4px;font-size:24px} .sub{color:#9aa6b2;margin:0 0 18px}
 label{display:block;margin:12px 0 4px;font-size:14px;color:#c0cad4}
 input,select{width:100%;box-sizing:border-box;padding:10px;border-radius:8px;border:1px solid #2b333d;background:#0e1116;color:#e6edf3;font-size:15px}
 button{margin-top:18px;width:100%;padding:12px;border:0;border-radius:8px;background:#5b8def;color:#fff;font-size:16px;font-weight:600;cursor:pointer}
 .ok{background:#10331c;border:1px solid #1f6f3a;color:#9be8b4;padding:10px;border-radius:8px;margin-bottom:14px}
 .err{background:#3a1416;border:1px solid #7a2630;color:#f3a3aa;padding:10px;border-radius:8px;margin-bottom:14px}
 .foot{margin-top:16px;font-size:12px;color:#6b7480;text-align:center}
</style></head><body><div class=card>
 <h1>🛡️ GM Grant Console</h1>
 <p class=sub>Grant a job unlock or set a level cap for an online fleet character.</p>
 {{banner}}
 <form method=post action="/">
  <label for=player>Character name</label>
  <input id=player name=player required maxlength=15 placeholder="Target character">
  <label for=kind>Grant kind</label>
  <select id=kind name=kind><option value=job>job unlock</option><option value=cap>level cap</option></select>
  <label for=value>Value (job short-name/id, or level 1-99 for cap)</label>
  <input id=value name=value required placeholder="PLD  (or 50)">
  <button type=submit>Queue grant</button>
 </form>
 <p class=foot>JSON: POST /request {"player","kind":"job|cap","value"}</p>
</div></body></html>
""";
    }

    /// Stop the listener (frees the port). The Loop's GetContextAsync throws and exits.
    public void Stop()
    {
        var l = _listener;
        _listener = null;
        try { l?.Stop(); l?.Close(); } catch { }
    }

    public bool TryDequeue(out (string player, string kind, string value) req) => _q.TryDequeue(out req);
    public void Enqueue(string player, string kind, string value) => _q.Enqueue((player, kind, value)); // seed/test
}
