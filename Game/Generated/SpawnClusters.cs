namespace XiHeadless.Game;

/// GENERATED from xiserver sql/mob_spawn_points.sql (this fork carries min/max level per spawn).
/// 80y cluster centroids per (zone, mob name, level band) — the data-driven answer to "where do
/// level-appropriate mobs live" for EVERY zone. Regenerate with tools/gen_spawn_clusters.py.
///
/// DATA LIVES IN res/spawn_clusters.csv, NOT a code initializer (fleet OOM, 2026-07-09): the old
/// 38k-element array initializer compiled to one colossal .cctor whose JIT transiently ate hundreds
/// of MB on first touch (the first roam beat) — fine on a dev box, an instant cgroup OOM kill in a
/// 384Mi pod. Parsing the CSV allocates only the ~5MB the data actually needs.
public static class SpawnClusters
{
    public readonly record struct Cluster(ushort Zone, string Name, byte Min, byte Max, float X, float Z, byte Count);

    /// Cluster centroids in `zone` for mobs whose level band overlaps [lvlLo, lvlHi], plus any mob
    /// whose name contains one of extraNames (droppers, any level). Steering data only — the live
    /// /check con at engage time remains the sole arbiter of what to fight.
    public static IEnumerable<(float x, float z)> For(ushort zone, int lvlLo, int lvlHi, params string[] extraNames) =>
        All.Where(c => c.Zone == zone && ((c.Min <= lvlHi && c.Max >= lvlLo)
                || extraNames.Any(n => c.Name.Contains(n, StringComparison.OrdinalIgnoreCase))))
           .Select(c => (c.X, c.Z));

    /// Clusters of the NAMED mobs only (any level) — where a farm's droppers actually live.
    public static IEnumerable<(float x, float z)> ForNames(ushort zone, params string[] names) =>
        All.Where(c => c.Zone == zone && names.Any(n => c.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
           .Select(c => (c.X, c.Z));

    public static readonly Cluster[] All = LoadCsv();

    static Cluster[] LoadCsv()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "res", "spawn_clusters.csv");
        var lines = File.ReadAllLines(path);
        var all = new Cluster[lines.Length];
        int n = 0;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var line in lines)
        {
            var f = line.Split(',');
            if (f.Length != 7) continue;
            all[n++] = new Cluster(ushort.Parse(f[0]), f[1], byte.Parse(f[2]), byte.Parse(f[3]),
                                   float.Parse(f[4], inv), float.Parse(f[5], inv), byte.Parse(f[6]));
        }
        if (n != all.Length) Array.Resize(ref all, n);
        return all;
    }

    /// Every raw spawn POINT in `zone` (res/spawn_points.csv: zone,x,z; tools/gen_spawn_clusters.py), loaded on
    /// first use. For clearance checks (where the party camps), where 80y centroids are far too coarse.
    public static IReadOnlyList<(float x, float z)> PointsIn(ushort zone) =>
        (_points ??= LoadPoints()).TryGetValue(zone, out var pts) ? pts : Array.Empty<(float, float)>();

    static Dictionary<ushort, (float x, float z)[]>? _points;

    static Dictionary<ushort, (float x, float z)[]> LoadPoints()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var byZone = new Dictionary<ushort, List<(float, float)>>();
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "res", "spawn_points.csv")))
        {
            var f = line.Split(',');
            if (f.Length != 3 || !ushort.TryParse(f[0], out var z)) continue;
            if (!byZone.TryGetValue(z, out var l)) byZone[z] = l = new();
            l.Add((float.Parse(f[1], inv), float.Parse(f[2], inv)));
        }
        return byZone.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }
}
