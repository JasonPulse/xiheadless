using DotRecast.Core;
using DotRecast.Core.Buffers;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;

namespace XiHeadless.Navigation;

/// Loads an LSB .nav (standard recast/detour MSET) and finds walk paths on it.
/// FFXI<->detour differ only by a negated Y axis (per LSB CNavMesh::ToFFXIPos).
public sealed class NavMesh
{
    readonly DtNavMesh _mesh;
    readonly DtNavMeshQuery _query;
    static readonly RcVec3f Extents = new(2.5f, 5.0f, 2.5f);   // poly search box (LSB polyPickExt)
    static readonly RcVec3f TallExtents = new(5.0f, 100.0f, 5.0f); // tall box: snap a target whose height we only approximate (outdoor terrain)
    static readonly RcVec3f WideExtents = new(25.0f, 200.0f, 25.0f); // fallback: snap an off-mesh target (e.g. a crystal/NPC against a wall) to the nearest walkable poly
    readonly IDtQueryFilter _filter = new DtQueryDefaultFilter();
    // DtNavMeshQuery is NOT thread-safe: its A* open-list is instance state, and the brain thread's
    // CanReach raced the navigator thread's FindPath into a corrupted sort (NRE inside DotRecast's
    // ComparisonNodeTotal — crashed the WHM brain live). One lock serializes every query.
    readonly object _qlock = new();

    NavMesh(DtNavMesh mesh) { _mesh = mesh; _query = new DtNavMeshQuery(mesh); }

    public static NavMesh Load(string navPath)
    {
        // LSB: little-endian C++ detour, 32-bit poly refs (DT_POLYREF64 OFF) -> Read32Bit.
        var bb = new RcByteBuffer(File.ReadAllBytes(navPath));
        bb.Order(RcByteOrder.LITTLE_ENDIAN);
        var mesh = new DtMeshSetReader().Read32Bit(bb, 6);
        // Fleet overlay: some generated navmeshes are missing links the real geometry has (recast didn't bake a
        // ledge-drop / step). Inject per-zone off-mesh connections from the registry so the bot can traverse them.
        var zone = Path.GetFileNameWithoutExtension(navPath);
        if (NavLinks.ForZone(zone) is { Count: > 0 } links) ApplyOffMeshLinks(mesh, links, zone);
        return new NavMesh(mesh);
    }

    /// TEST HOOK: load a mesh and apply an explicit set of off-mesh links (bypasses the registry).
    public static NavMesh LoadWithLinks(string navPath, IReadOnlyList<NavLinks.Link> links)
    {
        var bb = new RcByteBuffer(File.ReadAllBytes(navPath));
        bb.Order(RcByteOrder.LITTLE_ENDIAN);
        var mesh = new DtMeshSetReader().Read32Bit(bb, 6);
        ApplyOffMeshLinks(mesh, links, Path.GetFileNameWithoutExtension(navPath));
        return new NavMesh(mesh);
    }

    // Inject off-mesh connections into an already-loaded mesh. Each link's START point decides which tile the
    // connection lives in; the tile is rebuilt (via DtNavMeshBuilder) with the connection appended, then swapped
    // back in (RemoveTile+AddTile re-links it to its neighbours and base-links both endpoints to ground polys).
    static void ApplyOffMeshLinks(DtNavMesh mesh, IReadOnlyList<NavLinks.Link> links, string zone)
    {
        var byTile = new Dictionary<(int, int), List<NavLinks.Link>>();
        foreach (var l in links)
        {
            var s = ToDetour(l.Sx, l.Sy, l.Sz);
            mesh.CalcTileLoc(s, out int tx, out int ty);
            if (!byTile.TryGetValue((tx, ty), out var list)) byTile[(tx, ty)] = list = new();
            list.Add(l);
        }
        foreach (var ((tx, ty), list) in byTile)
        {
            long oldRef = mesh.GetTileRefAt(tx, ty, 0);
            var tile = mesh.GetTileByRef(oldRef);
            if (tile?.data?.header is null) { Console.WriteLine($"[nav] {zone}: no tile at ({tx},{ty}) for {list.Count} off-mesh link(s) — skipped"); continue; }
            var rebuilt = RebuildTileWithCons(mesh, tile.data, list);
            mesh.RemoveTile(oldRef);
            mesh.AddTile(rebuilt, 0, 0, out _);
            Console.WriteLine($"[nav] {zone}: injected {list.Count} off-mesh link(s) into tile ({tx},{ty})");
        }
    }

