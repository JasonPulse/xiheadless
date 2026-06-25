namespace XiHeadless.Brains;

/// Sets the home point at a Home Point crystal — the reliable, game-intended action (examine crystal,
/// select "Set Home Point" = option 1, server calls setHomePoint()). Needed because a headless char's
/// New Character Cutscene never ran setHomePoint(), so death warps it to zone-0 limbo. Run once in a
/// starting city; the char must be IN that city (the crystal is there). Reuses INavigation + IEvents +
/// IPerception + ILifecycle.
public sealed class HomePointBrain(IPerception p, INavigation nav, IZoning zoning, IEvents events, ILifecycle lifecycle) : IBrain
{
    // Starting city zone -> a Home Point crystal (npc id + position). Crystals are static objects that
    // don't stream as entities, so we examine by the known npc id (IEvents derives the target index).
    static readonly Dictionary<ushort, (uint npcId, float x, float y, float z)> Crystal = new()
    {
        [241] = (17764538u, -92f, -5f, 62f),   // Windurst Woods, HomePoint#3 (closest to the Mog House spawn)
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
        // Wait until we're beside the crystal (the path can be long/winding) or the path is exhausted.
        for (int t = 0; t < 150000 && p.DistanceTo(pos.x, pos.z) > 6f && nav.IsMoving && !ct.IsCancellationRequested; t += 200) await Task.Delay(200, ct);
        nav.Stop();
        Console.WriteLine($"[hp] arrived at ({p.World.X:F0},{p.World.Z:F0}), {p.DistanceTo(pos.x, pos.z):F0}y from crystal — examining 0x{pos.npcId:X}");

        // Examine the crystal by its known npc id (it doesn't stream) and select Set Home Point.
        if (await events.Examine(pos.npcId, ct))
        {
            await events.FinishEvent(1, ct);   // option 1 = SET_HOMEPOINT (homepoint.lua selection)
            Console.WriteLine("[hp] sent Set Home Point (option 1) — verify with a death->warp-to-city");
        }
        else Console.WriteLine("[hp] crystal examine produced no event");

        await Task.Delay(2500, ct);
        lifecycle.Logout();
    }
}
