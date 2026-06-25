using XiHeadless.Capabilities;

namespace XiHeadless.Brains;

/// Reusable Auction House buying. The server charges the EXACT bid and only fills if a listing <= bid
/// exists, so we escalate low -> high and stop the instant the item lands in inventory (paying close to
/// market, never overpaying). Must be in a MISC_AH zone. Shared by the crafter and the gear-up phase.
public static class ShopRoutines
{
    static readonly uint[] BidLadder = { 50, 250, 1000, 4000, 15000 };

    public static bool HasItem(IPerception p, ushort itemId)
    {
        foreach (var ((container, _), id) in p.World.Inventory)
            if (container == 0 && id == itemId) return true;
        return false;
    }

    /// Buy one itemId from the AH (single then stack at each price rung). True once it's in inventory.
    public static async Task<bool> BuyFromAH(IAuctionHouse ah, IPerception p, ushort itemId, CancellationToken ct = default)
    {
        if (HasItem(p, itemId)) return true;
        foreach (var bid in BidLadder)
        {
            foreach (var single in new[] { true, false })
            {
                if (bid > p.World.Gil) { Console.WriteLine($"[ah] bid {bid} > gil {p.World.Gil} — out of budget"); return HasItem(p, itemId); }
                ah.Bid(itemId, bid, single);
                Console.WriteLine($"[ah] bid {bid} for {itemId} (single={single})");
                for (int i = 0; i < 6 && !ct.IsCancellationRequested; i++)
                {
                    await Task.Delay(500, ct);
                    if (HasItem(p, itemId)) { Console.WriteLine($"[ah] acquired {itemId} for <= {bid}"); return true; }
                }
            }
        }
        return HasItem(p, itemId);
    }
}
