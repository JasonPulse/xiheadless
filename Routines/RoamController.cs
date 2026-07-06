namespace XiHeadless.Routines;

/// Zone-agnostic FREE-ROAM movement: no camp coordinates, no per-zone corridors, no keep-out geometry.
/// The bot wanders in committed headings and steers by CON — the same rule that already governs what to
/// fight ("con is the sole arbiter") applied to where to walk: headings whose path passes near a known
/// con>=5 mob are rejected, headings toward con-in-band prey are preferred. Danger is discovered live
/// (budgeted /checks, cached per entity, cleared on level-up) instead of being hardcoded per zone.
///
/// The avoid radius is grounded in the server's aggro model (mob_controller.cpp CanDetectTarget):
/// default sight aggro = 15y frontal cone, sound aggro = 8y omnidirectional — so a ~20y berth keeps a
/// walking bot outside both envelopes of anything it knows is too tough.
public sealed class RoamController(INavigation nav, IPerception p, ICombat combat, RoamController.Config cfg)
{
    public sealed class Config
    {
        public float HopLength = 150f;      // committed distance per roam step (short ~22y in a party so a healer keeps up; long solo)
        public float ThreatRadius = 20f;    // stay this far from known con>=5 mobs (> server sight 15y / sound 8y aggro)
        public int ConMin = 2, ConMax = 4;  // the engage band — used to score headings toward viable prey
        public int ConBudgetPerStep = 2;    // max /checks spent discovering unknown mobs per roam step
        // Prey-memory persistence: where in-band mobs were conned, saved per zone so the NEXT session starts
        // knowing its hunting grounds instead of re-exploring blind (runtime-learned — no hardcoded camps).
        public string MemoryFile = "";
        // OPT-IN (default off — GLOBAL behavior is unchanged for every other brain): priority/dropper treks
        // COMMIT — in-band mobs en route neither deliver the trek nor stop the walk. The Buburimu farm sets
        // this (user call at WAR 27: march straight to the Rarab camps); solo levelers keep hunt-en-route.
        public bool CommitPriorityTreks = false;
        // Mobs to AVOID by name (treated as threats regardless of con) — sleep-lock Saplin/Mandragora are
        // con-blind certain death, so the roam must steer around their aggro radius, not just skip targeting
        // them. Default empty = fleet behavior unchanged.
        public string[] AvoidNames = Array.Empty<string>();
        public string Tag = "roam";
    }

    readonly Dictionary<uint, int> _con = new();   // entity id -> last /check difficulty (0-7; -1 = no reply)
    double _heading;                                // committed direction so roaming travels, not circles
    byte _lastLvl;
    float _lastX, _lastZ; int _stalls;              // detect roam steps that produce no displacement (mesh pocket)
    readonly List<(float x, float z)> _trail = new();      // recent committed hop ORIGINS (known-walkable) — backtrack targets + anti-revisit
    readonly List<(float x, float z)> _preySeen = new();   // where in-band mobs were conned — free roam that LEARNS
    int _boxed;                                            // consecutive fully-blocked steps → backtrack
    int _desperate;                                        // consecutive boxed-in terminal hits → forced pathfinder escape
    (float x, float z)? _trek;                             // active trek destination (for memory self-cleaning on arrival)
    bool _forceTrek;                                       // hunger override: leave thin ground even if stray in-band mobs remain
    bool _trekPriority;                                    // the committed trek came from the PRIORITY pool (dropper march)
    readonly List<(float x, float z)> _priority = new();   // dropper clusters: forced treks go HERE first (the farm's whole point)

    /// A COMMITTED dropper-cluster march is in progress (only ever true when cfg.CommitPriorityTreks is on).
    public bool OnDropperTrek => cfg.CommitPriorityTreks && _trek is not null && _trekPriority;

    /// Priority trek targets (dropper clusters from spawn data) — a ForceTrek picks the nearest non-cooldown
    /// one of these before any generic memory point, so dropper-seek actually reaches the droppers.
    public void SetPriority(IEnumerable<(float x, float z)> pts) { _priority.Clear(); _priority.AddRange(pts); }
    readonly Dictionary<(int x, int z), long> _visited = new();   // trek arrivals (20y grid) -> time; revisit cooldown
    bool _memLoaded;
    long _lastSaveMs;

