namespace XiHeadless.Navigation;

/// INavigation backed by the navmesh: MoveTo/Follow compute a detour path, and a
/// step loop walks the bot's position along the waypoints at walk speed (~5 y/s),
/// updating WorldState so the 0x015 keepalive carries it. No teleporting — the
/// position advances smoothly along walkable polygons, like a real player.
public sealed class Navigator : INavigation
{
    const float WalkSpeed = 5.0f;   // yalms/sec (under the server's speed limit)
    const int StepMs = 200;         // movement tick
    const float Arrive = 1.0f;      // waypoint arrival radius

    readonly ISession _s;
    NavMesh? _mesh;
    readonly object _lock = new();
    List<(float x, float y, float z)> _path = new();
    int _wp;
    uint _followId;
    long _lastRepathMs;
    int _started;

    public Navigator(ISession s, NavMesh? mesh) { _s = s; _mesh = mesh; }

    public bool IsMoving { get { lock (_lock) return _wp < _path.Count || _followId != 0; } }

    /// Swap in the navmesh for the current zone (called after a zone change). Drops any
    /// in-flight path since it belonged to the old zone, then reconciles position.
    public void SetMesh(NavMesh? mesh)
    {
        lock (_lock) { _mesh = mesh; _path = new(); _wp = 0; _followId = 0; }
        _s.State.Moving = false;
        ReconcilePosition();
    }

    /// The 0x00A zone-in packet's Y/Z float order differs between the login path and the
    /// zone-change path (and old saved positions may be transposed in the DB). Rather than
    /// guess, ask the mesh: if swapping Y and Z lands us on walkable ground, adopt the swap.
    /// The keepalive then reports the corrected position, which also heals the stored value.
    public void ReconcilePosition()
    {
        if (_mesh is null) return;
        var st = _s.State;
        float asIs = _mesh.DistanceToMesh(st.X, st.Y, st.Z);
        float swapped = _mesh.DistanceToMesh(st.X, st.Z, st.Y);
        if (swapped + 1f < asIs)
        {
            Console.WriteLine($"[nav] zone-in Y/Z transposed (on-mesh {swapped:F1} vs {asIs:F1}); correcting -> ({st.X:F0},{st.Z:F0}v,{st.Y:F0})");
            (st.Y, st.Z) = (st.Z, st.Y);
        }
    }

    public void MoveTo(float x, float z) => MoveTo(x, _s.State.Y, z);

    public void MoveTo(float x, float y, float z)
    {
        var st = _s.State;
        if (_mesh is null) { Console.WriteLine($"[nav] no navmesh for zone {st.ZoneId}; cannot path to ({x:F0},{z:F0})"); return; }
        // FindPath is the single movement authority: it snaps both endpoints onto the navmesh (TallExtents, then
        // WideExtents for an off-mesh start/goal) and returns ground-Y waypoints, so following them keeps us on
        // the proper Y. We trust it — no parallel dead-reckoning. Keeping our Y correct (waypoints below set
        // st.Y = ty each step) is what makes the start snap to the RIGHT vertical layer on steep terrain.
        var path = _mesh.FindPath(st.X, st.Y, st.Z, x, y, z);
        lock (_lock) { _path = path; _wp = 0; _followId = 0; }
        if (path.Count == 0) Console.WriteLine($"[nav] FindPath found no route from ({st.X:F0},{st.Y:F0},{st.Z:F0}) to ({x:F0},{z:F0})");
        EnsureStarted();
    }

    // Reachability pre-check: can the navmesh path us to within melee of (x,y,z)? A brain uses this to AVOID
    // engaging mobs on disconnected mesh islands / off-mesh spots (the WAR would chase forever, finalDist 32-128,
    // and die). Reachable = a non-empty path whose last waypoint lands within ~8y of the target (melee 5 + slack).
    public bool CanReach(float x, float y, float z)
    {
        var m = _mesh; if (m is null) return false;
        var st = _s.State;
        var path = m.FindPath(st.X, st.Y, st.Z, x, y, z);
        return path.Count > 0 && Geometry.Dist2D(path[^1].x, path[^1].z, x, z) <= 8f;
    }

