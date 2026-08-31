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
    // (An NmNames blocklist used to live here. REMOVED per the hard rule: con is the SOLE arbiter — no mob
    // name allow/block lists, explicitly including NM lists. It also mis-fired as substrings: "Helldiver"
    // blanket-skipped Qufim's normal helldiver leveling population, and Sylvestre had been wrongly listed.
    // A bot that loses to an even-con NM recovers through the normal death path; never blacklist by name.)

    // Each nation's walkable chain: starting dunes -> gateway -> "dunes-tier" -> 20s zone -> Qufim.
    public static readonly IReadOnlyDictionary<Nation, HuntLeg[]> Paths = new Dictionary<Nation, HuntLeg[]>
    {
        // Camp(X,Y,Z) on the 10+ legs = the centroid of the densest LEVEL-BAND spawn cluster from the server
        // mob_spawn_points.sql (mobs whose level overlaps the leg band, densest within 40y). These are the
        // PARTY anchor: a party day landed at the zone-IN and roamed empty all session (Konschtat mobs sit at
        // (-404,-153) but Gralou camped the zone-in at (120,-637), ~700y past the puller's reach — 0 kills every
        // party day, user 2026-08-27). Centroids sit on walkable spawn ground. Nursery legs (band 1-12) stay
        // anchorless — party days require lvl>10, so HuntZonePlan never routes a party there.
        [Nation.Windurst] = new[]
        {
            new HuntLeg("West_Sarutabaruta",     1, 12),
            new HuntLeg("Tahrongi_Canyon",       9, 18, 120.12f, -10.13f, -155.64f),  // dense camp: 11 mobs lv7-12 in 50y
            new HuntLeg("Buburimu_Peninsula",   99, 30, -411.36f, -8.92f, -205.48f),  // PINNED (Min 99): NOT for WHM leveling. A lvl-9 WHM here gets aggro'd + dies, and a 9-vs-17 kill earns ~0 exp (level-gap penalty) — proven twice. The WHM levels via a LEVEL-SYNC duo in Tahrongi instead (at-level fights = real exp). Buburimu is reserved for the UNSYNCED lv18 WAR to farm the subjob items (Cup/Bloody Robe + Rabbit Tail off Mighty_Rarab) as a separate phase, skipping the NMs.
            new HuntLeg("Meriphataud_Mountains",24, 34, -307.8f, 18.0f, 410.9f),
            new HuntLeg("Sauromugue_Champaign", 32, 42, 218.8f, 32.6f, 256.0f),
            new HuntLeg("Qufim_Island",         38, 50, -238.9f, -19.8f, 305.0f),
        },
        [Nation.SanDoria] = new[]
        {
            new HuntLeg("West_Ronfaure",         1, 12),
            new HuntLeg("La_Theine_Plateau",     9, 18, -260.9f, 7.9f, 182.9f),
            new HuntLeg("Valkurm_Dunes",        17, 26, 322.2f, -7.9f, 80.4f),
            new HuntLeg("Jugner_Forest",        24, 34, 59.9f, 0.5f, 8.8f),
            new HuntLeg("Batallia_Downs",       32, 42, -352.3f, -15.3f, 242.7f),
            new HuntLeg("Qufim_Island",         38, 50, -238.9f, -19.8f, 305.0f),
        },
        [Nation.Bastok] = new[]
        {
            new HuntLeg("South_Gustaberg",       1, 12),
            new HuntLeg("Konschtat_Highlands",   9, 18, -404.3f, -5.7f, -153.3f),
            new HuntLeg("Pashhow_Marshlands",   17, 26, -180.0f, 24.4f, 128.0f),
            new HuntLeg("Rolanberry_Fields",    24, 34, -180.6f, 1.6f, -349.8f),
            new HuntLeg("Qufim_Island",         34, 50, -238.9f, -19.8f, 305.0f),
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

    /// The curated camp anchor (X,Z) for `nation`'s leg in `zone`, or null when that leg has none (nursery
    /// legs, or a zone not on this path). The PARTY day uses it as the formation MeetSpot so the group anchors
    /// on real spawn ground instead of the zone-in edge.
    public static (float x, float z)? CampFor(Nation nation, string zone)
    {
        if (!Paths.TryGetValue(nation, out var path)) return null;
        foreach (var leg in path)
            if (string.Equals(leg.Zone, zone, StringComparison.OrdinalIgnoreCase))
                return (leg.CampX == 0 && leg.CampZ == 0) ? null : (leg.CampX, leg.CampZ);
        return null;
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
