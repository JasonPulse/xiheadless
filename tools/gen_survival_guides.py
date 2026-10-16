#!/usr/bin/env python3
# Regenerates Game/Generated/SurvivalGuides.cs from the LSB server's npc_list.sql (READ-ONLY reference).
# Survival Guides are the fleet's party FORMING points (user rule 2026-10-16: groups form only at a Survival
# Guide or just outside a town, never at a hunting camp). npcid encodes the zone like mob ids.
# Usage: python3 tools/gen_survival_guides.py [path-to-npc_list.sql]
import re, sys, os
sql = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser('~/Code/Lua/Personal/xiserver/sql/npc_list.sql')
rows = []
for line in open(sql, errors='replace'):
    m = re.search(r"VALUES \((\d+),'Survival_Guide','[^']*',\d+,(-?[\d.]+),(-?[\d.]+),(-?[\d.]+),", line)
    if not m: continue
    zone = (int(m.group(1)) >> 12) & 0xFFF
    rows.append((zone, float(m.group(2)), float(m.group(3)), float(m.group(4))))
dst = os.path.join(os.path.dirname(__file__), '..', 'Game', 'Generated', 'SurvivalGuides.cs')
with open(dst, 'w') as f:
    f.write("namespace XiHeadless.Game;\n\n")
    f.write("/// GENERATED from xiserver sql/npc_list.sql by tools/gen_survival_guides.py — do not hand-edit.\n")
    f.write("/// Survival Guide positions (zone, x, y=vertical, z): where parties FORM (user rule 2026-10-16).\n")
    f.write("public static class SurvivalGuides\n{\n")
    f.write("    public static readonly (ushort zone, float x, float y, float z)[] All =\n    {\n")
    for z, x, y, zz in sorted(rows):
        f.write(f"        ({z}, {x:.1f}f, {y:.1f}f, {zz:.1f}f),\n")
    f.write("    };\n\n")
    f.write("    /// The Survival Guide in `zone` nearest (x, z), or null when the zone has none.\n")
    f.write("    public static (float x, float y, float z)? Nearest(ushort zone, float x, float z) =>\n")
    f.write("        All.Where(g => g.zone == zone).OrderBy(g => (g.x - x) * (g.x - x) + (g.z - z) * (g.z - z))\n")
    f.write("           .Select(g => ((float x, float y, float z)?)(g.x, g.y, g.z)).FirstOrDefault();\n")
    f.write("}\n")
print(f"wrote {len(rows)} survival guides -> {dst}")
