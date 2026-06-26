using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

/// Inventory management. A headless bot grinding for hours fills its 30-slot inventory with mob drops;
/// once full, AH purchases fail (server returns 0x04C result 0xE5 "inventory full"). We free slots by
/// SELLING junk to a vendor for gil (better than dropping it — the server pays the item's base price and
/// the slot frees). When we clear, we clear EVERYTHING sellable (not just one slot), so the bot doesn't
/// thrash sell-one/buy-one/full-again. Items that can't be sold (NOSALE/locked/key) are skipped.
public interface IInventory
{
    /// Drop `qty` of the item in (container, slot) — 0x028 ITEM_DUMP. Last-resort; prefer SellAllJunk.
    void Drop(byte container, byte slot, ushort qty);

    /// Sell EVERY main-inventory item not in `keep` to a vendor (0x084 appraise + 0x085 confirm) for gil,
    /// freeing as many slots as possible in one pass. Skips items that won't sell. Returns the number of
    /// items actually sold.
    Task<int> SellAllJunk(IReadOnlySet<ushort> keep, CancellationToken ct = default);
}

/// Builds 0x028 GP_CLI_COMMAND_ITEM_DUMP: hdr(4) ItemNum@4(u32 qty) Category@8(container) ItemIndex@9(slot). 12B/3 words.
internal static class DumpPacket
{
    public static byte[] Build(byte container, byte slot, ushort qty)
    {
        var p = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)(0x028 | (3 << 9)));
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), qty);
        p[8] = container;
        p[9] = slot;
        return p;
    }
}

/// 0x084 GP_CLI_COMMAND_SHOP_SELL_REQ (appraise): hdr(4) ItemNum@4(u32 qty) ItemNo@8(u16 itemid) ItemIndex@10(u8 slot). 12B/3 words.
internal static class SellReqPacket
{
    public static byte[] Build(ushort itemId, byte slot, ushort qty)
    {
        var p = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)(0x084 | (3 << 9)));
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), qty);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(8), itemId);
        p[10] = slot;
        return p;
    }
}

/// 0x085 GP_CLI_COMMAND_SHOP_SELL_SET (confirm): hdr(4) SellFlag@4(u16=1). 8B/2 words.
internal static class SellSetPacket
{
    public static byte[] Build()
    {
        var p = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)(0x085 | (2 << 9)));
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), 1);   // SellFlag = 1 (confirm the appraised sale)
        return p;
    }
}

public sealed class Inventory(ISession s) : IInventory
{
    // Slots that wouldn't sell (NOSALE/locked/key). The server silently refuses them; remembering them
    // lets the clear skip them instead of stalling. Sold slots are NOT marked (they free up + may be
    // reused by new drops, which should be sellable next time).
    readonly HashSet<(byte, byte)> _stuck = new();

    public void Drop(byte container, byte slot, ushort qty) => s.Enqueue(DumpPacket.Build(container, slot, qty));

    public async Task<int> SellAllJunk(IReadOnlySet<ushort> keep, CancellationToken ct = default)
    {
        int sold = 0;
        while (!ct.IsCancellationRequested)
        {
            // Find the next sellable junk slot (main inventory, not gil, not gear, not known-stuck).
            (byte c, byte slot, ushort id, ushort qty)? pick = null;
            foreach (var ((c, slot), id) in s.State.Inventory)
            {
                if (c != 0 || slot == 0 || id == 0 || keep.Contains(id) || _stuck.Contains((c, slot))) continue;
                ushort q = s.State.InventoryQty.TryGetValue((c, slot), out var qq) && qq > 0 ? qq : (ushort)1;
                pick = (c, slot, id, q);
                break;
            }
            if (pick is null) break;   // nothing left to sell

            var (pc, pslot, pid, pqty) = pick.Value;
            uint gilBefore = s.State.Gil;

            // 1) Try to SELL it for gil (0x084 appraise + 0x085 confirm).
            s.Enqueue(SellReqPacket.Build(pid, pslot, pqty));
            await Task.Delay(500, ct);
            s.Enqueue(SellSetPacket.Build());
            await Task.Delay(1000, ct);
            if (!s.State.Inventory.TryGetValue((pc, pslot), out var n1) || n1 != pid)
            {
                sold++; Console.WriteLine($"[inv] sold {pid} x{pqty} (slot {pslot}) +{(long)s.State.Gil - gilBefore}g -> gil {s.State.Gil}");
                continue;
            }

            // 2) Couldn't sell (NOSALE like Beastman Seals, or sell unavailable) — DROP it to free the slot.
            s.Enqueue(DumpPacket.Build(pc, pslot, pqty));
            await Task.Delay(1000, ct);
            if (!s.State.Inventory.TryGetValue((pc, pslot), out var n2) || n2 != pid)
            {
                sold++; Console.WriteLine($"[inv] dropped {pid} x{pqty} (slot {pslot}) — unsellable junk");
            }
            else { _stuck.Add((pc, pslot)); Console.WriteLine($"[inv] item {pid} (slot {pslot}) won't sell or drop (equipped/locked) — skipping"); }
        }
        Console.WriteLine($"[inv] junk clear done — sold {sold} item(s) for gil");
        return sold;
    }
}
