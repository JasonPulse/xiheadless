using System.Net.Http;

namespace XiHeadless.Net;

/// Read-only client for the WORLD server's HTTP API (session counts). The in-cluster k8s service name is
/// fixed, so it's a const (behavior/endpoints are CODE, not env). Routes live in
/// xiserver/src/world/http_server.cpp:
///   GET /api/sessions -> COUNT(*) FROM accounts_sessions          (total online)
///   GET /api/ips      -> COUNT(DISTINCT client_addr) ...          (unique client IPs)
/// Returns -1 on any failure so callers HOLD (never act on a bad read).
public sealed class WorldApi
{
    const string BaseUrl = "http://ffxi-world:8088";   // in-cluster world service (svc 'ffxi-world', port 8088)
    static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    async Task<int> GetCount(string path, CancellationToken ct)
    {
        try
        {
            var body = await _http.GetStringAsync($"{BaseUrl}{path}", ct);
            return int.TryParse(body.Trim(), out var n) ? n : -1;
        }
        catch (Exception ex) { Log.Info($"[worldapi] {path} read failed: {ex.Message}"); return -1; }
    }

    /// Total online sessions, or -1 if the API can't be read.
    public Task<int> Sessions(CancellationToken ct = default) => GetCount("/api/sessions", ct);
}
