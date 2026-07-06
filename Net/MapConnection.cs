using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace XiHeadless.Net;

/// Persistent map-server session: keeps the UDP connection alive, decrypts/decompresses/
/// splits inbound into WorldState, and frames/compresses/encrypts outbound. Runs a recv
/// loop + a send loop (keepalive 0x015). Brains drive it through the capability layer,
/// which talks to it via ISession (State + Enqueue) — there is no separate verb surface.
public sealed class MapConnection : ISession
{
    const int PH = 28; // FFXI_HEADER_SIZE
    static readonly bool Dbg = Environment.GetEnvironmentVariable("XI_DEBUG") == "1";

    readonly Socket _udp;
    readonly uint _charId;
    readonly byte[] _sessionKey;     // 20-byte key; md5 -> blowfish hash. key[4] (LE @16) +=2 each zone.
    FfxiBlowfish _bf;                // rebuilt on every zone change (server increments its copy too)
    readonly FfxiDecompress _dec;
    readonly FfxiCompress _comp;

    /// Fired after a zone change completes, with the new zone id (for navmesh reload).
    public event Action<ushort>? ZoneChanged;

    public WorldState State { get; } = new();

    ushort _clientId;       // our outgoing packet counter
    ushort _serverId;       // highest server packet id we've acked
    readonly object _sendLock = new();
    readonly List<byte[]> _outQueue = new(); // pending sub-packets (each: full sub-packet incl header)
    volatile bool _running;

    public MapConnection(IPEndPoint mapServer, uint charId, byte[] sessionKey, string resDir)
    {
        _charId = charId;
        _sessionKey = (byte[])sessionKey.Clone();
        _bf = BuildBlowfish();
        _dec = new FfxiDecompress(Path.Combine(resDir, "decompress.dat"));
        _comp = new FfxiCompress(Path.Combine(resDir, "compress.dat"));
        _mapServer = mapServer;
        _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // 1 MB SO_RCVBUF: the zone-in char-stream burst overflows the default kernel buffer and the OS
        // silently discards datagrams — the small event (0x032/0x034) frames that fire right after a
        // zone-in (home-point / new-char / gate-proximity cutscenes) were prime casualties, so their
        // event id never reached the parser (w.EventId stayed 0 -> generic auto-finish inert).
        _udp.ReceiveBufferSize = 1 << 20;
        _udp.Connect(mapServer); // matches the proven M1 path (Connect + Send/Receive)
    }

    readonly IPEndPoint _mapServer;

    // Mirrors the server's initBlowfish(): md5 of the 20-byte session key, then zero-truncate
    // the hash at its first zero byte, then key-schedule. (map_session.cpp initBlowfish)
    FfxiBlowfish BuildBlowfish()
    {
        var h = MD5.HashData(_sessionKey.AsSpan(0, 20));
        for (int i = 0; i < 16; i++) if (h[i] == 0) { for (int k = i; k < 16; k++) h[k] = 0; break; }
        return new FfxiBlowfish(h);
    }

    // Mirrors incrementBlowfish(): key[4] (the 5th uint32 = bytes 16-19 LE) += 2, then rebuild.
    void IncrementBlowfish()
    {
        uint k4 = BinaryPrimitives.ReadUInt32LittleEndian(_sessionKey.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(_sessionKey.AsSpan(16), k4 + 2);
        _bf = BuildBlowfish();
    }

    // ---- zone-in: SYNCHRONOUS send+receive on one thread (matches the proven M1/spike path),
    // BEFORE any background loops, to avoid a two-thread race on the socket. ----
    public bool ZoneInSync(int timeoutMs = 12000) => Handshake0x0A(timeoutMs);

    // The 0x00A double-send handshake, used for both the initial zone-in and every later
    // zone change. Sends 0x00A repeatedly (1st creates the session, a later one flushes char
    // data) until 0x00A comes back (InZone), then queues the 0x00C gameok. Runs on whichever
    // thread owns the socket reads at the time (initial: main; re-zone: the recv loop).
    bool Handshake0x0A(int timeoutMs)
    {
        _udp.ReceiveTimeout = 350;
        // 64 KB to match RecvLoop: this is the ONLY receive path during login AND every zone-in re-key —
        // the "login window" where the event (0x032) burst arrives. At 8192 a large zone-in/cutscene
        // datagram threw MessageSize, was swallowed as a timeout below, and vanished (no md5/decompress/
        // socket-error trace) — the root of the 0x032 reception gap. (Was left behind when RecvLoop hardened.)
        var buf = new byte[65536];
        ushort seq = 0;
        State.InZone = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && !State.InZone)
        {
            seq++;
            var login = BuildLogin0x0A(seq);
            _udp.Send(login);                                            // first creates session; later one flushes
            if (Dbg && seq <= 6) Console.WriteLine($"    [zonein] sent 0x00A seq {seq} to {_mapServer}");
            try
            {
                int n = _udp.Receive(buf);                               // inline receive between sends
                if (n > PH + 16) { try { HandleInbound(buf[..n]); } catch (Exception ex) { if (Dbg) Console.WriteLine($"    [recv-err] {ex.Message}"); } }
            }
            catch (SocketException sx) { if (sx.SocketErrorCode != SocketError.TimedOut) Console.WriteLine($"[zonein-sockerr] {sx.SocketErrorCode} — handshake datagram DROPPED (a lost event burst reads as a reception gap)"); }
        }
        _clientId = (ushort)(seq + 1);
        if (State.InZone) Enqueue(BuildGameOk());
        return State.InZone;
    }

