namespace XiHeadless.Brains;

/// Completes the New Character Cutscene — the server-side event that calls setHomePoint() (and gives
/// the Adventurer's Coupon), which a headless-created char never ran, leaving its home point unset so
/// death recovery dumps it in zone-0 limbo. The cutscene is per starting zone (New_Character_Cutscenes
/// .lua); on zone-in with notSeen==1 the server starts the zone's CS, and finishing that event sets the
/// home point. We finish whichever event the server actually presents (robust across nations), falling
/// back to the per-zone id. Run once per fresh char in its starting zone (ideally from char creation).
public sealed class HomePointBrain(IPerception p, IZoning zoning, IEvents events) : IBrain
{
    // Starting zone -> New Character Cutscene event id (from New_Character_Cutscenes.lua).
    static readonly Dictionary<ushort, ushort> NewCharCs = new()
    {
        [235] = 0,   [234] = 1,   [236] = 1,     // Bastok: Markets / Mines / Port
        [238] = 531, [241] = 367, [240] = 305,   // Windurst: Waters / Woods / (Walls/Port)
    };

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(4000, ct);   // let zone-in + the CS trigger (onZoneIn starts it if notSeen==1)
        ushort zone = zoning.CurrentZone;

        // Prefer the event the server actually started (the real CS); else the per-zone fallback id.
        ushort cs = events.EventActive ? events.CurrentEventId
                  : NewCharCs.TryGetValue(zone, out var mapped) ? mapped : ushort.MaxValue;
        if (cs == ushort.MaxValue && !events.EventActive)
        {
            Console.WriteLine($"[hp] zone {zone}: no new-char cutscene active and no mapping — already done?");
            return;
        }

        Console.WriteLine($"[hp] finishing New Character Cutscene event {cs} in zone {zone} (sets home point)");
        await events.Finish(p.World.MyId, 0, cs, 0, ct);
        await Task.Delay(3000, ct);   // let the cutscene's setHomePoint + setPos land
        Console.WriteLine($"[hp] done — home point should now be set (zone {zoning.CurrentZone})");
    }
}
