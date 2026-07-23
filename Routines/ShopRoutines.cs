using XiHeadless.Capabilities;

namespace XiHeadless.Routines;

/// Reusable economy coroutines. BuyItem(itemId) escalates the bid low->high, reads the server's result
/// packet (0x04C), and on a full inventory (result 0xE5) frees space (default: sell junk; brains pass
/// SellNearby for a vendor trip) then retries — so "go buy XXX" just works (must be in a MISC_AH zone).
/// SellNearby finds the nearest vendor and sells all junk for gil. Shared by the crafter and the WAR loop.
public static class ShopRoutines
{
    // Fine-grained rungs = price DISCOVERY: the bot stops at the FIRST winning bid, so small steps mean
    // it pays just above the actual lowest current listing instead of overshooting (the old {50,250,1000}
    // ladder bought a ~300g item at the 1000 rung). Tight 50-100g steps low (where low-level gear/mats
    // live) widening to ~30% steps high. Failed rungs resolve fast (server 0xC5), so more rungs is cheap.
    static readonly uint[] BidLadder =
        { 50, 100, 150, 200, 300, 400, 500, 700, 900, 1200, 1600, 2200, 3000, 4200, 6000, 8500, 12000 };

    /// A free-space callback that never sells (returns 0). For buy sites with no vendor reachable — the AH
    /// buy just fails on a full bag rather than trekking off to sell. Shared: brains/routines had a private
    /// copy each.
    public static Task<int> NoFree(CancellationToken _) => Task.FromResult(0);

    /// Bank drops for gil with ONE call, no shop hardcoded: find the nearest standard vendor (current zone
    /// first, else fewest zone-hops away — from the data-driven Vendors registry), travel to it, open its
    /// shop, and sell everything not in `keep`. Returns the number of items sold (0 if no vendor is
    /// reachable, we couldn't get there, or the shop wouldn't open — in which case the items are kept).
    public static async Task<int> SellNearby(IShop shop, INavigation nav, IZoning zoning, IInventory inv,
                                             IPerception p, IReadOnlySet<ushort> keep, CancellationToken ct = default)
    {
        if (Game.Vendors.Nearest(p.World.ZoneId) is not { } v)
        {
            Log.Info($"[sell] no known vendor reachable from zone {p.World.ZoneId} — keeping items");
            return 0;
        }
        if (p.World.ZoneId != v.Zone)
        {
            Log.Info($"[sell] traveling to {Game.Zonelines.Name(v.Zone)} to sell at {v.Name}");
            if (!await zoning.GoTo(Game.Zonelines.Name(v.Zone), ct))
            {
                Log.Info($"[sell] couldn't reach {Game.Zonelines.Name(v.Zone)} — keeping items");
                return 0;
            }
            await Task.Delay(2000, ct);   // let inventory/position resettle after the zone change
        }
        Log.Info($"[sell] walking to {v.Name} at ({v.X:F0},{v.Z:F0})");
        await NavRoutines.WalkTo(nav, p, v.X, v.Z, within: 5f, ct, y: v.Y, legTimeoutMs: 60_000);
        var stock = await shop.Open(v.NpcId, ct);
        if (stock.Count == 0)
        {
            Log.Info($"[sell] {v.Name}'s shop wouldn't open — keeping items");
            return 0;
        }
        return await inv.SellAllJunk(keep, ct);
    }