    /// Hunger override (kills ran dry here): put THIS area on cooldown and let the next step trek to a deeper
    /// seed even if stray in-band mobs linger in view — orbiting the thin Mhaura-entrance pocket for the odd
    /// goblin was worth less than reaching the dense clusters (user-observed, twice).
    /// far=true: pick the FARTHEST eligible ground instead of the nearest — dry-spell escalation. Repeated
    /// nearest-first treks shuffled a THF between adjacent night-guarded memory spots for hours (10.8k hops,
    /// 1 kill); when local ground is repeatedly fruitless the answer is distance, not another shuffle.
    public void ForceTrek(bool far = false)
    {
        _visited[Grid(p.World.X, p.World.Z)] = p.World.NowMs;
        _trek = null;
        _forceTrek = true;
        _trekFar = far;
    }
    bool _trekFar;

    const long RevisitCooldownMs = 180_000;   // ~respawn time — re-walking sooner covers only cleared ground
    // 40y cells: adjacent seeds ~30y apart used to sit in different cells, so arriving at one left the other
    // "fresh" and the duo ping-ponged between them (the short backtracking the user watched).
    static (int x, int z) Grid(float x, float z) => ((int)MathF.Round(x / 40f), (int)MathF.Round(z / 40f));

    void Log(string m) => XiHeadless.Log.Auto($"[{cfg.Tag}] {m}");

    // Prey memory persists across sessions (per zone): a bot that hunted here before starts by heading back
    // to remembered prey ground instead of paying the blind-exploration tax every launch. Plain "x z" lines.
    void LoadMemory()
    {
        if (_memLoaded) { return; }
        _memLoaded = true;
        try
        {
            if (cfg.MemoryFile.Length == 0 || !System.IO.File.Exists(cfg.MemoryFile)) return;
            foreach (var line in System.IO.File.ReadAllLines(cfg.MemoryFile))
            {
                var parts = line.Split(' ');
                if (parts.Length == 2 && float.TryParse(parts[0], out var x) && float.TryParse(parts[1], out var z))
                    _preySeen.Add((x, z));
            }
            if (_preySeen.Count > 0) Log($"loaded {_preySeen.Count} remembered prey spots from {cfg.MemoryFile}");
        }
        catch (Exception ex) { Log($"prey-memory load failed: {ex.Message}"); }
    }

    /// Seed the prey memory from generated spawn data (SpawnClusters) — deduped against what's already
    /// learned so runtime knowledge (purges, live cons) always wins over the static prior.
    public void SeedMemory(IEnumerable<(float x, float z)> pts)
    {
        LoadMemory();
        int added = 0;
        foreach (var (x, z) in pts)
            if (!_preySeen.Any(m => Geometry.Dist2D(m.x, m.z, x, z) < 30f)) { _preySeen.Add((x, z)); added++; }
        if (added > 0) { Log($"seeded {added} spawn clusters from server data (memory now {_preySeen.Count})"); SaveMemory(); }
    }

    void SaveMemory()
    {
        if (cfg.MemoryFile.Length == 0 || p.World.NowMs - _lastSaveMs < 15000) return;
        _lastSaveMs = p.World.NowMs;
        try { System.IO.File.WriteAllLines(cfg.MemoryFile, _preySeen.Select(m => $"{m.x:F0} {m.z:F0}")); }
        catch (Exception ex) { Log($"prey-memory save failed: {ex.Message}"); }
    }

    void ClearOnLevelUp()
    {
        if (p.World.MainJobLevel == _lastLvl) return;
        _lastLvl = p.World.MainJobLevel;
        _con.Clear();   // re-judge everything by con as we get stronger (user rule: skips are con-only + reset on level-up)
    }

