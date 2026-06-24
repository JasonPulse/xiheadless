using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

/// Personal bazaar: price inventory items for sale, then open the bazaar so other players can buy
/// from the bot where it stands. (Buyer-side packets 0x104/0x105/0x106 are a separate concern.)
public interface IBazaar
{
    // Setup order is BeginEdit -> SetPrice(s) -> Open. (Server semantics, 0x10b/0x109: "close"
    // = enter the Set-Prices menu / hide the bazaar to edit; "open" = exit the menu / show it.)
    void BeginEdit();                          // 0x10B — enter price-editing mode (isSettingBazaarPrices=true)
    void SetPrice(byte invSlot, uint price);   // 0x10A — price an inventory item for sale
    void Open();                               // 0x109 — finish editing and open the bazaar to buyers
}

/// Builds the bazaar c2s packets.
internal static class BazaarPacket
{
    // 0x10A BAZAAR_ITEMSET: hdr(4) ItemIndex@4(u8) padding[3]@5 Price@8(u32). 12 bytes = 3 words (PacketSize 0x06).
    public static byte[] ItemSet(byte invSlot, uint price)
    {
        var p = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)(0x10A | (3 << 9)));
        p[4] = invSlot;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), price);
        return p;
    }

    // 0x109 BAZAAR_OPEN / 0x10B BAZAAR_CLOSE: bodyless (PacketSize 0). 4 bytes = 1 word.
    public static byte[] Bare(ushort id)
    {
        var p = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)(id | (1 << 9)));
        return p;
    }
}

public sealed class Bazaar(ISession s) : IBazaar
{
    public void BeginEdit() => s.Enqueue(BazaarPacket.Bare(0x10B));
    public void SetPrice(byte invSlot, uint price) => s.Enqueue(BazaarPacket.ItemSet(invSlot, price));
    public void Open() => s.Enqueue(BazaarPacket.Bare(0x109));
}
