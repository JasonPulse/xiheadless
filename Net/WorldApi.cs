using System.Net.Http;

namespace XiHeadless.Net;

/// Read-only client for the WORLD server's HTTP API (session counts). Base URL from XIBOT_WORLD_URL,
/// default http://ffxi-world-service (the in-cluster k8s service); for local dev point it at the map host,
/// e.g. http://172.25.75.80:8088. The /api GETs are unauthenticated (only /api/bot/* POSTs are token-gated).
/// Routes live in xiserver/src/world/http_server.cpp:
///   GET /api/sessions -> COUNT(*) FROM accounts_sessions          (total online)
///   GET /api/ips      -> COUNT(DISTINCT client_addr) ...          (unique client IPs)
/// Returns -1 on any failure so callers HOLD (never act on a bad read).
public sealed class WorldApi
{
    static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    readonly string _baseUrl = (Environment.GetEnvironmentVariable("XIBOT_WORLD_URL") ?? "http://ffxi-world-service").TrimEnd('/');

    async Task<int> GetCount(string path, CancellationToken ct)
    {
        try
        {
            var body = await _http.GetStringAsync($"{_baseUrl}{path}", ct);
            return int.TryParse(body.Trim(), out var n) ? n : -1;
        }
        catch (Exception ex) { Log.Info($"[worldapi] {path} read failed: {ex.Message}"); return -1; }
    }

    /// Total online sessions, or -1 if the API can't be read.
    public Task<int> Sessions(CancellationToken ct = default) => GetCount("/api/sessions", ct);
}