    // Reconstruct a tile's DtMeshData from its runtime form + append off-mesh connections. Ground polygon
    // TOPOLOGY (verts, poly/neighbour indices) is preserved exactly; we pass detail=null so the builder rebuilds
    // trivial detail (height comes from the poly plane, which the Navigator re-snaps via GroundY anyway) — this
    // sidesteps recast's detail-vertex packing entirely. Verts are re-quantised at a fine cs so border verts still
    // match neighbour tiles for external linking.
    static DtMeshData RebuildTileWithCons(DtNavMesh mesh, DtMeshData md, List<NavLinks.Link> cons)
    {
        var h = md.header;
        int nvp = mesh.GetMaxVertsPerPoly();
        const int NULL_IDX = 0xffff;
        const float cs = 0.02f, ch = 0.02f;   // fine grid: quant error <=0.01y, tile-local range well under ushort
        int gVerts = h.vertCount - h.offMeshConCount * 2;   // exclude any existing off-mesh endpoint verts
        int gPolys = h.offMeshBase;                          // ground polys precede off-mesh polys

        var p = new DtNavMeshCreateParams
        {
            tileX = h.x, tileZ = h.y, tileLayer = h.layer, userId = h.userId,
            bmin = h.bmin, bmax = h.bmax,
            walkableHeight = h.walkableHeight, walkableRadius = h.walkableRadius, walkableClimb = h.walkableClimb,
            cs = cs, ch = ch, nvp = nvp, buildBvTree = true,
            vertCount = gVerts, verts = new int[gVerts * 3],
            polyCount = gPolys, polys = new int[gPolys * 2 * nvp], polyFlags = new int[gPolys], polyAreas = new int[gPolys],
        };

        for (int i = 0; i < gVerts; i++)
        {
            p.verts[i * 3 + 0] = (int)MathF.Round((md.verts[i * 3 + 0] - h.bmin.X) / cs);
            p.verts[i * 3 + 1] = (int)MathF.Round((md.verts[i * 3 + 1] - h.bmin.Y) / ch);
            p.verts[i * 3 + 2] = (int)MathF.Round((md.verts[i * 3 + 2] - h.bmin.Z) / cs);
        }

        for (int pi = 0; pi < gPolys; pi++)
        {
            var poly = md.polys[pi];
            for (int j = 0; j < nvp; j++)
            {
                bool used = j < poly.vertCount;
                p.polys[pi * 2 * nvp + j] = used ? poly.verts[j] : NULL_IDX;
                p.polys[pi * 2 * nvp + nvp + j] = used ? RuntimeNeiToSource(poly.neis[j]) : NULL_IDX;
            }
            p.polyFlags[pi] = poly.flags;
            p.polyAreas[pi] = poly.GetArea();
        }

        int existing = h.offMeshConCount, total = existing + cons.Count;
        p.offMeshConCount = total;
        p.offMeshConVerts = new float[total * 6];
        p.offMeshConRad = new float[total];
        p.offMeshConFlags = new int[total];
        p.offMeshConAreas = new int[total];
        p.offMeshConDir = new int[total];
        p.offMeshConUserID = new int[total];
        for (int i = 0; i < existing; i++)   // carry forward any pre-existing off-mesh cons (verts are detour-space)
        {
            var c = md.offMeshCons[i];
            p.offMeshConVerts[i * 6 + 0] = c.pos[0].X; p.offMeshConVerts[i * 6 + 1] = c.pos[0].Y; p.offMeshConVerts[i * 6 + 2] = c.pos[0].Z;
            p.offMeshConVerts[i * 6 + 3] = c.pos[1].X; p.offMeshConVerts[i * 6 + 4] = c.pos[1].Y; p.offMeshConVerts[i * 6 + 5] = c.pos[1].Z;
            p.offMeshConRad[i] = c.rad;
            p.offMeshConFlags[i] = md.polys[h.offMeshBase + i].flags;
            p.offMeshConAreas[i] = md.polys[h.offMeshBase + i].GetArea();
            p.offMeshConDir[i] = (c.flags & 1) != 0 ? 1 : 0;
            p.offMeshConUserID[i] = c.userId;
        }
        for (int k = 0; k < cons.Count; k++)
        {
            int i = existing + k;
            var l = cons[k];
            var s = ToDetour(l.Sx, l.Sy, l.Sz);
            var e = ToDetour(l.Ex, l.Ey, l.Ez);
            p.offMeshConVerts[i * 6 + 0] = s.X; p.offMeshConVerts[i * 6 + 1] = s.Y; p.offMeshConVerts[i * 6 + 2] = s.Z;
            p.offMeshConVerts[i * 6 + 3] = e.X; p.offMeshConVerts[i * 6 + 4] = e.Y; p.offMeshConVerts[i * 6 + 5] = e.Z;
            p.offMeshConRad[i] = l.Rad;
            p.offMeshConFlags[i] = 1;          // flag bit 0 = walkable (matches ground polys, passes the default filter)
            p.offMeshConAreas[i] = 0;          // area 0 = same as ground polys
            p.offMeshConDir[i] = l.BiDir ? 1 : 0;
            p.offMeshConUserID[i] = 0;
        }

        return DtNavMeshBuilder.CreateNavMeshData(p);
    }

    // Runtime dtPoly.neis encoding -> recast source encoding expected by DtNavMeshBuilder.
    //   runtime: 0 = solid border; 1..0x7fff = internal neighbour (1-based); 0x8000|side = tile-border portal.
    //   source : 0xffff = border; 0-based internal index; 0x8000|dir portal (builder remaps dir->side as
    //            0->4, 1->2, 2->0, 3->6), so we invert that remap for portals.
    static int RuntimeNeiToSource(int nei)
    {
        if (nei == 0) return 0xffff;
        if ((nei & 0x8000) == 0) return nei - 1;
        int side = nei & 0xff;
        int dir = side switch { 4 => 0, 2 => 1, 0 => 2, 6 => 3, _ => 0xf };
        return 0x8000 | dir;
    }

