using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

/// Inventory management. A headless bot grinding for hours fills its 30-slot inventory with mob drops;
/// once full, AH purchases fail (server returns 0x04C result 0xE5 "inventory full"). We free slots by
/// SELLING junk to a vendor for gil (better than dropping it for nothing — the server pays the item's
/// base price and the slot frees). Items that can't be sold (NOSALE/locked/key) are skipped.
public interface IInventory
{
    /// Drop `qty` of the item in (container, slot) — 0x028 ITEM_DUMP. Last-resort; prefer SellJunk.
    void Drop(byte container, byte slot, ushort qty);

    /// Sell one whole stack of a main-inventory item NOT in `keep` to a vendor (0x084 appraise + 0x085
    /// confirm) for gil, to free a slot. Rotates past items that won't sell. Returns the item id sold,
    /// or 0 if nothing sellable remains.
    Task<ushort> SellJunk(IReadOnlySet<ushort> keep, CancellationToken ct = default);
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
    // Slots already attempted this session. Some items won't sell (NOSALE/locked/key) and the server
    // silently refuses; without this we'd keep re-picking the same stuck item. Skipping tried slots
    // rotates us onto sellable loot.
    readonly HashSet<(byte, byte)> _tried = new();

    public void Drop(byte container, byte slot, ushort qty) => s.Enqueue(DumpPacket.Build(container, slot, qty));

    public async Task<ushort> SellJunk(IReadOnlySet<ushort> keep, CancellationToken ct = default)
    {
        foreach (var ((c, slot), id) in s.State.Inventory)
        {
            if (c != 0 || slot == 0 || id == 0 || keep.Contains(id) || _tried.Contains((c, slot))) continue;
            _tried.Add((c, slot));   // sell this slot once; if it doesn't leave, we move on next call
            ushort qty = s.State.InventoryQty.TryGetValue((c, slot), out var q) && q > 0 ? q : (ushort)1;
            uint gilBefore = s.State.Gil;
            Console.WriteLine($"[inv] selling junk item {id} x{qty} (slot {slot}) for gil");
            s.Enqueue(SellReqPacket.Build(id, slot, qty));   // 0x084 appraise
            await Task.Delay(600, ct);
            s.Enqueue(SellSetPacket.Build());                // 0x085 confirm
            await Task.Delay(1200, ct);                       // let the sale apply (gil + slot update)
            if (s.State.Gil > gilBefore) Console.WriteLine($"[inv] sold {id} for {s.State.Gil - gilBefore} gil (now {s.State.Gil})");
            return id;
        }
        return 0;   // nothing left sellable
    }
}