    /// Buy one itemId from the AH. Escalates (single then stack at each price rung), parses the AH result,
    /// frees a slot via inv.DropJunk(keep) on inventory-full, and stops the instant the item is owned.
    /// `keep` = item ids the bot must NOT drop (its gear); pass the bot's gear set so clearing only dumps junk.
    /// `freeSpace` (optional): how to make room when the bag is full (server result 0xE5). Defaults to a
    /// plain in-place junk sell, but a brain should pass `ct => SellNearby(...)` so a full bag triggers a
    /// trip to the nearest vendor (the in-place sell only works if a shop is already open).
    public static async Task<bool> BuyItem(IAuctionHouse ah, IPerception p, IInventory inv, ushort itemId,
                                           IReadOnlySet<ushort> keep,
                                           Func<CancellationToken, Task<int>>? freeSpace = null,
                                           CancellationToken ct = default)
    {
        if (inv.Has(itemId)) return true;
        bool soldForGil = false;
        foreach (var bid in BidLadder)
        {
            // BROKE-PLAYER RULE: short on gil with junk in the bag = sell FIRST, then bid. Without this the
            // funding loop deadlocks — junk-selling waited for a full bag while every bid read "out of
            // budget" (Gibra: 10 gil + 156 failed bids + a bag of unsold drops = a songless BRD all session).
            if (bid > p.World.Gil && !soldForGil && freeSpace != null)
            {
                soldForGil = true;   // one funding trip per purchase — junk is finite
                Log.Info($"[ah] bid {bid} > gil {p.World.Gil} — selling junk to fund the buy");
                await freeSpace(ct);
            }
            if (bid > p.World.Gil) { Log.Info($"[ah] bid {bid} > gil {p.World.Gil} — out of budget"); break; }
            foreach (var single in new[] { true, false })
            {
                // Up to 2 tries at this (bid, single): a first 0xE5 triggers a drop, then we re-bid.
                for (int attempt = 0; attempt < 2 && !ct.IsCancellationRequested; attempt++)
                {
                    p.World.AucResult = 0;
                    ah.Bid(itemId, bid, single);
                    Log.Info($"[ah] bid {bid} for {itemId} (single={single})");
                    int r = await WaitResult(p, inv, itemId, ct);
                    if (inv.Has(itemId)) { Log.Info($"[ah] acquired {itemId} for <= {bid}"); return true; }
                    if (r == 0xE5)   // inventory full — clear ALL sellable junk for gil in one pass, then retry
                    {                 // (full clear, not one slot, so we don't thrash sell-one/buy-one/full-again)
                        int sold = freeSpace != null ? await freeSpace(ct) : await inv.SellAllJunk(keep, ct);
                        if (sold == 0) { Log.Info($"[ah] inventory full and nothing sellable — cannot buy {itemId}"); return false; }
                        continue;     // plenty of room now; retry the bid
                    }
                    break;   // 0xC5 (no listing <= this bid) -> escalate to the next rung; or no result -> next stack
                }
            }
        }
        return inv.Has(itemId);
    }

    /// Buy UP TO `count` of a stackable item (consumables like powders). BuyItem stops at the first single
    /// (it only guarantees you own >=1), which caps stealth supplies at 1 powder. This keeps acquiring —
    /// STACK bid first (one stack ~= 12, efficient), then single — until the held quantity reaches `count`
    /// or nothing more is listed/affordable. Required for SUSTAINED Invis (Maintain re-applies, burning powders).
    public static async Task<bool> BuyAtLeast(IAuctionHouse ah, IPerception p, IInventory inv, ushort itemId, int count,
                                              IReadOnlySet<ushort> keep,
                                              Func<CancellationToken, Task<int>>? freeSpace = null,
                                              CancellationToken ct = default)
    {
        int CountOf() => inv.CountOf(itemId);
        bool soldForGil = false;
        while (CountOf() < count && !ct.IsCancellationRequested)
        {
            int before = CountOf();
            bool progressed = false;
            foreach (var bid in BidLadder)
            {
                // Same broke-player funding rule as BuyItem: one junk-sale trip before giving up on gil.
                if (bid > p.World.Gil && !soldForGil && freeSpace != null)
                {
                    soldForGil = true;
                    Log.Info($"[ah] bid {bid} > gil {p.World.Gil} — selling junk to fund the buy");
                    await freeSpace(ct);
                }
                if (bid > p.World.Gil) { Log.Info($"[ah] bid {bid} > gil {p.World.Gil} — stop ({CountOf()}/{count} of {itemId})"); return CountOf() >= count; }
                foreach (var single in new[] { false, true })   // stack first (efficient), then single
                {
                    p.World.AucResult = 0;
                    ah.Bid(itemId, bid, single);
                    int r = await WaitResult(p, inv, itemId, ct);
                    if (CountOf() > before) { Log.Info($"[ah] bought {itemId} stack={!single} -> {CountOf()}/{count}"); progressed = true; break; }
                    if (r == 0xE5) { int sold = freeSpace != null ? await freeSpace(ct) : await inv.SellAllJunk(keep, ct); if (sold == 0) return CountOf() >= count; }
                }
                if (progressed) break;   // got some at this rung; re-loop to check the count
            }
            if (!progressed) { Log.Info($"[ah] nothing more of {itemId} listed/affordable ({CountOf()}/{count})"); break; }
        }
        return CountOf() >= count;
    }

    // Wait for the AH result (0x04C) or the item to land in inventory. Returns the result byte
    // (0x01 bought / 0xC5 no-listing / 0xE5 inventory-full) or 0 on timeout.
    static async Task<int> WaitResult(IPerception p, IInventory inv, ushort itemId, CancellationToken ct)
    {
        for (int i = 0; i < 10 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(400, ct);
            if (inv.Has(itemId)) return 0x01;
            if (p.World.AucResult != 0) return p.World.AucResult;
        }
        return 0;
    }
}
