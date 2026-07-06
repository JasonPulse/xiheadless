using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

/// Builds the bazaar c2s packets.
internal static class BazaarPacket
{
    // 0x10A BAZAAR_ITEMSET: hdr(4) ItemIndex@4(u8) padding[3]@5 Price@8(u32). 12 bytes = 3 words (PacketSize 0x06).
    public static byte[] ItemSet(byte invSlot, uint price)
    {
        var p = new byte[12];
        SubPacket.WriteHeader(p, 0x10A);
        p[4] = invSlot;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), price);
        return p;
    }

    // 0x109 BAZAAR_OPEN / 0x10B BAZAAR_CLOSE: bodyless (PacketSize 0). 4 bytes = 1 word.
    public static byte[] Bare(ushort id)
    {
        var p = new byte[4];
        SubPacket.WriteHeader(p, id);
        return p;
    }
}

public sealed class Bazaar(ISession s) : IBazaar
{
    public void BeginEdit() => s.Enqueue(BazaarPacket.Bare(0x10B));
    public void SetPrice(byte invSlot, uint price) => s.Enqueue(BazaarPacket.ItemSet(invSlot, price));
    public void Open() => s.Enqueue(BazaarPacket.Bare(0x109));

    // c2s 0x105 BAZAAR_LIST: hdr(4) UniqueNo@4(u32) ActIndex@8(u16) pad@10(u16). 12 bytes = 3 words.
    public void Browse(uint sellerId, ushort targid)
    {
        var p = new byte[12];
        SubPacket.WriteHeader(p, 0x105);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), sellerId);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(8), targid);
        s.Enqueue(p);
    }

    // c2s 0x106 BAZAAR_BUY: hdr(4) BazaarItemIndex@4(u8) pad[3] BuyNum@8(u32). 12 bytes = 3 words.
    public void Buy(byte bazaarSlot, uint count)
    {
        var p = new byte[12];
        SubPacket.WriteHeader(p, 0x106);
        p[4] = bazaarSlot;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), count);
        s.Enqueue(p);
    }

    public void StopBrowsing() => s.Enqueue(BazaarPacket.Bare(0x104));   // c2s 0x104 BAZAAR_EXIT
}