    volatile bool _pendingZoneChange;   // set when a 0x0B ZONECHANGE is seen in the recv loop
    volatile bool _zoning;              // true while re-handshaking (pauses the send loop)
    int _gameOkCycles;                  // gameok resend counter; reset each zone-in
    bool _statusRequested;              // sent the 0x061 status request yet (this zone)?

    // c2s 0x061 GP_CLI_COMMAND_CLISTATUS: hdr(4) unknown00@4 padding@5. 8 bytes = 2 words. The server
    // replies via SendLocalPlayerPackets -> CLISTATUS2 0x062 (SKILLS), recasts, merits. Without this
    // request the bot never receives its skill levels.
    static byte[] BuildStatusRequest()
    {
        var p = new byte[8];
        SubPacket.WriteHeader(p, 0x061);
        return p; // unknown00 = 0
    }

    // Server sent 0x0B ZONECHANGE: it has already incremented its blowfish key (after encrypting
    // that packet), so we do the same, drop stale entities, and re-run the 0x00A handshake on the
    // new key. Same UDP socket/endpoint (single-process map server). Runs on the recv thread.
    void DoZoneChange()
    {
        _pendingZoneChange = false;
        _zoning = true;
        Console.WriteLine("[zone] server requested zone change -> re-key + re-zone");
        ushort prevZone = State.ZoneId;
        State.InZone = false;
        State.Entities.Clear();
        _gameOkCycles = 0;                 // re-arm gameok resends for the new zone
        _statusRequested = false;          // re-request skills/status after the new zone-in
        IncrementBlowfish();
        bool ok = Handshake0x0A(12000);
        _udp.ReceiveTimeout = 1000;
        _zoning = false;
        if (ok) { Console.WriteLine($"[zone] {prevZone} -> {State.ZoneId}"); ZoneChanged?.Invoke(State.ZoneId); }
        else Console.WriteLine("[zone] re-zone FAILED (no 0x00A reply)");
    }

    // 0x00C gameok: 12 bytes = hdr(4) + ClientState@4(u32,=0) + DebugClientFlg@8(u32,=0).
    // validate() requires ClientState==0 && DebugClientFlg==0, so the body fields are required.
    // Triggers the server to send the full char stream (JOB_INFO 0x01b, CLISTATUS 0x061, sync 0x067...).
    static byte[] BuildGameOk()
    {
        var p = new byte[12];
        SubPacket.WriteHeader(p, 0x00C);
        return p; // ClientState + DebugClientFlg already zero
    }

