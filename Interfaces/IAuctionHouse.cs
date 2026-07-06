using System.Buffers.Binary;

namespace XiHeadless.Interfaces;

/// Auction House (buy side). Bid on an item at a price; if a listing <= the bid exists, the server
/// puts the item straight into inventory and deducts the bid from gil. Needs gil + to be at an AH
/// zone. (Selling = LotIn, not built.)
public interface IAuctionHouse
{
    void Bid(ushort itemId, uint price, bool single = true);   // single=true buys 1, false buys a stack
}
