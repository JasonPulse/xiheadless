namespace XiHeadless.Game;

/// One zone-line doorway: walk to (TriggerX,TriggerZ) in the From zone, send 0x5E
/// with RectId, and the server moves you to the To zone. The trigger point is the
/// arrival position of the REVERSE line (where you land in From when coming from To),
/// which is on the navmesh by construction. Values verified from sql/zonelines.sql.
public readonly record struct ZoneLine(uint RectId, ushort From, ushort To, float TriggerX, float TriggerY, float TriggerZ);

/// Whole-world zone-connectivity graph, generated from the server SQL (see ZoneGraph / `gengraph`).
/// BFS finds a multi-hop route; names resolve so a brain can just GoTo("Windurst Woods").
public static class Zonelines
{
    public static readonly ZoneLine[] All = ZoneGraph.Lines;

    const ushort MiscAh = 0x200;       // ZONEMISC MISC_AH bit: zone allows auction-house use
    const ushort MiscMogMenu = 0x20;   // ZONEMISC MISC_MOGMENU: Explorer/Nomad Moogle (job change w/o Mog House)

    // Canonical name (and a spaces<->underscores, case-insensitive variant) -> zone id.
    static readonly Dictionary<string, ushort> _byName = BuildNames();
    static readonly Dictionary<ushort, (string name, ushort misc)> _info =
        ZoneGraph.Zones.ToDictionary(z => z.id, z => (z.name, z.misc));

    static Dictionary<string, ushort> BuildNames()
    {
        var m = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        foreach (var z in ZoneGraph.Zones) { m[z.name] = z.id; m[z.name.Replace('_', ' ')] = z.id; }
        return m;
    }

    /// Resolve a zone name ("Windurst Woods" or "Windurst_Woods", any case) to its id, or null.
    public static ushort? Resolve(string name) => _byName.TryGetValue(name.Trim(), out var id) ? id : null;
    public static string Name(ushort id) => _info.TryGetValue(id, out var i) ? i.name : id.ToString();
    public static bool HasAuctionHouse(ushort id) => _info.TryGetValue(id, out var i) && (i.misc & MiscAh) != 0;
    // True if the zone has an Explorer/Nomad Moogle, so job change works without entering a Mog House.
    public static bool HasMogMenu(ushort id) => _info.TryGetValue(id, out var i) && (i.misc & MiscMogMenu) != 0;

    static readonly Dictionary<ushort, List<ZoneLine>> _byFrom = Build();

    static Dictionary<ushort, List<ZoneLine>> Build()
    {
        var m = new Dictionary<ushort, List<ZoneLine>>();
        foreach (var z in All)
        {
            if (!m.TryGetValue(z.From, out var list)) m[z.From] = list = new();
            list.Add(z);
        }
        return m;
    }

    /// BFS the zone graph from -> to, returning the sequence of zone-lines to traverse
    /// (each hop walked + zoned in order). Empty if already there; null if unreachable.
    public static List<ZoneLine>? Route(ushort from, ushort to)
    {
        if (from == to) return new();
        var prev = new Dictionary<ushort, ZoneLine>();
        var seen = new HashSet<ushort> { from };
        var q = new Queue<ushort>();
        q.Enqueue(from);
        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            if (!_byFrom.TryGetValue(cur, out var edges)) continue;
            foreach (var e in edges)
            {
                if (!seen.Add(e.To)) continue;
                prev[e.To] = e;
                if (e.To == to)
                {
                    var path = new List<ZoneLine>();
                    for (ushort z = to; z != from; z = prev[z].From) path.Add(prev[z]);
                    path.Reverse();
                    return path;
                }
                q.Enqueue(e.To);
            }
        }
        return null;
    }
}
