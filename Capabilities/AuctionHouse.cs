using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

/// Auction House (buy side). Bid on an item at a price; if a listing <= the bid exists, the server
/// puts the item straight into inventory and deducts the bid from gil. Needs gil + to be at an AH
/// zone. (Selling = LotIn, not built.)
public interface IAuctionHouse
{
    void Bid(ushort itemId, uint price, bool single = true);   // single=true buys 1, false buys a stack
}

/// Builds 0x04E GP_CLI_COMMAND_AUC, Bid command. Layout: hdr(4) Command@4 AucWorkIndex@5 Result@6
/// ResultStatus@7, Param.Bid { BidPrice@8(u32) ItemNo@12(u16) pad@14 ItemStacks@16(u32) }, Parcel@20
/// (zeroed). 60 bytes = 15 words (PacketSize[0x04E]=0x1E). ItemStacks: 1=single item, 0=full stack.
internal static class AucPacket
{
    const byte Bid = 0x0E;

    public static byte[] BuildBid(ushort itemId, uint price, bool single)
    {
        var p = new byte[60];
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)(0x04E | (15 << 9)));
        p[4] = Bid;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), price);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(12), itemId);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(16), single ? 1u : 0u);
        return p;
    }
}

public sealed class AuctionHouse(ISession s) : IAuctionHouse
{
    public void Bid(ushort itemId, uint price, bool single = true) => s.Enqueue(AucPacket.BuildBid(itemId, price, single));
}
