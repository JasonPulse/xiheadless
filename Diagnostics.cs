using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XiHeadless;

/// Offline dev/test subcommands (crypto round-trips, spell resolution, packet decrypt).
/// Returns an exit code if it handled a dev command, else null.
public static class Diagnostics
{
    public static int? Run(string[] args)
    {
        if (args.Length >= 1 && args[0] == "intake")
        {
            // Offline check of the RMT storefront: serve it, GET the page, POST an order, confirm queued.
            var intake = new XiHeadless.Brains.RmtIntake(8088);
            intake.Start();
            using var http = new System.Net.Http.HttpClient();
            var page = http.GetStringAsync("http://localhost:8088/").GetAwaiter().GetResult();
            Console.WriteLine($"GET / -> {page.Length} bytes; has order form: {page.Contains("Place order")}; title: {page.Contains("Network Gnomes Gil")}");
            var form = new System.Net.Http.StringContent("player=Milliennial&amount=500000", Encoding.UTF8, "application/x-www-form-urlencoded");
            var resp = http.PostAsync("http://localhost:8088/", form).GetAwaiter().GetResult();
            Console.WriteLine($"POST order (form) -> {(int)resp.StatusCode}; confirmation page: {resp.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("Order received")}");
            Console.WriteLine(intake.TryDequeue(out var o) ? $"queued: {o.amount} gil -> '{o.player}'" : "queue empty (FAIL)");
            return 0;
        }

        if (args.Length >= 1 && args[0] == "dr-reflect")
        {
            var asm = typeof(DotRecast.Detour.Io.DtMeshSetReader).Assembly; // force-load
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
            foreach (var tn in new[] { "DotRecast.Detour.Io.DtMeshSetReader", "DotRecast.Detour.Io.DtMeshDataReader", "DotRecast.Detour.DtNavMesh" })
            {
                var t = asm.GetType(tn) ?? typeof(DotRecast.Detour.DtNavMesh).Assembly.GetType(tn);
                Console.WriteLine($"== {tn} {(t == null ? "(NOT FOUND)" : "")}==");
                if (t == null) continue;
                foreach (var m in t.GetMethods(F))
                    if (m.Name is "Read" or "AddTile" or "Init" or "Read32Bit" or "Read64Bit")
                        Console.WriteLine($"  {(m.IsPublic ? "pub" : "int")} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
            }
            return 0;
        }

        if (args.Length >= 2 && args[0] == "nav-test")
        {
            var nav = XiHeadless.Navigation.NavMesh.Load(args[1]);
            // path between two FFXI points (pass as args[2..7] or use Bastok Markets-ish defaults)
            float[] p = args.Length >= 8
                ? args[2..8].Select(float.Parse).ToArray()
                : new[] { -177f, -8f, -28f, -120f, -8f, 40f };
            var st = nav.SelfTest();
            Console.WriteLine($"nav-test: loaded {args[1]}");
            Console.WriteLine($"  self-test (random on-mesh points): polys={st.polys} waypoints={st.waypoints}");
            Console.WriteLine($"  random A(detour)=({st.a.X:F1},{st.a.Y:F1},{st.a.Z:F1})  B=({st.b.X:F1},{st.b.Y:F1},{st.b.Z:F1})");
            // settle the FFXI->detour transform: try variants on the spawn point (-177,-8,-28.4)
            float sx = p[0], sy = p[1], sz = p[2];
            foreach (var (lbl, dx, dy, dz) in new[] { ("(x,-y,-z)", sx, -sy, -sz), ("(x,y,z)", sx, sy, sz), ("(x,-y,z)", sx, -sy, sz), ("(x,y,-z)", sx, sy, -sz) })
            {
                var n = nav.NearestDetour(dx, dy, dz);
                Console.WriteLine($"  transform {lbl}: found={n.found} at=({n.at.X:F1},{n.at.Y:F1},{n.at.Z:F1}) dist={n.dist:F1}");
            }
            var path = nav.FindPath(p[0], p[1], p[2], p[3], p[4], p[5]);
            Console.WriteLine($"  FFXI path ({p[0]},{p[1]},{p[2]})->({p[3]},{p[4]},{p[5]}): {path.Count} waypoints");
            foreach (var w in path.Take(8)) Console.WriteLine($"    -> ({w.x:F1}, {w.y:F1}, {w.z:F1})");
            return path.Count > 0 ? 0 : 2;
        }

        if (args.Length >= 1 && args[0] == "zone-test")
        {
            // 1) BFS routes over the Bastok-region zone graph.
            void Route(ushort a, ushort b)
            {
                var r = XiHeadless.Game.Zonelines.Route(a, b);
                Console.WriteLine(r is null
                    ? $"  {a} -> {b}: UNREACHABLE"
                    : $"  {a} -> {b}: {r.Count} hop(s): {string.Join(" -> ", new[] { a }.Concat(r.Select(h => h.To)))}  [rects: {string.Join(",", r.Select(h => h.RectId))}]");
            }
            Console.WriteLine("zone routes:");
            Route(235, 234);   // Markets -> Mines (1 hop)
            Route(235, 106);   // Markets -> North Gustaberg (multi-hop via Port Bastok)
            Route(235, 107);   // Markets -> South Gustaberg
            Route(234, 236);   // Mines -> Port Bastok
            Route(235, 999);   // unreachable

            // 2) 0x5E zone-line request packet build + decode.
            var w = new WorldState { ZoneId = 235, X = -201.9f, Y = 1.9f, Z = -194.8f };
            var sent = new List<byte[]>();
            var z = new Zoning(new TestSession(w, sent), new StubNav());
            z.RequestZoneLine(812267130);
            var p = sent[^1];
            ushort hdr = BinaryPrimitives.ReadUInt16LittleEndian(p);
            Console.WriteLine($"0x5E: id=0x{hdr & 0x1ff:x3} words={(hdr >> 9) & 0x7f} bytes={p.Length} " +
                $"rect={BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4))} " +
                $"pos=({BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(8)):F1},{BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(12)):F1},{BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(16)):F1}) " +
                $"exitBit={p[22]} exitMode={p[23]}");
            bool ok = (hdr & 0x1ff) == 0x05E && ((hdr >> 9) & 0x7f) == 6 && p.Length == 24
                      && BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4)) == 812267130 && p[22] == 0 && p[23] == 0;
            Console.WriteLine(ok ? "0x5E OK" : "0x5E MALFORMED");
            return ok ? 0 : 2;
        }

