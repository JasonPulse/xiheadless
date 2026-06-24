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
    readonly IDtQueryFilter _filter = new DtQueryDefaultFilter();

    NavMesh(DtNavMesh mesh) { _mesh = mesh; _query = new DtNavMeshQuery(mesh); }

    public static NavMesh Load(string navPath)
    {
        // LSB: little-endian C++ detour, 32-bit poly refs (DT_POLYREF64 OFF) -> Read32Bit.
        var bb = new RcByteBuffer(File.ReadAllBytes(navPath));
        bb.Order(RcByteOrder.LITTLE_ENDIAN);
        var mesh = new DtMeshSetReader().Read32Bit(bb, 6);
        return new NavMesh(mesh);
    }

    /// Validation: two random on-mesh points + path between them (detour space, no transform).
    /// Proves load + query + pathfinding independent of FFXI coordinate correctness.
    public (int polys, int waypoints, RcVec3f a, RcVec3f b) SelfTest()
    {
        var rnd = new DotRecast.Core.RcRand(1);
        _query.FindRandomPoint(_filter, rnd, out long aRef, out RcVec3f a);
        _query.FindRandomPoint(_filter, rnd, out long bRef, out RcVec3f b);
        if (aRef == 0 || bRef == 0) return (0, 0, a, b);
        const int MAX = 512;
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
        _query.FindNearestPoly(p, new RcVec3f(30, 60, 30), _filter, out long r, out RcVec3f at, out _);
        return (r != 0, at, r != 0 ? RcVec3f.Distance(p, at) : -1);
    }

    /// Distance from an FFXI-space point to the nearest walkable poly (float.MaxValue if none).
    /// Used to sanity-check / disambiguate a position against the mesh.
    public float DistanceToMesh(float x, float y, float z)
    {
        var d = NearestDetour(x, -y, -z); // ToDetour
        return d.found ? d.dist : float.MaxValue;
    }

    /// Path from (ffxi) start to end, returned as FFXI-space waypoints (string-pulled).
    public List<(float x, float y, float z)> FindPath(float sx, float sy, float sz, float ex, float ey, float ez)
    {
        var result = new List<(float, float, float)>();
        var startPos = ToDetour(sx, sy, sz);
        var endPos = ToDetour(ex, ey, ez);

        _query.FindNearestPoly(startPos, TallExtents, _filter, out long startRef, out RcVec3f startNear, out _);
        _query.FindNearestPoly(endPos, TallExtents, _filter, out long endRef, out RcVec3f endNear, out _);
        if (startRef == 0 || endRef == 0) return result;
        startPos = startNear; endPos = endNear; // use the snapped on-mesh points (correct height)

        const int MAX = 512;
        var polys = new long[MAX];
        _query.FindPath(startRef, endRef, startPos, endPos, _filter, polys, out int npolys, MAX);
        if (npolys == 0) return result;

        var straight = new DtStraightPath[MAX];
        _query.FindStraightPath(startPos, endPos, polys.AsSpan(0, npolys), npolys, straight, out int nstraight, MAX, 0);
        for (int i = 0; i < nstraight; i++) { var f = ToFfxi(straight[i].pos); result.Add(f); }
        return result;
    }
}
