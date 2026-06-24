using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace XiHeadless.Brains;

/// The RMT storefront the spam URL points at, served in-process by the bot. GET / returns an order
/// page (character name + gil amount); submitting it queues a request the RmtBrain fulfills (mails
/// the gil to that character). Also accepts a JSON POST {"player","amount"} for programmatic use.
/// Binds localhost (no admin); if the bind fails the queue still works (e.g. for a seeded request).
public sealed class RmtIntake(int port)
{
    readonly ConcurrentQueue<(string player, int amount)> _q = new();
    HttpListener? _listener;

    public void Start()
    {
        _listener = new HttpListener();
        // Bind ALL interfaces (0.0.0.0), not just loopback — inside a k8s pod the Service/ingress
        // reaches the bot from another address, so localhost would be unreachable. ('+' wildcard;
        // on Linux/macOS .NET it maps to IPAddress.Any with no admin/URL-ACL needed for a high port.)
        _listener.Prefixes.Add($"http://+:{port}/");
        try { _listener.Start(); _ = Task.Run(Loop); Console.WriteLine($"[intake] storefront on http://0.0.0.0:{port}/ (all interfaces)"); }
        catch (Exception e) { Console.WriteLine($"[intake] bind :{port} failed ({e.Message}); queue still works for seeded requests"); }
    }

    async Task Loop()
    {
        while (_listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { break; }
            try { await Handle(ctx); }
            catch (Exception e) { Console.WriteLine($"[intake] request error: {e.Message}"); try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { } }
        }
    }

    async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        if (req.HttpMethod == "GET")
        {
            await Respond(ctx, 200, "text/html", StorePage(null));
            return;
        }
        if (req.HttpMethod == "POST")
        {
            using var sr = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = await sr.ReadToEndAsync();
            var (player, amount, jsonClient) = ParseOrder(body, req.ContentType);
            if (player.Length > 0 && amount is > 0 and <= 999999999)
            {
                _q.Enqueue((player, amount));
                Console.WriteLine($"[intake] order queued: {amount} gil -> '{player}'");
                if (jsonClient) await Respond(ctx, 202, "application/json", $"{{\"queued\":true,\"player\":\"{player}\",\"amount\":{amount}}}");
                else await Respond(ctx, 200, "text/html", StorePage((player, amount)));
                return;
            }
            await Respond(ctx, 400, "text/html", StorePage(null, "Enter a character name and a gil amount between 1 and 999,999,999."));
            return;
        }
        await Respond(ctx, 405, "text/plain", "method not allowed");
    }

    // Parse an order from either an HTML form (application/x-www-form-urlencoded) or a JSON body.
    static (string player, int amount, bool jsonClient) ParseOrder(string body, string? contentType)
    {
        if (contentType is not null && contentType.Contains("json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var pl = doc.RootElement.GetProperty("player").GetString() ?? "";
                return (pl.Trim(), doc.RootElement.GetProperty("amount").GetInt32(), true);
            }
            catch { return ("", 0, true); }
        }
        // form-urlencoded: player=Foo&amount=1000000
        string player = ""; int amount = 0;
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var key = pair[..eq];
            var val = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')).Trim();
            if (key == "player") player = val;
            else if (key == "amount") int.TryParse(val, out amount);
        }
        return (player, amount, false);
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

    // The storefront. `confirmed` set => show an order-received banner; `error` set => show a hint.
    static string StorePage((string player, int amount)? confirmed, string? error = null)
    {
        string banner = confirmed is { } c
            ? $"<div class='ok'>✅ Order received! Delivering <b>{c.amount:N0} gil</b> to <b>{WebUtility.HtmlEncode(c.player)}</b> now — check your Mog House delivery box.</div>"
            : error is not null ? $"<div class='err'>{WebUtility.HtmlEncode(error)}</div>" : "";
        return $$"""
<!doctype html><html lang=en><head><meta charset=utf-8>
<meta name=viewport content="width=device-width,initial-scale=1"><title>Network Gnomes Gil</title>
<style>
 body{margin:0;font:16px/1.5 system-ui,sans-serif;background:#0e1116;color:#e6edf3;display:flex;min-height:100vh;align-items:center;justify-content:center}
 .card{background:#161b22;border:1px solid #2b333d;border-radius:14px;max-width:440px;width:92%;padding:28px;box-shadow:0 8px 40px #0008}
 h1{margin:0 0 4px;font-size:24px} .sub{color:#9aa6b2;margin:0 0 18px}
 .pkgs{display:flex;gap:8px;margin:0 0 18px} .pkg{flex:1;text-align:center;background:#0e1116;border:1px solid #2b333d;border-radius:10px;padding:10px 6px;font-size:14px}
 .pkg b{display:block;font-size:16px;color:#f0c14b}
 label{display:block;margin:12px 0 4px;font-size:14px;color:#c0cad4}
 input{width:100%;box-sizing:border-box;padding:10px;border-radius:8px;border:1px solid #2b333d;background:#0e1116;color:#e6edf3;font-size:15px}
 button{margin-top:18px;width:100%;padding:12px;border:0;border-radius:8px;background:#f0c14b;color:#1a1a1a;font-size:16px;font-weight:600;cursor:pointer}
 .ok{background:#10331c;border:1px solid #1f6f3a;color:#9be8b4;padding:10px;border-radius:8px;margin-bottom:14px}
 .err{background:#3a1416;border:1px solid #7a2630;color:#f3a3aa;padding:10px;border-radius:8px;margin-bottom:14px}
 .foot{margin-top:16px;font-size:12px;color:#6b7480;text-align:center}
</style></head><body><div class=card>
 <h1>🪙 Network Gnomes Gil</h1>
 <p class=sub>Cheapest gil in Vana'diel — instant in-game delivery to your Mog House.</p>
 {{banner}}
 <div class=pkgs><div class=pkg><b>100k</b>$2</div><div class=pkg><b>1M</b>$5</div><div class=pkg><b>5M</b>$20</div></div>
 <form method=post action="/">
  <label for=player>Character name</label>
  <input id=player name=player required maxlength=15 placeholder="Your character">
  <label for=amount>Gil amount</label>
  <input id=amount name=amount type=number min=1 max=999999999 value=1000000 required>
  <button type=submit>Place order</button>
 </form>
 <p class=foot>Delivery is mailed to your character's Mog House delivery box.</p>
</div></body></html>
""";
    }

    public bool TryDequeue(out (string player, int amount) req) => _q.TryDequeue(out req);
    public void Enqueue(string player, int amount) => _q.Enqueue((player, amount)); // seed/test
}
