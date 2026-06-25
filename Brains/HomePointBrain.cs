namespace XiHeadless.Brains;

/// Completes the New Character Cutscene, whose onEventFinish calls setHomePoint() — the headless char
/// never ran it, so its home point is unset (death → zone-0 limbo) AND it stays "in event", which the
/// server's tryStartNextEvent (`if isInEvent() return;`) uses to SUPPRESS all other NPC events (so a
/// crystal Talk gets no event until the cutscene is finished). Fix = blind 0x5B EVENTEND with the
/// zone's cutscene id; the server matches on currentEvent==csid (our parser doesn't see the CS start
/// packet, so we don't wait for it). This is the botBB-proven approach (see project memory 2026-06-24).
public sealed class HomePointBrain(IPerception p, IZoning zoning, IEvents events, ILifecycle lifecycle) : IBrain
{
    // New Character Cutscene csid per starting zone (New_Character_Cutscenes.lua). Finishing it runs
    // setHomePoint() + clears notSeen + unblocks events. (Bastok Markets 235 is two-step: 0 then 7.)
    public static readonly Dictionary<ushort, ushort> CutsceneId = new()
    {
        [234] = 1,   [236] = 1,   [235] = 0,     // Bastok: Mines / Port / Markets(0->7)
        [238] = 531, [241] = 367, [240] = 305,   // Windurst: Waters / Woods / Walls
        [230] = 535, [231] = 503, [232] = 500,   // San d'Oria: Southern / Northern / Port
    };

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(4000, ct);
        ushort zone = zoning.CurrentZone;
        if (!CutsceneId.TryGetValue(zone, out var csid))
        {
            Console.WriteLine($"[hp] no New Character Cutscene csid mapped for zone {zone}");
            lifecycle.Logout();
            return;
        }

        Console.WriteLine($"[hp] finishing New Character Cutscene csid {csid} in zone {zone} (blind EVENTEND -> setHomePoint + unblock events)");
        await events.Finish(p.World.MyId, 0, csid, 0, ct);
        await Task.Delay(2000, ct);
        if (zone == 235) { await events.Finish(p.World.MyId, 0, 7, 0, ct); await Task.Delay(2000, ct); }   // Bastok Markets chains 0 -> 7

        Console.WriteLine("[hp] done — home point should be set (verify: die in the field -> warp to the city, not zone 0)");
        lifecycle.Logout();
    }
}
