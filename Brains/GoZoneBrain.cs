namespace XiHeadless.Brains;

/// Dev/utility: travel to a named zone (env GO_ZONE, default "North Gustaberg") via the shared zoning
/// route stack, then log out — used to park a throwaway char in a specific zone (e.g. a field with
/// monsters) so the Vellichor renderer can log in there afterward and validate live model rendering.
/// Pure navigation + a clean logout; chooses only the destination.
public sealed class GoZoneBrain(IZoning zoning, ILifecycle lifecycle) : IBrain
{
    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(3000, ct);
        string target = System.Environment.GetEnvironmentVariable("GO_ZONE") ?? "North Gustaberg";
        Log.Always($"[gozone] traveling to '{target}' (from zone {zoning.CurrentZone})");
        bool ok = await zoning.GoTo(target, ct);
        Log.Always($"[gozone] arrived={ok} now in zone {zoning.CurrentZone}; logging out so the renderer can take over");
        await Task.Delay(2500, ct);
        lifecycle.Logout();
    }
}
