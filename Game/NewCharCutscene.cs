namespace XiHeadless.Game;

/// The new-character opening cutscene, per nation's start zones. A freshly-created character lands in its
/// nation's start city and that zone's onZoneIn starts this cutscene; finishing it calls player:setHomePoint()
/// (and gives the adventurer coupon, clears the 'notSeen' flag). BUT the headless client can't SEE the
/// event-start (the 0x32 recv gap), so the auto-completer never finishes it — the char sits frozen "in event"
/// with NO home point set, and on death warps to zone-0 limbo. So BotHost blind-finishes this event by id on
/// zone-in. Event ids are from scripts/quests/hiddenQuests/New_Character_Cutscenes.lua (one per start zone).
public static class NewCharCutscene
{
    static readonly IReadOnlyDictionary<ushort, ushort> ByZone = new Dictionary<ushort, ushort>
    {
        [235] = 0,   // Bastok Markets
        [234] = 1,   // Bastok Mines
        [236] = 1,   // Port Bastok
        [231] = 535, // Northern San d'Oria
        [230] = 503, // Southern San d'Oria
        [232] = 500, // Port San d'Oria
        [238] = 531, // Windurst Waters
        [241] = 367, // Windurst Woods
        [240] = 305, // Port Windurst
    };

    /// The opening-cutscene event id for a start-city zone, or -1 if the zone isn't a start city.
    public static int EventFor(ushort zone) => ByZone.TryGetValue(zone, out var ev) ? ev : -1;

    /// Every KNOWN zone-in/blocking cutscene id a char can carry with no parsed 0x32 (the recv gap): 30000
    /// (OBSERVED LIVE on Zzshekashi — the map server's 0x05B validator names the current event when rejecting
    /// a mismatched EVENTEND: "Event ID mismatch 30000 != 368"), the ROV intro (30035), moghouse (368), and
    /// the start-city intros. A mismatched EVENTEND is REJECTED at packet validation (it never reaches the
    /// zone script), so sweeping is safe; only the matching id ends the event. ONE shared list —
    /// HomePointRoutines (crystal-set pre-clear) and the auto-completer both sweep it.
    public static readonly ushort[] KnownBlockers = { 30000, 30035, 368, 367, 305, 531, 0, 1, 535, 503, 500 };
}
