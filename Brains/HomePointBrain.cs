namespace XiHeadless.Brains;

/// Sets the home point at a Home Point crystal — the reliable, game-intended action (examine crystal,
/// select "Set Home Point" = option 1, server calls setHomePoint()). Used as a workaround when the New
/// Character Cutscene can't run (e.g. a headless char whose notSeen was never set), so death would
/// otherwise warp to zone-0 limbo. Run once in a starting city; the char must be IN that city. Pure
/// navigation + a deliberate event (brain activity); the home point set itself is server-side.
public sealed class HomePointBrain(IPerception p, INavigation nav, IZoning zoning, IEvents events, ILifecycle lifecycle) : IBrain
{
    // Starting city zone -> a Home Point crystal (npc id + position + its event csid). We can't receive
    // the event-start packet (recv gap for 0x32), so we Talk to trigger the crystal event server-side,
    // then blind-finish it with the crystal's csid: onEventFinish[csid] with option 1 (SET_HOMEPOINT)
    // calls setHomePoint(). csid is per-crystal (Windurst_Woods/npcs/HomePoint#N.lua).
    static readonly Dictionary<ushort, (uint npcId, float x, float y, float z, ushort csid)> Crystal = new()
    {
        [241] = (17764538u, -92f, -5f, 62f, 8702),   // Windurst Woods, HomePoint#3 (csid 8702)
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

        // On login at level >= 3, ROV mission 1-01 (event 30035) fires on zone-in and leaves the char
        // "in event", which BLOCKS the crystal (and everything else). We don't receive the event-start
        // (recv gap for 0x32), so the auto-completer can't see it — blind-finish 30035 to free the char.
        // If the new-char CS (367) was queued behind ROV, finishing 30035 starts it; finishing 367 then
        // calls setHomePoint() directly (bonus path).
        Console.WriteLine("[hp] clearing blocking ROV cutscene (blind EVENTEND 30035)");
        await events.Finish(p.World.MyId, 0, 30035, 0, ct);
        await Task.Delay(2000, ct);
        await events.Finish(p.World.MyId, 0, 367, 0, ct);   // new-char CS if it was queued behind ROV
        await Task.Delay(2000, ct);

        Console.WriteLine($"[hp] walking to the Home Point crystal in zone {zone} ({pos.x:F0},{pos.z:F0})");
        nav.MoveTo(pos.x, pos.y, pos.z);
        // Walk as close as the navmesh allows (3y target) or until the path is exhausted.
        for (int t = 0; t < 150000 && p.DistanceTo(pos.x, pos.z) > 3f && nav.IsMoving && !ct.IsCancellationRequested; t += 200) await Task.Delay(200, ct);
        nav.Stop();
        Console.WriteLine($"[hp] arrived at ({p.World.X:F0},{p.World.Z:F0}), {p.DistanceTo(pos.x, pos.z):F0}y from crystal");

        // We don't receive the event-start (0x32) packet, so don't wait for it. Talk to trigger the
        // crystal event server-side, then blind-finish with its csid + option 1 (SET_HOMEPOINT). The
        // server's onEventFinish[csid] checks csid==currentEvent and choice(option&0xFF)==1 -> setHomePoint.
        ushort idx = (ushort)(pos.npcId & 0xFFF);
        for (int attempt = 1; attempt <= 3 && !ct.IsCancellationRequested; attempt++)
        {
            Console.WriteLine($"[hp] attempt {attempt}: Talk crystal 0x{pos.npcId:X} idx {idx}, then blind EVENTEND csid {pos.csid} option 1");
            await events.Examine(pos.npcId, ct);                  // sends Talk (the EventActive wait will time out — expected)
            await events.Finish(pos.npcId, idx, pos.csid, 1, ct); // EVENTEND: SET_HOMEPOINT
            await Task.Delay(1500, ct);
        }
        Console.WriteLine("[hp] sent Set Home Point — verify with a death -> warp to the city (241), not zone 0");

        await Task.Delay(2500, ct);
        lifecycle.Logout();
    }
}