    /// Con with a per-entity cache (a mob's con only changes when WE level, which clears the cache).
    /// Shared by roam steering and target selection so knowledge accumulates instead of re-/checking.
    /// In-band results also feed the PREY MEMORY: where huntable mobs live, learned at runtime — so the roam
    /// can head back to known hunting grounds after a rally instead of re-exploring from the zone-in.
    public async Task<int> ConsiderCached(uint id, CancellationToken ct)
    {
        ClearOnLevelUp();
        if (_con.TryGetValue(id, out var c)) return c;
        c = await combat.Consider(id, ct);
        // Cache no-reply (-1) too: objects (???/Field Manual) never reply and would otherwise re-/check forever.
        _con[id] = c;
        if (c >= cfg.ConMin && c <= cfg.ConMax && p.World.Entities.TryGetValue(id, out var e))
        {
            _preySeen.Add((e.X, e.Z));
            if (_preySeen.Count > 200) _preySeen.RemoveAt(0);
            SaveMemory();
        }
        return c;
    }

    public int? KnownCon(uint id) { ClearOnLevelUp(); return _con.TryGetValue(id, out var c) ? c : null; }

    /// Is this spot safe to SIT in? Resting drops all defense, and CON measures winnability, not temperament —
    /// a con-3 goblin is still AGGRESSIVE and any hit interrupts the rest (the duo sat down in front of one,
    /// live-observed). The only safe neighbor is a non-combat object (con -1, the no-reply ???/Manual class):
    /// ANY real mob within the radius, whatever its con, means relocate before sitting.
    public async Task<bool> SafeToRest(float radius, CancellationToken ct) => await RestThreat(radius, ct) is null;

    /// The nearest mob that makes this spot unsafe to sit in (null = safe) — returned as an ENTITY so the
    /// caller can step directly AWAY from it. Generic roam hops only repel known con>=5 mobs, so a con-4
    /// aggressive chased the duo through its "relocation" and killed the 39%-HP tank mid-shuffle.
    public async Task<Entity?> RestThreat(float radius, CancellationToken ct)
    {
        int budget = 2;
        foreach (var e in NearbyMobs().Where(e => p.DistanceTo(e.X, e.Z) < radius).OrderBy(e => p.DistanceTo(e.X, e.Z)))
        {
            int c = KnownCon(e.Id) ?? (budget-- > 0 ? await ConsiderCached(e.Id, ct) : -2);
            if (c == -1) continue;   // no-reply = non-combat object — harmless to sit near
            Log($"unsafe rest spot: '{e.Name}' (con={(c == -2 ? "?" : c.ToString())}) {p.DistanceTo(e.X, e.Z):F0}y away");
            return e;
        }
        return null;
    }

    /// True if engaging `target` won't happen inside a known-tough mob's aggro envelope: /checks (budgeted)
    /// the unknown mobs near the target, then refuses the pull if any known con>=5 sits within ThreatRadius.
    /// This is the "clean pull" gate — the add-cascade (pulling a con-3 with a con-5 goblin 10y away) was a
    /// top death cause.
    public async Task<bool> CleanPull(Entity target, (float x, float z)? guard, CancellationToken ct, int dirtyCon = 5)
    {
        // 16y = the server's sight-aggro range (15y, mobentity.h) + margin. Using the roomier roam radius
        // (20y) here rejected SAFE pulls — huntable mobs co-spawn ~18y from the Bull_Dhalmels, and the farm
        // starved with clean prey in view.
        // `guard` = a second spot that must ALSO be clear — the HEALER's position. Checking only around the
        // target let an add reach the WHM standing 15y behind the fight; it died, and the tank then lost a
        // fight it was winning by 4%.
        const float PullThreatRadius = 16f;
        int budget = 3;
        foreach (var e in NearbyMobs())
        {
            if (e.Id == target.Id) continue;
            float dTarget = Geometry.Dist2D(e.X, e.Z, target.X, target.Z);
            float dGuard = guard is { } g ? Geometry.Dist2D(e.X, e.Z, g.x, g.z) : 999f;
            if (dTarget > PullThreatRadius && dGuard > PullThreatRadius) continue;
            int c = KnownCon(e.Id) ?? (budget-- > 0 ? await ConsiderCached(e.Id, ct) : -2);   // -2 = unknown, out of budget
            // dirtyCon: 5 for a duo (a healer + peel absorbs one add) — but a SOLO low-level bot dies to a
            // chained add of its own con band (crawler nest: won the pull, the neighbor killed it mid-rest),
            // so fragile brains lower this to their ConMax.
            if (c >= dirtyCon)
            {
                Log($"dirty pull: con={c} '{e.Name}' is {MathF.Min(dTarget, dGuard):F0}y from the {(dTarget <= dGuard ? "target" : "healer")} — skipping this pull");
                return false;
            }
        }
        return true;
    }