    byte[] BuildLogin0x0A(ushort seq)
    {
        var d = new byte[136];
        BinaryPrimitives.WriteUInt16LittleEndian(d, seq);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(PH), 0x000A);
        d[PH + 1] = 0x2E;
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(PH + 2), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(PH + 0x0C), _charId);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(PH + 0x58), 2);
        int sum = 0; for (int i = PH + 8; i < PH + 92; i++) sum += d[i];
        d[PH + 4] = (byte)(sum & 0xFF);
        MD5.HashData(d.AsSpan(PH, d.Length - PH - 16)).CopyTo(d, d.Length - 16);
        return d;
    }

    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public void Start()
    {
        _running = true;
        new Thread(RecvLoop) { IsBackground = true, Name = "xi-recv" }.Start();
        new Thread(SendLoop) { IsBackground = true, Name = "xi-send" }.Start();
    }

    int _stopping;

    public void Stop()
    {
        if (System.Threading.Interlocked.Exchange(ref _stopping, 1) == 1) return;
        // Clean logout: send 0x0E7 reqlogout (Mode=LogoutOn, Kind=Logout). The server applies a
        // LEAVEGAME effect then runs the full logout (shuttingDown=1, char save) and only then is
        // accounts_sessions DELETEd. The whole procedure takes ~30s; if we disconnect early the row
        // falls to the slow MAX_TIME_LASTUPDATE d/c timeout instead -> ORPHANED session -> the next
        // login fails 0xA2 on the unique-accid constraint. So hold the connection (SendLoop keeps the
        // 0x015 keepalive flowing, so we're not flagged link-dead) for the full logout. It's ~30s of
        // in-game logout time which works out to ~40s real before the session is safely cleared
        // (user-confirmed from watching it in-game), so hold 40s.
        if (State.InZone)
        {
            Enqueue(BuildReqLogout());
            if (Dbg) Console.WriteLine("    [logout] sent 0x0E7, holding session 40s for the full logout to complete");
            Thread.Sleep(40000);
        }
        _running = false;
    }

    static byte[] BuildReqLogout()
    {
        // 0x0E7: hdr(4) Mode@4 Kind@6 -> 8B = 2 words. Mode=LogoutOn(1), Kind=Logout(1).
        var p = new byte[8];
        SubPacket.WriteHeader(p, 0x0E7);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), 0x01);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), 0x01);
        return p;
    }

    // ---- inbound: decrypt -> decompress -> split -> dispatch ----
    void RecvLoop()
    {
        _udp.ReceiveTimeout = 1000;
        // 64 KB read buffer: an inbound datagram larger than this makes Socket.Receive throw MessageSize and
        // (below) the whole frame was silently dropped — an event (0x032) riding a large zone-in/cutscene
        // datagram vanished with NO md5-fail and NO decompress-trunc, exactly the observed signature.
        var buf = new byte[65536];
        while (_running)
        {
            State.NowMs = _clock.ElapsedMilliseconds; // runtime clock for entity aging / repath timing
            int n;
            try { n = _udp.Receive(buf); }
            catch (SocketException sx) { if (sx.SocketErrorCode != SocketError.TimedOut) Console.WriteLine($"[recv-sockerr] {sx.SocketErrorCode} — datagram DROPPED (a lost event frame reads as a reception gap)"); continue; }
            if (Dbg) Console.WriteLine($"    [recv] {n}B head={Convert.ToHexString(buf.AsSpan(0, Math.Min(8, n)))}");
            if (n <= PH + 16) continue;
            var pkt = buf[..n];
            try { HandleInbound(pkt); } catch (Exception ex) { if (Dbg) Console.WriteLine($"    [recv-err] {ex.Message}"); }
            if (_pendingZoneChange) DoZoneChange();   // re-key + re-zone on this same thread
        }
    }

    void HandleInbound(byte[] pkt)
    {
        // track server packet id from header[0:2] for our ack
        ushort sid = BinaryPrimitives.ReadUInt16LittleEndian(pkt);
        // DIAG: a SMALL forward jump in server seq = we missed the in-between datagram(s); the lockstep server
        // treats our advanced ack as proof of delivery and won't resend them, so any 0x032 there is lost.
        // Guarded to skip normal zone-in sequence RESETS (e.g. 176->1, which is not a gap). Remove once fixed.
        if (sid != 0 && _serverId != 0 && sid > _serverId + 1 && sid - _serverId < 256)
            Console.WriteLine($"[ack-gap] server sid {_serverId} -> {sid} (skipped {sid - _serverId - 1} datagram(s)) — un-acked frames incl. any 0x032 lost here");
        if (sid != 0) _serverId = sid;

        _bf.DecipherBuffer(pkt, PH);
        var bodyMd5 = MD5.HashData(pkt.AsSpan(PH, pkt.Length - PH - 16));
        if (!bodyMd5.AsSpan().SequenceEqual(pkt.AsSpan(pkt.Length - 16))) { Console.WriteLine($"    [md5-fail] sid={sid} len={pkt.Length} — datagram DROPPED (an event frame lost here reads as a reception gap)"); return; }
        if (Dbg) Console.WriteLine($"    [decrypted-ok] sid={sid} len={pkt.Length}");
        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(pkt.AsSpan(pkt.Length - 20));
        var dec = _dec.Decompress(pkt.AsSpan(PH), bits);

        int off = 0;
        var ids = new List<string>();   // DIAG: full boundary trace so a desync (overrun) can be dumped
        while (off + 4 <= dec.Length)
        {
            ushort hdr = BinaryPrimitives.ReadUInt16LittleEndian(dec.AsSpan(off));
            var (id, words) = SubPacket.Parse(hdr);
            if (words == 0)
            {
                if (Dbg || dec.Length - off > 8)
                {
                    Console.WriteLine($"      [split-stop words=0 @{off}/{dec.Length} hdr=0x{hdr:x4} id=0x{id:x3} tailrem={dec.Length - off}]");
                    Console.WriteLine($"      [desync-walk {dec.Length}B] {string.Join(",", ids.Count > 22 ? ids.GetRange(ids.Count - 22, 22) : ids)}");
                    int hs0 = Math.Max(0, off - 24), he0 = Math.Min(dec.Length, off + 16);
                    Console.WriteLine($"      [desync-hex @{hs0}] {Convert.ToHexString(dec.AsSpan(hs0, he0 - hs0))}");
                }
                break;
            }
            int len = words * 4;
            // DIAGNOSTIC (event reception): if an event-start opcode reaches this loop but no "[event] start"
            // follows, the bug is parse/dispatch; if it NEVER reaches here while ServerStatus flips to 4, the
            // datagram was dropped upstream (socket/md5/decompress). Remove once reception is confirmed stable.
            if (id is 0x032 or 0x033 or 0x034) Console.WriteLine($"[recv-event-opcode] 0x{id:x3} len={len} off={off}/{dec.Length} sid={sid} status={State.ServerStatus}");
            if (off + len > dec.Length)
            {
                // id==0 here is the BENIGN 4-byte end-of-frame trailer (00 80 00 00 = hdr 0x8000) — expected
                // padding, not a desync; stay silent. A NON-zero id overrunning is a genuine framing desync:
                // dump the boundary trace + hex so the mis-sized sub-packet is visible.
                if (id != 0)
                {
                    Console.WriteLine($"      [split-stop overrun @{off} id=0x{id:x3} len={len} rem={dec.Length - off}]");
                    Console.WriteLine($"      [desync-walk {dec.Length}B] {string.Join(",", ids.Count > 22 ? ids.GetRange(ids.Count - 22, 22) : ids)}");
                    int hs = Math.Max(0, off - 24), he = Math.Min(dec.Length, off + 16);
                    Console.WriteLine($"      [desync-hex @{hs}] {Convert.ToHexString(dec.AsSpan(hs, he - hs))}");
                }
                break;
            }
            // 0x0B GP_SERV_COMMAND_LOGOUT: LogoutState@body0 == 2 (ZONECHANGE) -> re-zone after this packet.
            if (id == 0x00B && off + 5 <= dec.Length && dec[off + 4] == 2) _pendingZoneChange = true;
            ids.Add($"@{off}:0x{id:x3}.{words}w");
            PacketParsers.Dispatch(id, dec.AsSpan(off, len), State);
            off += len;
        }
        if (Dbg && ids.Count > 0) Console.WriteLine($"      [in sid={sid} {dec.Length}B] {string.Join(",", ids)}");
    }

    // ---- outbound: drain queue -> frame -> compress -> size -> md5 -> encrypt ----
    void SendLoop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_running)
        {
            Thread.Sleep(400); // ~client cadence
            if (!State.InZone || _zoning) continue; // don't send while unzoned or mid re-zone handshake
            // Resend 0x00C gameok for the first few in-zone cycles to beat startup ack-instability
            // (the server only flushes the queued char data once our ack is in sync). Reset on each zone.
            if (++_gameOkCycles <= 4) Enqueue(BuildGameOk());
            // Once the char stream has flushed, request local-player status to get SKILLS (0x062) etc.
            if (_gameOkCycles == 3 && !_statusRequested) { _statusRequested = true; Enqueue(BuildStatusRequest()); }
            SendQueued((uint)sw.ElapsedMilliseconds);
        }
    }

    public void Enqueue(byte[] subPacket)
    {
        lock (_sendLock) _outQueue.Add(subPacket);
    }

    // Add several sub-packets under one lock so SendQueued can't drain the queue between them — they
    // land in the same frame, consecutively (after the leading 0x015). Lets ordered pairs like the
    // 0x084->0x085 vendor-sell satisfy the server's PacketGuard "arriving in correct order" check.
    public void EnqueueAtomic(params byte[][] subPackets)
    {
        lock (_sendLock) foreach (var sp in subPackets) _outQueue.Add(sp);
    }

    void SendQueued(uint timeNow)
    {
        List<byte[]> batch;
        lock (_sendLock)
        {
            batch = new List<byte[]>(_outQueue);
            _outQueue.Clear();
        }
        // always include a position/keepalive 0x015 so the session never times out
        batch.Insert(0, BuildPos());

        // assemble sub-packets into a body, stamping each with our sequence at [2:4]
        var body = new List<byte>();
        foreach (var sp in batch)
        {
            _clientId++;
            BinaryPrimitives.WriteUInt16LittleEndian(sp.AsSpan(2), _clientId);
            body.AddRange(sp);
        }
        var bodyArr = body.ToArray();
        if (Dbg)
        {
            var ids = new List<string>();
            int o = 0; while (o + 4 <= bodyArr.Length) { ushort h = BinaryPrimitives.ReadUInt16LittleEndian(bodyArr.AsSpan(o)); var (sid2, wd) = SubPacket.Parse(h); if (wd == 0) break; ids.Add($"0x{sid2:x3}"); o += wd * 4; }
            Console.WriteLine($"    [send] subs=[{string.Join(",", ids)}] clientId={_clientId} ack={_serverId}");
        }

        // out buffer: [28 header][compressed + 4B size][16B md5]
        var comp = new byte[bodyArr.Length * 2 + 32];
        int bits = _comp.Compress(bodyArr, comp);
        // Server's byte length = zlib_compressed_size(bits) = (bits+7)/8 — the out[0]=1
        // header byte is already INCLUDED in this count (do NOT add +1, or the size field,
        // md5, and cipher boundaries shift by one and the server silently drops the packet).
        int compBytes = (bits + 7) / 8;
        int sizeFieldPos = compBytes;
        var outPkt = new byte[PH + compBytes + 4 + 16];
        Array.Copy(comp, 0, outPkt, PH, compBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(outPkt.AsSpan(PH + sizeFieldPos), (uint)bits);
        // header
        BinaryPrimitives.WriteUInt16LittleEndian(outPkt.AsSpan(0), _clientId);
        BinaryPrimitives.WriteUInt16LittleEndian(outPkt.AsSpan(2), _serverId);
        BinaryPrimitives.WriteUInt32LittleEndian(outPkt.AsSpan(8), timeNow);
        // md5 over [compressed + size], then encipher payload region
        int payloadLen = compBytes + 4;
        MD5.HashData(outPkt.AsSpan(PH, payloadLen)).CopyTo(outPkt, PH + payloadLen);
        int cypher = ((payloadLen + 16) / 4) & ~1;
        _bf.EncipherBuffer(outPkt, PH, cypher * 4);
        try { _udp.Send(outPkt); } catch { }
    }

    byte[] BuildPos()
    {
        // 0x015 GP_CLI_COMMAND_POS. Server expects PacketSize[0x015]=0x10 → SmallPD_Size=16 →
        // 8 words = 32 bytes (NOT 28; the server validates the exact size or rejects "Bad packet size").
        // CRITICAL field order (0x015_pos.cpp process: newY=z@8, newZ=y@12, "Not a typo"):
        // the server reads the VERTICAL (loc.p.y) from @8 and horizontal-z (loc.p.z) from @12.
        // So @8 must carry our vertical (State.Y) and @12 our horizontal (State.Z). Swapping these
        // put the bot's height = its Z coord -> hundreds of yalms in the air (clipping walls).
        var p = new byte[32];
        SubPacket.WriteHeader(p, 0x015);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(4), State.X);   // -> loc.p.x
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(8), State.Y);   // -> loc.p.y (VERTICAL)
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(12), State.Z);  // -> loc.p.z (horizontal)
        p[20] = (byte)State.Rotation;                       // dir
        p[21] = (byte)(State.Moving ? 0x06 : 0x04);         // flags: GroundMode(0x04) + RunMode(0x02) when moving
        return p;
    }

}