    /// Validation: two random on-mesh points + path between them (detour space, no transform).
    /// Proves load + query + pathfinding independent of FFXI coordinate correctness.
    public (int polys, int waypoints, RcVec3f a, RcVec3f b) SelfTest()
    {
        var rnd = new DotRecast.Core.RcRand(1);
        _query.FindRandomPoint(_filter, rnd, out long aRef, out RcVec3f a);
        _query.FindRandomPoint(_filter, rnd, out long bRef, out RcVec3f b);
        if (aRef == 0 || bRef == 0) return (0, 0, a, b);
        const int MAX = 2048;   // raised from 512: Buburimu's navmesh is complete but paths AROUND terrain
                                 // barriers are long; 512 polys truncated them ~23y short of the mob (no melee).
        var polys = new long[MAX];
        _query.FindPath(aRef, bRef, a, b, _filter, polys, out int np, MAX);
        var straight = new DtStraightPath[MAX];
        _query.FindStraightPath(a, b, polys.AsSpan(0, np), np, straight, out int ns, MAX, 0);
        return (np, ns, a, b);
    }

    static RcVec3f ToDetour(float x, float y, float z) => new(x, -y, -z); // LSB ToDetourPos negates Y and Z
    static (float x, float y, float z) ToFfxi(RcVec3f v) => (v.X, -v.Y, -v.Z);

    /// Debug: nearest poly to a raw detour point (wide search). Returns found pos + distance.
    public (bool found, RcVec3f at, float dist) NearestDetour(float dx, float dy, float dz)
    {
        var p = new RcVec3f(dx, dy, dz);
        long r; RcVec3f at;
        lock (_qlock) _query.FindNearestPoly(p, new RcVec3f(30, 60, 30), _filter, out r, out at, out _);
        return (r != 0, at, r != 0 ? RcVec3f.Distance(p, at) : -1);
    }

    /// Distance from an FFXI-space point to the nearest walkable poly (float.MaxValue if none).
    /// Used to sanity-check / disambiguate a position against the mesh.
    public float DistanceToMesh(float x, float y, float z)
    {
        var d = NearestDetour(x, -y, -z); // ToDetour
        return d.found ? d.dist : float.MaxValue;
    }

    /// FFXI ground height at (x,z), from the nearest walkable poly (yHint picks the right vertical layer on
    /// multi-level terrain). Returns yHint if off-mesh. The Navigator snaps the bot's Y to this each step so it
    /// stays ON the surface between waypoints — otherwise on a long climbing segment Y holds the far waypoint's
    /// value, the bot goes "underground", and FindPath/CanReach FROM that off-mesh position return no route (it
    /// can con a mob 9y away but "can't reach" it, and re-paths strand it on an isolated poly).
    public float GroundY(float x, float z, float yHint)
    {
        var n = NearestDetour(x, -yHint, -z); // ToDetour
        return n.found ? -n.at.Y : yHint;     // ToFfxi Y = -detourY
    }

    /// Path from (ffxi) start to end, returned as FFXI-space waypoints (string-pulled).
    public List<(float x, float y, float z)> FindPath(float sx, float sy, float sz, float ex, float ey, float ez)
    {
        var result = new List<(float, float, float)>();
        var startPos = ToDetour(sx, sy, sz);
        var endPos = ToDetour(ex, ey, ez);

        lock (_qlock)
        {
            _query.FindNearestPoly(startPos, TallExtents, _filter, out long startRef, out RcVec3f startNear, out _);
            _query.FindNearestPoly(endPos, TallExtents, _filter, out long endRef, out RcVec3f endNear, out _);
            // Off-mesh endpoint (e.g. a crystal/NPC placed a few yalms off walkable ground): retry with a
            // wider box so we path to the nearest walkable spot beside it instead of failing outright.
            if (startRef == 0) _query.FindNearestPoly(startPos, WideExtents, _filter, out startRef, out startNear, out _);
            if (endRef == 0) _query.FindNearestPoly(endPos, WideExtents, _filter, out endRef, out endNear, out _);
            if (startRef == 0 || endRef == 0) return result;
            startPos = startNear; endPos = endNear; // use the snapped on-mesh points (correct height)

            const int MAX = 2048;   // raised from 512: Buburimu's navmesh is complete but paths AROUND terrain
                                     // barriers are long; 512 polys truncated them ~23y short of the mob (no melee).
            var polys = new long[MAX];
            _query.FindPath(startRef, endRef, startPos, endPos, _filter, polys, out int npolys, MAX);
            if (npolys == 0) return result;

            var straight = new DtStraightPath[MAX];
            _query.FindStraightPath(startPos, endPos, polys.AsSpan(0, npolys), npolys, straight, out int nstraight, MAX, 0);
            for (int i = 0; i < nstraight; i++) { var f = ToFfxi(straight[i].pos); result.Add(f); }
            return result;
        }
    }
}
