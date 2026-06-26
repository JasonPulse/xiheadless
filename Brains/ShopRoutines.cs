using XiHeadless.Capabilities;

namespace XiHeadless.Brains;

/// Reusable Auction House buying as ONE coroutine: BuyItem(itemId) escalates the bid low->high, reads the
/// server's result packet (0x04C), and when the inventory is full (result 0xE5) drops a junk item to free
/// a slot and retries — so "go buy XXX" just works. Must be in a MISC_AH zone. Shared by the crafter and
/// the WAR gear-up. (Selling drops for gil instead of dropping them is a better long-term move — TODO.)
public static class ShopRoutines
{
    // Fine-grained rungs = price DISCOVERY: the bot stops at the FIRST winning bid, so small steps mean
    // it pays just above the actual lowest current listing instead of overshooting (the old {50,250,1000}
    // ladder bought a ~300g item at the 1000 rung). Tight 50-100g steps low (where low-level gear/mats
    // live) widening to ~30% steps high. Failed rungs resolve fast (server 0xC5), so more rungs is cheap.
    static readonly uint[] BidLadder =
        { 50, 100, 150, 200, 300, 400, 500, 700, 900, 1200, 1600, 2200, 3000, 4200, 6000, 8500, 12000 };

    public static bool HasItem(IPerception p, ushort itemId)
    {
        foreach (var ((container, _), id) in p.World.Inventory)
            if (container == 0 && id == itemId) return true;
        return false;
    }

    /// Buy one itemId from the AH. Escalates (single then stack at each price rung), parses the AH result,
    /// frees a slot via inv.DropJunk(keep) on inventory-full, and stops the instant the item is owned.
    /// `keep` = item ids the bot must NOT drop (its gear); pass the bot's gear set so clearing only dumps junk.
    public static async Task<bool> BuyItem(IAuctionHouse ah, IPerception p, IInventory inv, ushort itemId,
                                           IReadOnlySet<ushort> keep, CancellationToken ct = default)
    {
        if (HasItem(p, itemId)) return true;
        foreach (var bid in BidLadder)
        {
            if (bid > p.World.Gil) { Console.WriteLine($"[ah] bid {bid} > gil {p.World.Gil} — out of budget"); break; }
            foreach (var single in new[] { true, false })
            {
                // Up to 2 tries at this (bid, single): a first 0xE5 triggers a drop, then we re-bid.
                for (int attempt = 0; attempt < 2 && !ct.IsCancellationRequested; attempt++)
                {
                    p.World.AucResult = 0;
                    ah.Bid(itemId, bid, single);
                    Console.WriteLine($"[ah] bid {bid} for {itemId} (single={single})");
                    int r = await WaitResult(p, itemId, ct);
                    if (HasItem(p, itemId)) { Console.WriteLine($"[ah] acquired {itemId} for <= {bid}"); return true; }
                    if (r == 0xE5)   // inventory full — clear ALL sellable junk for gil in one pass, then retry
                    {                 // (full clear, not one slot, so we don't thrash sell-one/buy-one/full-again)
                        int sold = await inv.SellAllJunk(keep, ct);
                        if (sold == 0) { Console.WriteLine($"[ah] inventory full and nothing sellable — cannot buy {itemId}"); return false; }
                        continue;     // plenty of room now; retry the bid
                    }
                    break;   // 0xC5 (no listing <= this bid) -> escalate to the next rung; or no result -> next stack
                }
            }
        }
        return HasItem(p, itemId);
    }

    // Wait for the AH result (0x04C) or the item to land in inventory. Returns the result byte
    // (0x01 bought / 0xC5 no-listing / 0xE5 inventory-full) or 0 on timeout.
    static async Task<int> WaitResult(IPerception p, ushort itemId, CancellationToken ct)
    {
        for (int i = 0; i < 10 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(400, ct);
            if (HasItem(p, itemId)) return 0x01;
            if (p.World.AucResult != 0) return p.World.AucResult;
        }
        return 0;
    }
}
