using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XiHeadless.Net;

sealed class XiClient(string host, string clientVer)
{
    const int PH = 28; // FFXI_HEADER_SIZE (0x1C)

    readonly IPAddress _serverIp = Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork);

    uint _accountId;
    byte[] _sessionHash = [];
    uint _charId;
    string _charName = "";
    IPEndPoint _mapServer = new(IPAddress.Any, 0);

    public IPEndPoint MapServer => _mapServer;
    public uint CharId => _charId;
    public string CharName => _charName;

    NetworkStream _dataStream = null!;
    NetworkStream _viewStream = null!;

    // ---- little-endian helpers ----
    static void U16(byte[] b, int off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(off), v);
    static void U32(byte[] b, int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(off), v);
    static ushort RU16(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt16LittleEndian(b[off..]);
    static uint RU32(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt32LittleEndian(b[off..]);
    static void PackStr(byte[] b, int off, string s) => Encoding.ASCII.GetBytes(s).CopyTo(b, off);

    // outer packet MD5 over body[PH .. len-16], written into the trailing 16 bytes
    static void PacketMd5(byte[] d) => MD5.HashData(d.AsSpan(PH, d.Length - PH - 16)).CopyTo(d, d.Length - 16);

    static int ReadSome(Stream s, byte[] buf)
    {
        try { return s.Read(buf, 0, buf.Length); } catch (IOException) { return 0; }
    }

    public async Task LoginAsync(string user, string pass)
    {
        Console.WriteLine($"login -> {host} ({_serverIp}):54231 (TLS)");
        var tcp = new TcpClient();
        await tcp.ConnectAsync(_serverIp, 54231);
        var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            // macOS .NET rejects explicitly pinning Tls13; None lets the OS negotiate up to 1.3.
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.None,
        });

        string json = $"{{\"username\":\"{user}\",\"password\":\"{pass}\",\"otp\":0,\"new_password\":\"\",\"version\":[2,0,0],\"command\":16}}";
        var jb = Encoding.UTF8.GetBytes(json);
        ssl.Write(jb);

        var buf = new byte[8192];
        int n = ssl.Read(buf, 0, buf.Length);
        ssl.Close();
        var reply = Encoding.UTF8.GetString(buf, 0, n).Replace("'", "\"");
        using var doc = JsonDocument.Parse(reply);
        var root = doc.RootElement;
        if (!root.TryGetProperty("result", out var r) || r.GetInt32() != 1)
            throw new Exception($"login failed: {reply}");
        _accountId = root.GetProperty("account_id").GetUInt32();
        _sessionHash = root.GetProperty("session_hash").EnumerateArray().Select(e => (byte)e.GetInt32()).ToArray();
        Console.WriteLine($"  ok: account_id={_accountId}, sessionHash={_sessionHash.Length}B");
    }

    public void LobbyDataConnect()
    {
        Console.WriteLine($"lobby data -> {_serverIp}:54230");
        var data = new TcpClient();
        data.Connect(_serverIp, 54230);
        _dataStream = data.GetStream();
        _dataStream.ReadTimeout = 6000;

        // hand session hash back: [0]=0xFE, hash @ 12
        var p = new byte[28];
        p[0] = 0xFE;
        _sessionHash.CopyTo(p, 12);
        _dataStream.Write(p);

        // 0xA1 (0) — this first one carries the session hash
        Send_0xA1(withHash: true);

        Console.WriteLine($"lobby view -> {_serverIp}:54001");
        var view = new TcpClient();
        view.Connect(_serverIp, 54001);
        _viewStream = view.GetStream();
        _viewStream.ReadTimeout = 6000;
    }

    void Send_0xA1(bool withHash)
    {
        // [0]=0xA1, accid@1, serverIP@5, (0xA1_0 only) hash@12
        var p = new byte[28];
        p[0] = 0xA1;
        U32(p, 1, _accountId);
        _serverIp.GetAddressBytes().CopyTo(p, 5);
        if (withHash) _sessionHash.CopyTo(p, 12);
        _dataStream.Write(p);
    }

    // initial char-list exchange advances the lobby state machine before create
    public void InitialCharList() => FetchCharList();

    byte[] ViewPkt(byte op, int size)
    {
        var p = new byte[size];
        p[8] = op;
        _sessionHash.CopyTo(p, 12);
        return p;
    }

    public void LobbyView_0x26()
    {
        var p = ViewPkt(0x26, 152);
        PackStr(p, 116, clientVer);
        _viewStream.Write(p);
        var resp = new byte[40];
        ReadSome(_viewStream, resp);
        Console.WriteLine($"  0x26 ok: expansions=0x{RU32(resp, 32):X}, features=0x{RU16(resp, 36):X}");
    }

    public void LobbyView_0x1F() => _viewStream.Write(ViewPkt(0x1F, 44));

    static readonly Random Rng = new();

    // 0x22 reserve a (globally-unique) name. We confirm success by re-selecting, not by the result.
    void ReserveName(string name)
    {
        var rsv = ViewPkt(0x22, 152);   // name @ 32
        PackStr(rsv, 32, name);
        _viewStream.Write(rsv);
        var r = new byte[0x20];
        _viewStream.ReadExactly(r, 0, 0x20); // reply is fixed 0x20
        Console.WriteLine($"  0x22 reserve '{name}': result={r[8]}");
    }

    // 0x21 create the reserved char with a randomized appearance (race@48 job@50 nation@54 size@57
    // face@60). Random race/face + random nation give visual variety and distribute bots across the
    // three starting cities (ambiance); job = WAR (combat brains assume it; job-change is future).
    void CreateCharBody()
    {
        var cr = ViewPkt(0x21, 152);
        cr[48] = (byte)(1 + Rng.Next(8));   // race+gender 1..8
        cr[50] = 1;                         // job: WAR
        cr[54] = (byte)Rng.Next(3);         // nation 0..2 (starting city)
        cr[57] = (byte)Rng.Next(3);         // size 0..2
        cr[60] = (byte)(1 + Rng.Next(8));   // face 1..8
        _viewStream.Write(cr);
        var r = new byte[0x20];
        _viewStream.ReadExactly(r, 0, 0x20);
        Console.WriteLine($"  0x21 create: result={r[8]}");
    }

    public void DeleteAllChars()
    {
        var view = FetchCharList();
        for (int slot = 0; slot < 16; slot++)
        {
            int b = 36 + slot * 140;
            if (b + 140 > view.Length) break;
            uint cid = RU32(view, b);
            if (cid == 0) continue;
            string nm = Encoding.ASCII.GetString(view, b + 8, 16).Split('\0')[0].Trim();
            var p = ViewPkt(0x14, 152);     // 0x14 delete: charID @ 0x20
            U32(p, 0x20, cid);
            _viewStream.Write(p);
            var r = new byte[0x20];
            try { _viewStream.ReadExactly(r, 0, 0x20); } catch { }
            Console.WriteLine($"  deleted {nm} ({cid}): result={r[8]}");
            Thread.Sleep(200);
        }
    }

    // The char-list reply is variable length; drain until the stream goes idle.
    byte[] FetchCharList()
    {
        Send_0xA1(withHash: false);
        DrainRead(_dataStream, 1200);          // data-sock ack (size varies)
        return DrainRead(_viewStream, 1200);   // view char-list payload
    }

    static byte[] DrainRead(NetworkStream s, int idleMs)
    {
        var buf = new byte[4096];
        var acc = new List<byte>();
        s.ReadTimeout = idleMs;
        try
        {
            // Read until the stream goes idle for idleMs. Do NOT stop on a short read: the
            // char-list payload can arrive across multiple TCP segments, and breaking on the
            // first partial read returned an incomplete (or empty, if the first read raced the
            // payload) list — which upstream misread as "char doesn't exist" and tried to CREATE
            // over a globally-unique existing name. Assemble everything; the idle timeout ends it.
            while (true)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                acc.AddRange(buf.AsSpan(0, n).ToArray());
            }
        }
        catch (IOException) { /* idle timeout = end of payload */ }
        return acc.ToArray();
    }

    /// Send the 0x07 char-select for a given (charId, name).
    void WriteSelect(uint cid, string name)
    {
        _charId = cid;
        _charName = name;
        var sel = ViewPkt(0x07, 88);     // 0x07 select: charId@28, name@36
        U32(sel, 28, cid);
        PackStr(sel, 36, name);
        _viewStream.Write(sel);
    }

    /// Find the account's character (highest-charid NAMED slot — skips unnamed/junk slots) and
    /// select it. Returns true if one was found and selected.
    bool TrySelectBest(byte[] view)
    {
        uint bestCid = 0; string bestName = "";
        for (int slot = 0; slot < 16; slot++)
        {
            int b = 36 + slot * 140;
            if (b + 140 > view.Length) break;
            uint cid = RU32(view, b);
            if (cid == 0 || cid < bestCid) continue;
            string nm = Encoding.ASCII.GetString(view, b + 8, 16).Split('\0')[0].Trim();
            if (nm.Length == 0) continue;   // unnamed/incomplete slot (junk) — can't select it
            bestCid = cid; bestName = nm;
        }
        if (bestCid == 0) return false;
        WriteSelect(bestCid, bestName);
        return true;
    }

    /// Select the account's character, or CREATE one if the account is empty — one-step fleet
    /// deploy: account + password + brain, no char name (a fantasy name is generated). Returns true
    /// if a char was just created (=> session-key byte16 += 6 for the fresh char).
    ///
    /// The char-list read is retry-hardened: a raced (empty/partial) read must NOT be mistaken for
    /// an empty account and create-over an existing char (names are globally unique). So we retry
    /// the read; only a genuinely empty account (no char after repeated solid reads) provisions one.
    public bool SelectOrCreate()
    {
        for (int i = 1; i <= 5; i++)
        {
            if (TrySelectBest(FetchCharList())) { Console.WriteLine($"  selected char id={_charId} '{_charName}' (read attempt {i})"); return false; }
            Console.WriteLine($"  no char in char-list (attempt {i}/5)");
            if (i < 5) Thread.Sleep(500);
        }
        Console.WriteLine("  account is empty -> provisioning a character");
        return CreateChar();
    }

    /// Provision a NEW character: generated fantasy name + randomized appearance, retrying on a name
    /// collision, then select it. Returns true (justCreated). Used by the empty-account deploy path
    /// AND the `provision` tool. Verifies success by re-reading the list and selecting the new char.
    public bool CreateChar()
    {
        for (int t = 1; t <= 5; t++)
        {
            var name = NameGen.Next();
            Console.WriteLine($"  creating char '{name}' (try {t}/5)");
            ReserveName(name);
            CreateCharBody();
            if (TrySelectBest(FetchCharList())) { Console.WriteLine($"  created + selected char id={_charId} '{_charName}'"); return true; }
        }
        throw new Exception($"failed to create a character on account {_accountId} after 5 tries");
    }

    public void RequestZoneServer()
    {
        Thread.Sleep(2000); // mirror the client's pre-0xA2 pause
        var p = new byte[25];
        p[0] = 0xA2;
        _dataStream.Write(p);
        // The 0xA2 reply is 0x48 (72B) on success or 0x24 (36B) on an error (e.g. session state).
        // Read length-agnostically rather than assuming a size.
        var resp = new byte[256];
        _viewStream.ReadTimeout = 6000;
        int n = _viewStream.Read(resp, 0, resp.Length);
        if (n < 0x48)
            throw new Exception($"0xA2 returned a {n}B error packet (0x24) — server declined the zone handoff " +
                                "(likely a stale/duplicate session). Wait for it to time out or use a fresh char.");
        var zoneIp = new IPAddress(resp[0x38..0x3C]);
        ushort zonePort = RU16(resp, 0x3C);
        _mapServer = new IPEndPoint(zoneIp, zonePort);
        Console.WriteLine($"  zone server = {_mapServer}");
        if (zonePort == 0) throw new Exception("null zone handoff (0.0.0.0) — char-select did not register");
    }
}

/// Generates a character name for fleet auto-provisioning: a fleet-tag prefix + pronounceable
/// fantasy syllables, first letter capitalized, letters only, <= 15 chars. Edit Prefix to taste
/// (it makes the bots' chars easy to spot/manage on the server).
static class NameGen
{
    const string Prefix = "Zz";
    static readonly Random Rng = new();
    static readonly string[] Onset = { "b", "d", "f", "g", "k", "l", "m", "n", "r", "s", "t", "v", "z", "th", "sh", "br", "dr", "gr" };
    static readonly string[] Nucleus = { "a", "e", "i", "o", "u", "ae", "ia", "ou", "ar", "en" };

    public static string Next()
    {
        var sb = new StringBuilder(Prefix.ToLowerInvariant());
        int syllables = 2 + Rng.Next(2);   // 2-3 syllables
        for (int i = 0; i < syllables; i++)
        {
            sb.Append(Onset[Rng.Next(Onset.Length)]);
            sb.Append(Nucleus[Rng.Next(Nucleus.Length)]);
        }
        var s = sb.ToString();
        if (s.Length > 15) s = s[..15];
        return char.ToUpperInvariant(s[0]) + s[1..];
    }
}
