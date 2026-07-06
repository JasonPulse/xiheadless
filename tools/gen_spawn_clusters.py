#!/usr/bin/env python3
# Regenerates Game/Generated/SpawnClusters.cs from the LSB server's mob_spawn_points.sql
# (READ-ONLY reference; this fork's dump carries min/max level per spawn row).
# Usage: python3 tools/gen_spawn_clusters.py [path-to-mob_spawn_points.sql]
import re, sys, collections, os
sql = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser('~/Code/Lua/Personal/xiserver/sql/mob_spawn_points.sql')
rows=[]
for line in open(sql):
    m=re.match(r"INSERT INTO `mob_spawn_points` VALUES \((\d+),\d+,'([^']+)','[^']*',(\d+),(\d+),(\d+),(-?[\d.]+),(-?[\d.]+),(-?[\d.]+),", line)
    if not m: continue
    mobid=int(m.group(1)); zone=(mobid>>12)&0xFFF
    rows.append((zone, m.group(2), int(m.group(4)), int(m.group(5)), float(m.group(6)), float(m.group(8))))
cells=collections.defaultdict(list)
for z,n,lo,hi,x,zz in rows: cells[(z,n,lo,hi,round(x/80),round(zz/80))].append((x,zz))
out=sorted((z,n,lo,hi,sum(p[0] for p in pts)/len(pts),sum(p[1] for p in pts)/len(pts),len(pts)) for (z,n,lo,hi,_,_),pts in cells.items())
dst=os.path.join(os.path.dirname(__file__),'..','Game','Generated','SpawnClusters.cs')
with open(dst,'w') as f:
    f.write("namespace XiHeadless.Game;\n\n/// GENERATED from xiserver sql/mob_spawn_points.sql (this fork carries min/max level per spawn).\n/// 80y cluster centroids per (zone, mob name, level band) — the data-driven answer to \"where do\n/// level-appropriate mobs live\" for EVERY zone. Regenerate with tools/gen_spawn_clusters.py.\npublic static class SpawnClusters\n{\n    public readonly record struct Cluster(ushort Zone, string Name, byte Min, byte Max, float X, float Z, byte Count);\n\n    /// Cluster centroids in `zone` for mobs whose level band overlaps [lvlLo, lvlHi], plus any mob\n    /// whose name contains one of extraNames (droppers, any level). Steering data only — the live\n    /// /check con at engage time remains the sole arbiter of what to fight.\n    public static IEnumerable<(float x, float z)> For(ushort zone, int lvlLo, int lvlHi, params string[] extraNames) =>\n        All.Where(c => c.Zone == zone && ((c.Min <= lvlHi && c.Max >= lvlLo)\n                || extraNames.Any(n => c.Name.Contains(n, StringComparison.OrdinalIgnoreCase))))\n           .Select(c => (c.X, c.Z));\n\n    public static readonly Cluster[] All =\n    {\n")
    for z,n,lo,hi,cx,cz,c in out:
        f.write(f"        new({z}, \"{n}\", {min(lo,255)}, {min(hi,255)}, {cx:.0f}f, {cz:.0f}f, {min(c,255)}),\n")
    f.write("    };\n}\n")
print(f"wrote {len(out)} clusters -> {dst}")
