using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

/// Encodes auto-translate phrases written as {Phrase} into the FFXI wire token: 0xFD + LE32(id) + 0xFD
/// (autotranslate.cpp: "0xFD XX YY ZZ AA 0xFD", the 4 middle bytes = the id little-endian). The full
/// client table is ~30k entries; this is the curated subset the bots actually use — add as needed.
/// Unknown phrases pass through as literal "{...}" so a typo is visible rather than silently dropped.
internal static class AutoTranslate
{
    static readonly Dictionary<string, uint> Ids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Gil"] = 4294902279,
        ["Buy?"] = 51511810,
        ["Do you need it?"] = 101843458,
        ["Hello!"] = 184615426,
        ["Do you need any help?"] = 151126530,
        ["Thank you."] = 201392642,
        ["Good luck!"] = 335610370,
        ["Congratulations!"] = 251724290,
    };

    public static byte[] Encode(string msg)
    {
        var outp = new List<byte>(msg.Length + 8);
        int i = 0;
        while (i < msg.Length)
        {
            int open = msg.IndexOf('{', i);
            if (open < 0) { outp.AddRange(System.Text.Encoding.ASCII.GetBytes(msg[i..])); break; }
            outp.AddRange(System.Text.Encoding.ASCII.GetBytes(msg[i..open]));
            int close = msg.IndexOf('}', open + 1);
            if (close < 0) { outp.AddRange(System.Text.Encoding.ASCII.GetBytes(msg[open..])); break; }
            if (Ids.TryGetValue(msg[(open + 1)..close], out var id))
            {
                outp.Add(0xFD);
                var idb = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(idb, id); outp.AddRange(idb);
                outp.Add(0xFD);
            }
            else outp.AddRange(System.Text.Encoding.ASCII.GetBytes(msg[open..(close + 1)]));
            i = close + 1;
        }
        return outp.ToArray();
    }
}

public sealed class Chat(ISession s) : IChat
{
    // 0x0B5 GP_CLI_COMMAND_MESSAGE: hdr(4) type@4 ... text@6. PacketSize 0 (any size ok).
    // The text supports {Phrase} auto-translate tokens (encoded to wire bytes by AutoTranslate).
    void Send(byte mode, string msg)
    {
        var text = AutoTranslate.Encode(msg);
        var p = new byte[(6 + text.Length + 1 + 3) & ~3];   // hdr(4)+type@4+pad@5+text+null, word-padded
        SubPacket.WriteHeader(p, 0x0B5);
        p[4] = mode;
        text.CopyTo(p, 6);
        s.Enqueue(p);
    }
    // CHAT_MESSAGE_TYPE (chat_message_type.h): SAY=0, SHOUT=1, TELL=3, PARTY=4, LINKSHELL=5, YELL=26.
    public void Say(string msg) => Send(0, msg);
    public void Shout(string msg) => Send(1, msg);
    public void Yell(string msg) => Send(26, msg);
    public void Party(string msg) => Send(4, msg);   // was 5 = LINKSHELL (bug); MESSAGE_PARTY is 4
    // 0x0B6 GP_CLI_COMMAND_CHAT_NAME (tells): hdr(4) unk@4 unk@5 sName[15]@6 Mes[]@21. Tells are a
    // DIFFERENT packet from channel chat — sending them as 0x0B5 mode 3 was silently dropped by the
    // server (the REFORM handshake never arrived and the stale-party deadlock persisted).
    public void Tell(string to, string msg)
    {
        var text = AutoTranslate.Encode(msg);
        var p = new byte[(21 + text.Length + 1 + 3) & ~3];
        SubPacket.WriteHeader(p, 0x0B6);
        // The server's 0x0B6 validate() REQUIRES byte4(unknown00)==3 (TELL type) and byte5(unknown01)==0;
        // leaving byte4=0 made the server reject EVERY bot tell at validation (real clients send 3). This is
        // why bot tells never arrived (grant requests + the Reunion REFORM handshake silently dropped).
        p[4] = 3;   // unknown00 = MESSAGE_TELL; p[5] stays 0
        System.Text.Encoding.ASCII.GetBytes(to, 0, System.Math.Min(to.Length, 14), p, 6);
        text.CopyTo(p, 21);
        s.Enqueue(p);
        Log.Info($"[chat] tell -> '{to}': {msg}");
    }
}
