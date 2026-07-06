using System.Buffers.Binary;

namespace XiHeadless.Net;

/// Centralizes the FFXI sub-packet 16-bit header at offset 0: `id | (words << 9)`, where
/// words = byteLength/4 (sub-packets are 4-byte aligned). Deriving the word count from the
/// buffer length (never a hand-counted literal) means a mis-sized packet can't silently
/// desync the frame — a wrong word count makes the server DROP the whole datagram.
public static class SubPacket
{
    /// Writes the sub-packet header (id + length-derived word count) at offset 0. The word
    /// count comes from p.Length/4, so it can NEVER drift from the actual buffer size.
    public static void WriteHeader(Span<byte> p, int id)
        => BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)(id | ((p.Length / 4) << 9)));

    /// byte[] overload (the common call site: a freshly-allocated fixed-size array).
    public static void WriteHeader(byte[] p, int id) => WriteHeader(p.AsSpan(), id);

    /// Splits an inbound sub-packet header into its opcode id (9 bits) and word count (7 bits).
    public static (int id, int words) Parse(ushort hdr) => (hdr & 0x1ff, (hdr >> 9) & 0x7f);
}
