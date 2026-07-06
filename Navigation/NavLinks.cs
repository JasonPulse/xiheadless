namespace XiHeadless.Navigation;

/// Fleet overlay of off-mesh connections, keyed by zone (navmesh filename without extension). Some generated
/// navmeshes are missing links the real in-game geometry has — recast doesn't bake ledge-drops or steep steps as
/// walkable edges. Each Link is a straight traversal from a START point (which decides the owning tile) to an END
/// point; BiDir also allows the reverse. Coordinates are in FFXI world space (the same frame the bot reports).
/// Applied automatically in NavMesh.Load. Adding a zone here fixes it for EVERY bot in the fleet.
public static class NavLinks
{
    /// One off-mesh connection. Rad is the base-link search radius around each endpoint (keep small — just big
    /// enough to reach the intended ground poly, ~2-4y).
    public readonly record struct Link(float Sx, float Sy, float Sz, float Ex, float Ey, float Ez, float Rad, bool BiDir);

    static readonly Dictionary<string, Link[]> ByZone = new()
    {
        // Ordelle's Caves (zone 193) — A Squire's Test II (PLD prereq). The pool qm2 (-91.7,1.2,276.2) and dew qm3
        // (-140,0.7,265) sit ~1y off the floor on a lower chamber the recast bake left unconnected: every cave
        // route reaches only the overhang SHELF (~ -93,30,262) directly above the pool (~14y horizontal, ~29y
        // down) — the descent the player takes in-game was not baked. This drop bridges shelf -> pool; pool<->dew
        // are already linked in the mesh, so this single link makes BOTH qm reachable. BiDir so the bot can path
        // back out after scooping the dew.
        // END lands on the pool poly connected to the dew corridor (-94.5,1.1,276.8) — ~2.9y from qm2
        // (-91.7,1.2,276.2), inside examine range — so from the drop the bot can examine qm2 AND walk the lower
        // corridor to qm3. (Aiming the drop directly at qm2's own poly strands the bot: that poly is NOT linked to
        // the dew corridor.)
        ["Ordelles_Caves"] = new[]
        {
            new Link(-93.3f, 30.1f, 262.4f, -94.5f, 1.1f, 276.8f, 3f, true),
        },
    };

    public static IReadOnlyList<Link>? ForZone(string zone) => ByZone.TryGetValue(zone, out var v) ? v : null;
}