    /// Take one roam step: discover nearby threats (budgeted /checks), then commit a hop in the best-scored
    /// heading — away from known con>=5 mobs, toward con-in-band prey. No-op while a previous hop is running.
    public async Task StepAsync(CancellationToken ct)
    {
        if (nav.IsMoving) return;
        ClearOnLevelUp();
        LoadMemory();

        // Stall detection: on a narrow mesh strip (e.g. a zone-in tip) every hop candidate can snap back to
        // where we stand — MoveTo "succeeds" with a degenerate path and the bot idles silently forever (the
        // frozen-at-zone-in bug). If successive steps produce no displacement, escalate the hop length to
        // punch out of the pocket.
        float moved = Geometry.Dist2D(p.World.X, p.World.Z, _lastX, _lastZ);
        _lastX = p.World.X; _lastZ = p.World.Z;
        if (moved < 2f) _stalls++; else _stalls = 0;
        float hop = _stalls >= 4 ? cfg.HopLength * 3f : cfg.HopLength;
        if (_stalls == 4) Log($"roam stalled at ({p.World.X:F0},{p.World.Z:F0}) — escaping with {hop:F0}y hops");
        // Longer hops alone don't escape a rim pocket: they fail CanReach, the 8y desperation pass "succeeds"
        // without displacing us, and visible-but-unreachable prey (below a canyon rim) pulls the heading
        // straight back every step (observed: 3,089 identical 8y hops). BLIND mode: forget prey ground we
        // can't path to, stop scoring prey-ward, reverse once, and explore on anti-revisit alone until we move.
        bool blind = _stalls >= 6;
        if (_stalls == 6)
        {
            int purged = _preySeen.RemoveAll(m => !nav.CanReach(m.x, p.World.Y, m.z));
            _heading += Math.PI;
            Log($"still pinned — BLIND escape (purged {purged} unreachable prey memories, ignoring prey pull)");
        }

        // Discover: /check the nearest unknown mobs so the threat map fills in as we travel. Unknown mobs
        // are treated as neutral (not threats) — short hops mean we meet them a hop at a time, and the
        // budget tops the map up every step.
        int budget = cfg.ConBudgetPerStep;
        foreach (var e in NearbyMobs().OrderBy(e => p.DistanceTo(e.X, e.Z)))
        {
            if (budget <= 0) break;
            if (KnownCon(e.Id) is null && p.DistanceTo(e.X, e.Z) < 35f) { await ConsiderCached(e.Id, ct); budget--; }
        }

        var w = p.World;
        var threats = NearbyMobs()
            .Where(e => KnownCon(e.Id) >= 5
                || (cfg.AvoidNames.Length > 0 && cfg.AvoidNames.Any(n => e.Name.Contains(n, StringComparison.OrdinalIgnoreCase))))
            .Select(e => (e.X, e.Z)).ToList();
        // Prey = in-band mobs we could actually pull. A winnable mob parked inside a threat's aggro bubble is
        // NOT prey (the clean-pull gate will refuse it) — counting it kept the roam orbiting dirty clusters.
        var prey = NearbyMobs()
            .Where(e => KnownCon(e.Id) is int c && c >= cfg.ConMin && c <= cfg.ConMax)
            .Where(e => !threats.Any(t => Geometry.Dist2D(t.X, t.Z, e.X, e.Z) < cfg.ThreatRadius))
            .Select(e => (e.X, e.Z)).ToList();

        // TREK: nothing huntable in view AT ALL — walk straight to remembered prey ground instead of
        // fan-hopping (at short hop lengths the memory gradient rounds to zero, and a mesh pocket saturates
        // the anti-revisit penalty equally in every direction, so local hops random-walked the tip for whole
        // rounds). Gate on ANY in-band mob (even a dirty-pull one): dirty prey means "hunt here, carefully" —
        // trekking away from live in-band mobs bounced the duo between memory points all round. Memory is
        // SELF-CLEANING: arriving at a remembered spot and finding nothing in-band forgets that area.
        bool anyInBand = NearbyMobs().Any(e => KnownCon(e.Id) is int c && c >= cfg.ConMin && c <= cfg.ConMax);
        if (_trek is { } tgt)
        {
            if (anyInBand && !OnDropperTrek) { _visited[Grid(tgt.x, tgt.z)] = p.World.NowMs; _trek = null; }   // delivered — mark visited, hunt (opted-in priority marches deliver on ARRIVAL only)
            else if (Geometry.Dist2D(tgt.x, tgt.z, w.X, w.Z) < 25f)
            {
                int purged = _preySeen.RemoveAll(m => Geometry.Dist2D(m.x, m.z, tgt.x, tgt.z) < 30f);
                Log($"remembered spot ({tgt.x:F0},{tgt.z:F0}) is empty — forgetting {purged} stale memory point(s)");
                _visited[Grid(tgt.x, tgt.z)] = p.World.NowMs;
                _trek = null;
                SaveMemory();
            }
        }
        if ((!anyInBand || _forceTrek || OnDropperTrek) && _preySeen.Count > 0)
        {
            // COMMIT to the active trek target — recomputing "nearest" on every step re-issue flipped between
            // roughly-equidistant clusters as we moved, reversing the heading each time (the tight zigzag the
            // user watched live). A new target is chosen only on arrival, delivery, or purge.
            // Priority (dropper) treks ROTATE: least-recently-visited first (unvisited = first of all), so
            // every dropper cluster gets its turn — nearest-first re-picked the same close cluster on every
            // single seek (8/8 treks to one point; the Rarab grounds 400y west never got visited).
            bool usePriority = _forceTrek && _priority.Count > 0;
            var near = _trek ?? (usePriority
                ? _priority
                    .OrderBy(m => _visited.TryGetValue(Grid(m.x, m.z), out var t) ? t : 0)
                    .ThenBy(m => Geometry.Dist2D(m.x, m.z, w.X, w.Z))
                    .Cast<(float x, float z)?>().FirstOrDefault()
                : _preySeen
                    .Where(m => !_visited.TryGetValue(Grid(m.x, m.z), out var t) || p.World.NowMs - t > RevisitCooldownMs)
                    .OrderBy(m => _trekFar ? -Geometry.Dist2D(m.x, m.z, w.X, w.Z) : Geometry.Dist2D(m.x, m.z, w.X, w.Z))
                    .Cast<(float x, float z)?>().FirstOrDefault());
            if (near is { } n && (Geometry.Dist2D(n.x, n.z, w.X, w.Z) > 100f || _trek is not null) && nav.CanReach(n.x, w.Y, n.z))
            {
                nav.MoveTo(n.x, n.z);
                if (nav.IsMoving)
                {
                    if (_trek is null)
                    {
                        Log($"trek -> ({n.x:F0},{n.z:F0}) [{(usePriority ? "priority/dropper" : _forceTrek ? "hunger" : "memory")}]");
                        _trekPriority = usePriority;   // only on a FRESH pick — re-issues run with _forceTrek consumed
                    }
                    _trek = n; _forceTrek = false;
                    return;
                }
            }
        }
        // EXPLORATION LEGS: nothing in-band in view and nowhere remembered to go — cover ground in long
        // strides, not 22y shuffles (the entrance region alone is ~150y; hop-scale exploration re-walked it
        // for whole rounds). The tether still gates each leg, so the healer keeps up.
        if (!anyInBand) hop = MathF.Max(hop, 66f);

        // Candidate headings fan out around the committed one; the ladder samples several DISTANCES (largest
        // first — we want the farthest reachable relocation), threat-free and CanReach-gated at every rung.
        // Score = prey near the hop target; with none in view, remembered prey ground (_preySeen) pulls the
        // heading back toward known hunting areas instead of blind wandering. CanReach gates every candidate:
        // without it, a target in the void snaps to the mesh point we're already standing on and MoveTo
        // "succeeds" with a degenerate zero-length path.
        //   FLOOR = 16y, NOT 8y: CanReach's arrival tolerance is 8y (Navigator.CanReach), so an OFF-mesh point
        //   ~8y away passes it — its path stops at our feet, still within 8y of the target — a false success
        //   that committed a zero-displacement "hop" and logged `hop -> [8y]` to the SAME coords forever (the
        //   stall/escape loop: the 450y escape rung failed CanReach, then the 8y rung "succeeded" without ever
        //   relocating). A 16y rung still threads a narrow pocket (a 10y-reachable path lands 6y from a 16y
        //   target = within tolerance, so it commits the real 10y walk) but a genuine snap-back (path at feet,
        //   16y from target) is now correctly rejected. A mid rung (hop*0.3) closes the gap so a long escape
        //   that fails CanReach still finds a reachable mid-range hop instead of dropping straight to the floor.
        foreach (float dist in blind ? new[] { hop, hop * 0.6f } : new[] { hop, hop * 0.6f, hop * 0.3f, 16f })
        {
            (double ang, float tx, float tz, int score) best = (double.NaN, 0, 0, int.MinValue);
            for (int i = 0; i < 8; i++)
            {
                double ang = _heading + (i * (Math.PI / 4)) * (i % 2 == 0 ? 1 : -1);
                float tx = w.X + (float)Math.Cos(ang) * dist, tz = w.Z + (float)Math.Sin(ang) * dist;
                if (threats.Any(t => SegDist(w.X, w.Z, tx, tz, t.X, t.Z) < cfg.ThreatRadius)) continue;
                if (!nav.CanReach(tx, w.Y, tz)) continue;   // ocean/void/disconnected-island candidate — reject
                int score = (blind ? 0 : prey.Count(m => Geometry.Dist2D(m.X, m.Z, tx, tz) < 40f) * 4) - i;   // prefer prey-ward, tie-break toward the committed heading
                if (!blind && prey.Count == 0 && _preySeen.Count > 0)
                {
                    // No prey in view: prefer the candidate that CLOSES distance to remembered prey ground.
                    float nearest = _preySeen.Min(m => Geometry.Dist2D(m.x, m.z, tx, tz));
                    score += (int)(-nearest / 10f);   // closer remembered ground = higher score
                }
                // ANTI-REVISIT: penalize ground we recently stood on. Without this a dead-end pocket is a
                // random-walk attractor — the bot oscillated in the peninsula tip for 15 minutes. Visited
                // ground repels, so exploration pushes systematically into NEW ground and out of pockets.
                score -= _trail.Count(t => Geometry.Dist2D(t.x, t.z, tx, tz) < 15f) * 3;
                if (score > best.score) best = (ang, tx, tz, score);
            }
            if (!double.IsNaN(best.ang))
            {
                nav.MoveTo(best.tx, best.tz);
                if (nav.IsMoving)
                {
                    _heading = best.ang; _boxed = 0;
                    _trail.Add((w.X, w.Z));
                    if (_trail.Count > 24) _trail.RemoveAt(0);   // horizon covers a whole pocket so anti-revisit can expel us
                    Log($"hop -> ({best.tx:F0},{best.tz:F0}) [{dist:F0}y] prey={prey.Count} threats={threats.Count}{(_preySeen.Count > 0 && prey.Count == 0 ? $" mem={_preySeen.Count}" : "")}");
                    return;
                }
            }
        }
        // Everything failed repeatedly: BACKTRACK along our own trail — those points were walkable when we
        // stood on them, so pathing back out of a dead-end pocket is the reliable exit (the tip-pocket trap:
        // reversal alone flip-flopped forever while every fan candidate failed CanReach).
        if (++_boxed >= 3 && _trail.Count > 0)
        {
            var back = _trail[0];
            _trail.Clear();
            nav.MoveTo(back.x, back.z);
            if (nav.IsMoving)
            {
                _boxed = 0;
                Log($"pocket-trapped @({w.X:F0},{w.Z:F0}) — backtracking along trail to ({back.x:F0},{back.z:F0})");
                return;
            }
        }
        // THREAT-PINNED last resort: every candidate grazes a known threat's bubble (a ring of con-5s), so the
        // strict passes rejected everything — and flip-flopping the heading forever means standing still in the
        // middle of them, which is strictly worse than walking OUT past a 15y sight cone at full HP with a
        // healer. Take the reachable full-length hop that maximizes the path's closest approach to any threat;
        // if something bites en route, the caller's aggro defense fights it.
        if (threats.Count > 0)
        {
            (double ang, float tx, float tz, float clear) best = (double.NaN, 0, 0, -1f);
            for (int i = 0; i < 8; i++)
            {
                double ang = _heading + i * (Math.PI / 4);
                float tx = w.X + (float)Math.Cos(ang) * hop, tz = w.Z + (float)Math.Sin(ang) * hop;
                if (!nav.CanReach(tx, w.Y, tz)) continue;
                float clear = threats.Min(t => SegDist(w.X, w.Z, tx, tz, t.X, t.Z));
                if (clear > best.clear) best = (ang, tx, tz, clear);
            }
            if (!double.IsNaN(best.ang))
            {
                nav.MoveTo(best.tx, best.tz);
                if (nav.IsMoving)
                {
                    _heading = best.ang;
                    Log($"threat-pinned @({w.X:F0},{w.Z:F0}) by {threats.Count} threats — least-bad hop -> ({best.tx:F0},{best.tz:F0}), clearance {best.clear:F0}y");
                    return;
                }
            }
        }
        _heading += Math.PI;   // truly boxed in (walls every direction) — turn around
        Log($"boxed in @({w.X:F0},{w.Z:F0}) ({threats.Count} known threats near) — reversing heading");
        // TERMINAL ESCAPE: reversing forever is a freeze (the THF looped here 45 minutes — one camped
        // threat vetoed every fan candidate and the straight-line CanReach probes all failed in a mesh
        // pocket). MoveTo runs FULL pathfinding — it can route out where radial probes can't — and one
        // walk past a single mob's aggro bubble at full HP beats an infinite idle. Target remembered prey
        // ground (or the trail origin as a fallback), ungated.
        if (++_desperate >= 10)
        {
            _desperate = 0;
            var anchor = _preySeen.Count > 0
                ? _preySeen.OrderBy(m => Geometry.Dist2D(m.x, m.z, w.X, w.Z)).Cast<(float x, float z)?>().FirstOrDefault()
                : _trail.Count > 0 ? _trail[0] : null;
            if (anchor is { } a2)
            {
                Log($"boxed-in TERMINAL — forcing pathfinder walk to ({a2.x:F0},{a2.z:F0}) through the veto");
                nav.MoveTo(a2.x, a2.z);
            }
            else
            {
                // NO ANCHOR (no remembered prey, no trail — arrived straight into the pocket): scan far rings
                // for ANY reachable point and full-path to it. This is what was missing — with both memories
                // empty the escape used to no-op and the bot looped forever (THF wedged 15 min at a rim).
                bool escaped = false;
                foreach (float r in new[] { 120f, 80f, 200f })
                {
                    for (int i = 0; i < 16 && !escaped; i++)
                    {
                        double ang = i * (Math.PI / 8);
                        float tx = w.X + (float)Math.Cos(ang) * r, tz = w.Z + (float)Math.Sin(ang) * r;
                        if (!nav.CanReach(tx, w.Y, tz)) continue;
                        nav.MoveTo(tx, tz);
                        if (nav.IsMoving) { _heading = ang; escaped = true; Log($"boxed-in TERMINAL (no anchor) — escaping to reachable ({tx:F0},{tz:F0}) at {r:F0}y"); }
                    }
                    if (escaped) break;
                }
                if (!escaped) Log("boxed-in TERMINAL (no anchor) — no reachable point in 200y; truly walled");
            }
        }
    }

    IEnumerable<Entity> NearbyMobs() =>
        p.World.Entities.Values.ToArray().Where(e => e.IsMob && e.Hpp > 0 && p.DistanceTo(e.X, e.Z) < 60f);   // snapshot: parse thread mutates concurrently

    // Distance from point (px,pz) to segment (ax,az)-(bx,bz): does this hop's path graze a threat?
    static float SegDist(float ax, float az, float bx, float bz, float px, float pz)
    {
        float dx = bx - ax, dz = bz - az;
        float len2 = dx * dx + dz * dz;
        if (len2 < 0.001f) return Geometry.Dist2D(ax, az, px, pz);
        float t = Math.Clamp(((px - ax) * dx + (pz - az) * dz) / len2, 0f, 1f);
        return Geometry.Dist2D(ax + t * dx, az + t * dz, px, pz);
    }
}
