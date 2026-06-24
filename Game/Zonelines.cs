namespace XiHeadless.Game;

/// One zone-line doorway: walk to (TriggerX,TriggerZ) in the From zone, send 0x5E
/// with RectId, and the server moves you to the To zone. The trigger point is the
/// arrival position of the REVERSE line (where you land in From when coming from To),
/// which is on the navmesh by construction. Values verified from sql/zonelines.sql.
public readonly record struct ZoneLine(uint RectId, ushort From, ushort To, float TriggerX, float TriggerY, float TriggerZ);

/// Static zone-connectivity graph for the Bastok region (the zones we have navmeshes for).
/// Add rows to extend the bot's reachable world; BFS finds a multi-hop route.
public static class Zonelines
{
    // From\To, RectId of the From->To line, and the trigger point in From (= reverse line's toPos).
    public static readonly ZoneLine[] All =
    {
        new(812267130, 235, 234, -201.904f, 1.928f, -194.828f), // Bastok Markets -> Bastok Mines
        new(845756026, 234, 235, -104.018f, 9.359f,  81.411f),  // Bastok Mines   -> Bastok Markets
        new(845821562, 235, 236, -233.879f, -2.224f, 86.783f),  // Bastok Markets -> Port Bastok
        new(845887098, 236, 235, -194.012f, -0.138f, -76.239f), // Port Bastok    -> Bastok Markets
        new(812332666, 236, 106,  142.002f, 4.969f,  -2.008f),  // Port Bastok    -> North Gustaberg
        new(813249146, 106, 236,  660.000f, -2.478f, 306.236f), // North Gustaberg -> Port Bastok
        new(812201594, 234, 107,  -16.039f, -4.217f, -132.804f),// Bastok Mines   -> South Gustaberg
        new(813314682, 107, 234,  579.993f, -1.928f, -305.077f),// South Gustaberg -> Bastok Mines
        new(879375994, 235, 107, -363.536f, -12.108f,-183.525f),// Bastok Markets -> South Gustaberg
        new(846869114, 107, 235,  259.963f, -1.928f, -183.317f),// South Gustaberg -> Bastok Markets
    };

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
