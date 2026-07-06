namespace XiHeadless.Game;

/// Starting nation — selects which leveling path to follow.
public enum Nation : byte { SanDoria, Bastok, Windurst }

/// One leg of a leveling path: hunt in `Zone` while the character is roughly `Min`..`Max`. Legs are ordered
/// low->high within a nation. `Zone` is a Zonelines name (resolved to a zone id for travel). Bands are tuned
/// for SOLO hunting — the character sits a few levels ABOVE the zone's mobs so there's always EasyPrey to
/// kill (not the party-EvenMatch framing of the guide). Source: BG-Wiki New Player Leveling Guide + the
/// standard open-world, WALKABLE regional progression; all three nations converge at Qufim Island via Jeuno.
/// A leg of a leveling path. Camp(X,Y,Z) is an optional dense-spawn anchor (the centroid of a tight mob
/// cluster from the server spawn data) — the hunter parks there and sweeps locally instead of roaming the
/// whole zone, which matters a lot in sparse/slow-respawn zones. (0,0,0) = no anchor -> roam wide.
public readonly record struct HuntLeg(string Zone, byte Min, byte Max, float CampX = 0, float CampY = 0, float CampZ = 0);

public static class HuntZones
{
    // Notorious Monsters — NEVER engage these. An NM cons by LEVEL like any mob (e.g. Sylvestre /checks as a
    // level-17 Decent Challenge), but it has NM-inflated HP/stats that the con can't reflect, so a leveling bot
    // that trusts con walks into an unwinnable fight and dies (and FFXI hate can't be shaken once engaged, so
    // there's no recovery). Con is fundamentally blind to NM-ness — the only reliable filter is the server's NM
    // name list. Sourced from xiserver scripts/zones/*/mobs + sql/mob_pools. Matched as a case-insensitive
    // substring against the entity name, so list the display name (spaces, not underscores). All bots skip these.
    public static readonly string[] NmNames =
    {
        // Buburimu_Peninsula (118). Sylvestre was WRONGLY listed here — mob_spawn_points shows 57 spawns at
        // lv15-19 (the zone's main in-band leveling population; one was field-killed by the duo). Bull Dhalmel
        // is 47 spawns lv20-24 — a normal (aggressive) population and the CUP dropper; the con gate handles it.
        "Backoo", "Buburimboo", "Helldiver", "Ketos", "Wake Warder Wanda",
    };

    // Each nation's walkable chain: starting dunes -> gateway -> "dunes-tier" -> 20s zone -> Qufim.
    public static readonly IReadOnlyDictionary<Nation, HuntLeg[]> Paths = new Dictionary<Nation, HuntLeg[]>
    {
        [Nation.Windurst] = new[]
        {
            new HuntLeg("West_Sarutabaruta",     1, 12),
            new HuntLeg("Tahrongi_Canyon",       9, 18, 120.12f, -10.13f, -155.64f),  // dense camp: 11 mobs lv7-12 in 50y
            new HuntLeg("Buburimu_Peninsula",   99, 30, -411.36f, -8.92f, -205.48f),  // PINNED (Min 99): NOT for WHM leveling. A lvl-9 WHM here gets aggro'd + dies, and a 9-vs-17 kill earns ~0 exp (level-gap penalty) — proven twice. The WHM levels via a LEVEL-SYNC duo in Tahrongi instead (at-level fights = real exp). Buburimu is reserved for the UNSYNCED lv18 WAR to farm the subjob items (Cup/Bloody Robe + Rabbit Tail off Mighty_Rarab) as a separate phase, skipping the NMs.
            new HuntLeg("Meriphataud_Mountains",24, 34),
            new HuntLeg("Sauromugue_Champaign", 32, 42),
            new HuntLeg("Qufim_Island",         38, 50),
        },
        [Nation.SanDoria] = new[]
        {
            new HuntLeg("West_Ronfaure",         1, 12),
            new HuntLeg("La_Theine_Plateau",     9, 18),
            new HuntLeg("Valkurm_Dunes",        17, 26),
            new HuntLeg("Jugner_Forest",        24, 34),
            new HuntLeg("Batallia_Downs",       32, 42),
            new HuntLeg("Qufim_Island",         38, 50),
        },
        [Nation.Bastok] = new[]
        {
            new HuntLeg("South_Gustaberg",       1, 12),
            new HuntLeg("Konschtat_Highlands",   9, 18),
            new HuntLeg("Pashhow_Marshlands",   17, 26),
            new HuntLeg("Rolanberry_Fields",    24, 34),
            new HuntLeg("Qufim_Island",         34, 50),
        },
    };

    /// The zone a level-`level` character of `nation` should hunt in: the furthest leg they've reached
    /// (the highest-`Min` leg with `Min <= level`), clamped to the path. This is the "where to be now" pick;
    /// the hunting routine still advances early if a zone runs dry of killable mobs.
    public static string ZoneFor(Nation nation, int level)
    {
        var path = Paths[nation];
        var pick = path[0];
        foreach (var leg in path)
            if (leg.Min <= level) pick = leg;
        return pick.Zone;
    }

    /// The next leg after the one we're currently hunting (for "this zone ran dry, move on"). Returns null
    /// at the end of the path. Matches by the current zone name; unknown zone -> first leg.
    public static string? NextZoneAfter(Nation nation, string currentZone)
    {
        var path = Paths[nation];
        for (int i = 0; i < path.Length; i++)
            if (string.Equals(path[i].Zone, currentZone, StringComparison.OrdinalIgnoreCase))
                return i + 1 < path.Length ? path[i + 1].Zone : null;
        return path[0].Zone;
    }
}