        if (args.Length >= 1 && args[0] == "magic-test")
        {
            // Simulate a mage that knows Fire/FireII/FireIII (ids 144/145/146) and Cure/CureII.
            var w = new WorldState { KnownSpellBits = new byte[256] };
            void Learn(Spell sp) { ushort id = (ushort)sp; w.KnownSpellBits[id >> 3] |= (byte)(1 << (id & 7)); }
            Learn(Spell.Fire); Learn(Spell.FireII); Learn(Spell.FireIII);
            Learn(Spell.Cure); Learn(Spell.CureII);

            var sent = new List<byte[]>();
            var fake = new TestSession(w, sent);
            var tmagic = new Magic(fake);
            var tcombat = new Combat(fake);
            Console.WriteLine($"Known FireII? {tmagic.Known(Spell.FireII)}  Highest Fire(known) = {tmagic.Highest(SpellLine.Fire)}  Lowest = {tmagic.Lowest(SpellLine.Fire)}");
            uint Last(int field) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sent[^1].AsSpan(field));
            uint LastSpell() => Last(12);
            // MP gating: CastHighest casts the highest tier we can AFFORD
            w.Mp = 100; tmagic.CastHighest(SpellLine.Fire, 7); Console.WriteLine($"MP=100 -> CastHighest(Fire) casts {(Spell)LastSpell()}");
            w.Mp = 30;  tmagic.CastHighest(SpellLine.Fire, 7); Console.WriteLine($"MP=30  -> CastHighest(Fire) casts {(Spell)LastSpell()} (FireIII unaffordable)");
            w.Mp = 0;   int before = sent.Count; tmagic.CastHighest(SpellLine.Fire, 7); Console.WriteLine($"MP=0   -> CastHighest(Fire) sent {(sent.Count > before ? "a spell" : "nothing (correct)")}");
            // WS/ability enums -> action packet
            // async now, but the packet is enqueued synchronously before the first await, so sent[^1] is set.
            w.Tp = 1000; _ = tcombat.WeaponSkill(WeaponSkill.Combo, 9);
            Console.WriteLine($"CanWeaponSkill@1000TP={tcombat.CanWeaponSkill}  WeaponSkill(Combo) -> cat=0x{System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(sent[^1].AsSpan(10)):x2} id={Last(12)}");
            _ = tcombat.Ability(Ability.Provoke, 9);
            Console.WriteLine($"Ability(Provoke) -> cat=0x{System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(sent[^1].AsSpan(10)):x2} id={Last(12)} (35=Provoke)");
            return 0;
        }

        if (args.Length >= 1 && args[0] == "outbound-test")
        {
            // Build a full outbound packet (0x015 pos + 0x00C gameok), then run it back through the
            // oracle-verified server-style decrypt/decompress. If framing is server-valid it recovers
            // the original body. Isolates encrypt/compress/size/md5/cipher-boundary correctness.
            const int PH = 28;
            var resDir = Path.Combine(AppContext.BaseDirectory, "res");
            var comp = new FfxiCompress(Path.Combine(resDir, "compress.dat"));
            var dec = new FfxiDecompress(Path.Combine(resDir, "decompress.dat"));
            var key = new byte[16]; for (int i = 0; i < 16; i++) key[i] = (byte)(i + 1); // any key (round-trip)
            var bf = new FfxiBlowfish(key);

            var pos = new byte[28]; System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(pos, (ushort)(0x015 | (7 << 9)));
            var ok = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(ok, (ushort)(0x00C | (1 << 9)));
            var body = new byte[pos.Length + ok.Length]; pos.CopyTo(body, 0); ok.CopyTo(body, pos.Length);

            var c = new byte[body.Length * 2 + 32];
            int bits = comp.Compress(body, c);
            int compBytes = (bits + 7) / 8;
            var pkt = new byte[PH + compBytes + 4 + 16];
            Array.Copy(c, 0, pkt, PH, compBytes);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(PH + compBytes), (uint)bits);
            int payloadLen = compBytes + 4;
            System.Security.Cryptography.MD5.HashData(pkt.AsSpan(PH, payloadLen)).CopyTo(pkt, PH + payloadLen);
            int cypher = ((payloadLen + 16) / 4) & ~1;
            bf.EncipherBuffer(pkt, PH, cypher * 4);
            Console.WriteLine($"built outbound: bodyLen={body.Length} bits={bits} compBytes={compBytes} pktLen={pkt.Length}");

            // server-style decrypt
            bf.DecipherBuffer(pkt, PH);
            var md5 = System.Security.Cryptography.MD5.HashData(pkt.AsSpan(PH, pkt.Length - PH - 16));
            bool md5ok = md5.AsSpan().SequenceEqual(pkt.AsSpan(pkt.Length - 16));
            uint sz = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pkt.AsSpan(pkt.Length - 20));
            var back = dec.Decompress(pkt.AsSpan(PH), sz, body.Length + 32);
            bool same = back.AsSpan().SequenceEqual(body);
            Console.WriteLine($"decrypt: md5ok={md5ok} sizeField={sz} recovered={back.Length}B match={same}");
            if (same) { int id0 = back[0] | ((back[1] & 1) << 8); int id1 = back[28] | ((back[29] & 1) << 8); Console.WriteLine($"  sub-packets: 0x{id0:x3} 0x{id1:x3}"); }
            return (md5ok && same) ? 0 : 2;
        }

        if (args.Length >= 2 && args[0] == "compress-test")
        {
            var orig = File.ReadAllBytes(args[1]);
            var resDir = Path.Combine(AppContext.BaseDirectory, "res");
            var comp = new byte[orig.Length * 2 + 32];
            int bits = new FfxiCompress(Path.Combine(resDir, "compress.dat")).Compress(orig, comp);
            var back = new FfxiDecompress(Path.Combine(resDir, "decompress.dat")).Decompress(comp, (uint)bits, orig.Length + 16);
            bool same = back.AsSpan().SequenceEqual(orig);
            Console.WriteLine($"compress-test: bits={bits} compBytes={(bits + 7) / 8 + 1} roundtrip={same} (orig={orig.Length}B, back={back.Length}B)");
            return same ? 0 : 2;
        }

        if (args.Length >= 1 && args[0] == "dumps")
        {
            FfxiBlowfish.DebugDumpS(51, 292, 567, 836, 0, 1, 1023);
            return 0;
        }

        if (args.Length >= 1 && args[0] == "firstenc")
        {
            var k = new byte[20]; k[16] = 6;
            var hh = System.Security.Cryptography.MD5.HashData(k);
            for (int i = 0; i < 16; i++) if (hh[i] == 0) { for (int z = i; z < 16; z++) hh[z] = 0; break; }
            FfxiBlowfish.DebugFirstEncipher(hh);
            return 0;
        }

        if (args.Length >= 1 && args[0] == "dumpkey")
        {
            var sk0 = new byte[20]; sk0[16] = 6;
            var h0 = System.Security.Cryptography.MD5.HashData(sk0);
            Console.WriteLine($"hash={Convert.ToHexString(h0)}");
            for (int i = 0; i < 16; i++) if (h0[i] == 0) { for (int k = i; k < 16; k++) h0[k] = 0; break; }
            Console.WriteLine(new FfxiBlowfish(h0).Dump());
            return 0;
        }

        if (args.Length >= 2 && args[0] == "decrypt-test")
        {
            var raw = File.ReadAllBytes(args[1]);
            int plen = raw[0] | (raw[1] << 8);
            var pkt = raw[2..(2 + plen)];
            var sk = new byte[20]; sk[16] = 6;                       // session_key: zeros, byte16=6 (just-created)
            var h = System.Security.Cryptography.MD5.HashData(sk);   // key = md5(session_key)...
            for (int i = 0; i < 16; i++) if (h[i] == 0) { for (int k = i; k < 16; k++) h[k] = 0; break; } // ...zero-truncated
            new FfxiBlowfish(h).DecipherBuffer(pkt, 28);
            var bodyMd5 = System.Security.Cryptography.MD5.HashData(pkt.AsSpan(28, plen - 28 - 16));
            bool ok = bodyMd5.AsSpan().SequenceEqual(pkt.AsSpan(plen - 16));
            uint usz = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pkt.AsSpan(plen - 20));
            Console.WriteLine($"decrypt-test: md5ok={ok} size_field={usz} body[0:16]={Convert.ToHexString(pkt.AsSpan(28, 16))}");
            // full pipeline: decompress and (optionally) compare to oracle output
            var datPath = Path.Combine(AppContext.BaseDirectory, "res", "decompress.dat");
            var dec = new FfxiDecompress(datPath).Decompress(pkt.AsSpan(28), usz);
            int firstId = dec[0] | ((dec[1] & 1) << 8);
            Console.WriteLine($"decompressed={dec.Length} bytes  first sub-packet id=0x{firstId:x3} size_words={dec[1] >> 1}");
            Console.WriteLine($"out[0:32]={Convert.ToHexString(dec.AsSpan(0, Math.Min(32, dec.Length)))}");
            if (args.Length >= 3)
            {
                var oracle = File.ReadAllBytes(args[2]);
                bool same = oracle.AsSpan().SequenceEqual(dec);
                Console.WriteLine($"oracle match: {same} (oracle={oracle.Length}B, ours={dec.Length}B)");
            }
            // split into sub-packets [id:9 | size:7 words] and confirm clean tiling
            Console.WriteLine("-- sub-packets --");
            int off = 0, npk = 0;
            while (off + 4 <= dec.Length)
            {
                ushort hdr = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(dec.AsSpan(off));
                int id = hdr & 0x1ff, words = (hdr >> 9) & 0x7f;
                if (words == 0) break; // padding / end
                Console.WriteLine($"  @{off,4}  id=0x{id:x3}  {words * 4}B");
                off += words * 4;
                npk++;
            }
            Console.WriteLine($"-- {npk} sub-packets, consumed {off}/{dec.Length} bytes --");
            return ok ? 0 : 2;
        }
        return null;
    }
}

// Minimal ISession for offline capability tests (captures enqueued packets).
sealed class TestSession(WorldState state, System.Collections.Generic.List<byte[]> sent) : ISession
{
    public WorldState State => state;
    public void Enqueue(byte[] subPacket) => sent.Add(subPacket);
}

/// No-op navigation for offline zone-request tests (zoning's packet build doesn't move).
sealed class StubNav : INavigation
{
    public bool IsMoving => false;
    public void MoveTo(float x, float z) { }
    public void MoveTo(float x, float y, float z) { }
    public void Follow(uint entityId) { }
    public void Face(uint entityId) { }
    public void Stop() { }
}
