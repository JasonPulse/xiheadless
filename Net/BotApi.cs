using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace XiHeadless.Net;

/// Client for the server's bot API (gil-grant). Auth + endpoint are DEPLOYMENT SECRETS (like the
/// password), not brain config: XIBOT_API_URL (base, e.g. http://map-host:port) and XIBOT_API_TOKEN
/// (= server settings.network.MAP_BOT_API_TOKEN). Until both are set the call is a no-op STUB that
/// logs what it would do, so the rest of the RMT loop is testable before the server side is live.
///
/// Contract: POST {url}/api/bot/grant_gil, header X-Bot-Token, body {player,amount,reason};
/// 202 Accepted = queued; 400 bad amount (1..999999999); 401 bad token; 404 disabled; 503 no token.
public sealed class BotApi : IGilGrant
{
    static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    readonly string? _baseUrl = Environment.GetEnvironmentVariable("XIBOT_API_URL")?.TrimEnd('/');
    readonly string? _token = Environment.GetEnvironmentVariable("XIBOT_API_TOKEN");

    public async Task<bool> Grant(string player, int amount, string reason, CancellationToken ct = default)
    {
        if (amount < 1 || amount > 999999999)
        {
            Log.Info($"[botapi] grant_gil refused: amount {amount} out of range (1..999999999)");
            return false;
        }
        if (string.IsNullOrEmpty(_baseUrl) || string.IsNullOrEmpty(_token))
        {
            Log.Info($"[botapi] grant_gil STUB (set XIBOT_API_URL + XIBOT_API_TOKEN to enable): would grant {amount} gil to '{player}' (reason={reason})");
            return false;
        }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/bot/grant_gil")
            {
                Content = JsonContent.Create(new { player, amount, reason })
            };
            req.Headers.Add("X-Bot-Token", _token);
            var resp = await _http.SendAsync(req, ct);
            bool ok = resp.StatusCode == HttpStatusCode.Accepted; // 202
            Log.Info($"[botapi] grant_gil '{player}' +{amount} (reason={reason}) -> {(int)resp.StatusCode} {(ok ? "queued" : resp.ReasonPhrase)}");
            return ok;
        }
        catch (Exception e)
        {
            Log.Info($"[botapi] grant_gil '{player}' +{amount} FAILED: {e.Message}");
            return false;
        }
    }
}
