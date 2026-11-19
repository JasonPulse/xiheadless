#!/usr/bin/env python3
# Regenerates Game/Generated/NoAuction.cs from the LSB server's item_basic.sql (READ-ONLY reference).
# An item the AH can never hold: auction category NONE (aH = 0), or the NOAUCTION (0x0040) or EX (0x4000)
# flag. Bidding on one only climbs the whole ladder for nothing: the fleet bid 112x on an Auriga Xiphos,
# a Rare/Ex NM drop (user 2026-11-19: never bid on Rare/Ex NM gear).
# Usage: python3 tools/gen_no_auction.py [path-to-item_basic.sql]
import re, sys, os
sql = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser('~/Code/Lua/Personal/xiserver/sql/item_basic.sql')
consts, ids = {}, []
for line in open(sql, errors='replace'):
    m = re.match(r"SET @(\w+)\s*=\s*(\d+);", line)
    if m: consts[m.group(1)] = int(m.group(2)); continue
    m = re.search(r"VALUES \((\d+),\d+,'[^']*','[^']*',[@\w]+,\d+,(\d+),@?(\w+),", line)
    if not m: continue
    ah = consts.get(m.group(3), int(m.group(3)) if m.group(3).isdigit() else None)
    if ah == 0 or int(m.group(2)) & 0x4040: ids.append(int(m.group(1)))
ranges = []
for i in sorted(ids):
    if ranges and ranges[-1][1] == i - 1: ranges[-1][1] = i
    else: ranges.append([i, i])
dst = os.path.join(os.path.dirname(__file__), '..', 'Game', 'Generated', 'NoAuction.cs')
with open(dst, 'w') as f:
    f.write("namespace XiHeadless.Game;\n\n")
    f.write("/// GENERATED from xiserver sql/item_basic.sql by tools/gen_no_auction.py. Do not hand-edit.\n")
    f.write("/// Items the AH can never hold (aH NONE, or the NOAUCTION / EX flag), as sorted inclusive id ranges.\n")
    f.write("public static class NoAuction\n{\n")
    f.write("    static readonly (ushort lo, ushort hi)[] Ranges =\n    {\n")
    for k in range(0, len(ranges), 8):
        f.write("        " + " ".join(f"({lo}, {hi})," for lo, hi in ranges[k:k + 8]) + "\n")
    f.write("    };\n\n")
    f.write("    /// True if `itemId` can never be listed on the AH, so a bid on it can never fill.\n")
    f.write("    public static bool Contains(ushort itemId)\n    {\n")
    f.write("        int lo = 0, hi = Ranges.Length - 1;\n")
    f.write("        while (lo <= hi)\n        {\n")
    f.write("            int mid = (lo + hi) / 2;\n")
    f.write("            if (itemId < Ranges[mid].lo) hi = mid - 1;\n")
    f.write("            else if (itemId > Ranges[mid].hi) lo = mid + 1;\n")
    f.write("            else return true;\n")
    f.write("        }\n        return false;\n    }\n}\n")
print(f"wrote {len(ids)} unlistable items in {len(ranges)} ranges -> {dst}")
