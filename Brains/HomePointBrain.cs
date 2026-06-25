namespace XiHeadless.Brains;

/// Sets the home point at a Home Point crystal — the reliable, game-intended action (examine crystal,
/// select "Set Home Point" = option 1, server calls setHomePoint()). Needed because a headless char's
/// New Character Cutscene never ran setHomePoint(), so death warps it to zone-0 limbo. Run once in a
/// starting city; the char must be IN that city (the crystal is there). Reuses INavigation + IEvents +
/// IPerception + ILifecycle.
public sealed class HomePointBrain(IPerception p, INavigation nav, IZoning zoning, IEvents events, ILifecycle lifecycle) : IBrain
{
    // Starting city zone -> a Home Point crystal position (from the zone's HomePoint NPC !pos).
    static readonly Dictionary<ushort, (float x, float y, float z)> Crystal = new()
    {
        [241] = (-98.588f, 0.001f, -183.416f),   // Windurst Woods, HomePoint#1
    };

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(4000, ct);
        ushort zone = zoning.CurrentZone;
        if (!Crystal.TryGetValue(zone, out var pos))
        {
            Console.WriteLine($"[hp] no Home Point crystal mapped for zone {zone} — be in a mapped city");
            lifecycle.Logout();
            return;
        }

        Console.WriteLine($"[hp] walking to the Home Point crystal in zone {zone} ({pos.x:F0},{pos.z:F0})");
        nav.MoveTo(pos.x, pos.y, pos.z);
        for (int t = 0; t < 60000 && nav.IsMoving && !ct.IsCancellationRequested; t += 200) await Task.Delay(200, ct);
        nav.Stop();

        // Examine the crystal (nearest "Home Point" NPC, else nearest non-mob at the spot) and Set Home Point.
        var crystal = p.Nearest(e => e.Name.Contains("Home Point", StringComparison.OrdinalIgnoreCase) && p.DistanceTo(e.X, e.Z) <= 8f)
                      ?? p.Nearest(e => !e.IsMob && p.DistanceTo(e.X, e.Z) <= 8f);
        if (crystal is null) { Console.WriteLine("[hp] no crystal in reach"); lifecycle.Logout(); return; }

        Console.WriteLine($"[hp] examining crystal 0x{crystal.Id:X} '{crystal.Name}'");
        if (await events.Examine(crystal.Id, ct))
        {
            await events.FinishEvent(1, ct);   // option 1 = SET_HOMEPOINT (homepoint.lua selection)
            Console.WriteLine("[hp] sent Set Home Point (option 1) — verify with a death->warp-to-city");
        }
        else Console.WriteLine("[hp] crystal examine produced no event (NPC interaction blocked?)");

        await Task.Delay(2500, ct);
        lifecycle.Logout();
    }
}