    public void Follow(uint entityId) { lock (_lock) { _followId = entityId; } EnsureStarted(); }

    /// Turn to face an entity (sets heading). The server's melee check rejects attacks with
    /// "unable to see target" unless loc.p.rotation points within ~90deg of the target.
    public void Face(uint entityId)
    {
        var st = _s.State;
        if (!st.Entities.TryGetValue(entityId, out var e)) return;
        st.Rotation = Heading(st.X, st.Z, e.X, e.Z);
    }

    /// Server heading byte from (ax,az) to (bx,bz), matching worldAngle() in common/utils.cpp
    /// exactly so the melee facing() check passes. (Earlier atan2-based formula was ~30deg off.)
    static byte Heading(float ax, float az, float bx, float bz)
    {
        if (ax == bx && az == bz) return 0;
        byte angle = (byte)(int)(MathF.Atan((bz - az) / (bx - ax)) * -(128f / MathF.PI));
        return (byte)(ax > bx ? angle + 128 : angle);
    }

    public void Stop()
    {
        lock (_lock) { _path = new(); _wp = 0; _followId = 0; }
        _s.State.Moving = false;
    }

    void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
            new Thread(Loop) { IsBackground = true, Name = "xi-navigator" }.Start();
    }

    void Loop()
    {
        while (true)
        {
            try { Step(); } catch { }
            Thread.Sleep(StepMs);
        }
    }

    void Step()
    {
        var st = _s.State;
        const float dt = StepMs / 1000f;

        // UNIVERSAL: a DEAD character cannot move. Refuse ALL movement while KO'd (MaxHp>0 && Hpp==0), no matter
        // what any brain commands — this is the core anti-"dead-walking" guard, in the movement layer so no brain
        // can ever bypass it. Death recovery (Home Point) is handled by BotHost's core death task.
        if (st.MaxHp > 0 && st.Hpp == 0) { st.Moving = false; return; }

        // Following: repath toward the target a few times a second; stop when in melee range.
        lock (_lock)
        {
            if (_followId != 0 && st.Entities.TryGetValue(_followId, out var e))
            {
                float d = Geometry.Dist2D(st.X, st.Z, e.X, e.Z);
                if (d <= 1.5f) { st.Moving = false; return; }  // close to true melee range (dist 3 was too far to engage)
                if (_mesh is not null && st.NowMs - _lastRepathMs > 500)
                {
                    _path = _mesh.FindPath(st.X, st.Y, st.Z, e.X, e.Y, e.Z);
                    _wp = 0; _lastRepathMs = st.NowMs;
                }
            }

            if (_wp >= _path.Count) { st.Moving = false; return; }

            var (tx, ty, tz) = _path[_wp];
            float dx = tx - st.X, dz = tz - st.Z;
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            float stepDist = WalkSpeed * dt;

            if (dist <= MathF.Max(stepDist, Arrive)) { st.X = tx; st.Y = ty; st.Z = tz; _wp++; }
            else
            {
                st.X += dx / dist * stepDist;
                st.Z += dz / dist * stepDist;
                // Snap Y to the navmesh GROUND at our new (X,Z) — NOT the far waypoint's ty. On a long climbing
                // segment ty would leave us metres underground between waypoints, and FindPath/CanReach from that
                // off-mesh position return no route (the bot "can't reach" a mob 9y away and re-paths strand it).
                // ty is the hint to pick the right vertical layer; falls back to ty if (X,Z) is briefly off-mesh.
                st.Y = _mesh?.GroundY(st.X, st.Z, ty) ?? ty;
                st.Rotation = Heading(st.X, st.Z, tx, tz);
            }
            st.Moving = true;
        }
    }
}
