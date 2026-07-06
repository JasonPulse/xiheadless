namespace XiHeadless.Game;

/// A standard general-shop vendor NPC the bot can SELL junk to: one whose shop ALWAYS opens and accepts
/// sells (the LSB `xi.shop.general` vendors — NOT conquest/region-gated `handleRegionalShop` ones, which
/// can return an empty shop). Data only — the zone is DECODED from the npc id (its high bits encode the
/// zone: (id >> 12) & 0xFFF), so a row is just (npcId, position, name) with nothing zone-specific
/// hardcoded. Verified against the server (sql/npc_list.sql + the vendor's onTrigger Lua). Add a row to
/// extend the bot's selling reach — the find-nearest logic picks it up automatically.
public readonly record struct Vendor(uint NpcId, float X, float Y, float Z, string Name)
{
    public ushort Zone => (ushort)((NpcId >> 12) & 0xFFF);
}

/// The vendor registry + "nearest reachable vendor" lookup. Brains never name a shop; they call
/// ShopRoutines.SellNearby, which asks this for the closest vendor to the bot's current zone.
public static class Vendors
{
    // npcId high bits -> zone: 17764456 -> 241 (Windurst Woods), 17760438 -> 240 (Port Windurst), etc.
    public static readonly Vendor[] All =
    {
        new(17764456,   18.7f,  -4.55f, -155.92f,  "Manyny"),         // Windurst Woods (241)
        new(17760438,  -86.084f, -3.0f,  109.223f, "Kucha-Malkobhi"), // Port Windurst (240)
        new(17760405,    6.535f, -4.0f,  105.8f,   "Drozga"),         // Port Windurst (240)
        new(17719352, -144.884f, -5.999f, -11.793f, "Capucine"),      // Southern San d'Oria (230)
        new(17719389,   68.426f,  0.001f, 38.173f,  "Victoire"),      // Southern San d'Oria (230)
        new(17780861,  -35.938f, -6.1f,  -119.684f, "Adelflete"),     // Lower Jeuno (245)
        new(17780859,  -45.568f,  5.149f, -118.03f, "Creepstix"),     // Lower Jeuno (245)
    };

    /// The vendor reachable in the FEWEST zone-hops from currentZone. A vendor in the current zone wins
    /// immediately (0 hops, no travel). Returns null only if none of the registry's zones are reachable.
    public static Vendor? Nearest(ushort currentZone)
    {
        Vendor? best = null;
        int bestHops = int.MaxValue;
        foreach (var v in All)
        {
            if (v.Zone == currentZone) return v;            // already here — unbeatable
            var route = Zonelines.Route(currentZone, v.Zone);
            if (route is null) continue;                    // unreachable from here
            if (route.Count < bestHops) { bestHops = route.Count; best = v; }
        }
        return best;
    }
}
