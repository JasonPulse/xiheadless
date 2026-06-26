using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

/// Inventory management. A headless bot grinding for hours fills its 30-slot inventory with mob drops;
/// once full, AH purchases fail (server returns 0x04C result 0xE5 "inventory full"). Dropping junk frees
/// slots so the bot can keep buying. (Selling drops for gil is a better long-term move — future work.)
public interface IInventory
{
    /// Drop `qty` of the item in (container, slot) — 0x028 ITEM_DUMP. qty should be the full stack to free the slot.
    void Drop(byte container, byte slot, ushort qty);

    /// Drop one whole stack of a main-inventory item NOT in `keep`, to free a slot. Returns the dropped
    /// item id, or 0 if there was nothing droppable (inventory holds only kept/equipped gear and gil).
    ushort DropJunk(IReadOnlySet<ushort> keep);
}

/// Builds 0x028 GP_CLI_COMMAND_ITEM_DUMP: hdr(4) ItemNum@4(u32 qty) Category@8(container) ItemIndex@9(slot).
/// 12 bytes = 3 words (struct padded to uint32 alignment; size field = words).
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

public sealed class Inventory(ISession s) : IInventory
{
    public void Drop(byte container, byte slot, ushort qty) => s.Enqueue(DumpPacket.Build(container, slot, qty));

    public ushort DropJunk(IReadOnlySet<ushort> keep)
    {
        foreach (var ((c, slot), id) in s.State.Inventory)
        {
            if (c != 0 || slot == 0 || id == 0 || keep.Contains(id)) continue;   // main inventory only; slot 0 = gil
            ushort qty = s.State.InventoryQty.TryGetValue((c, slot), out var q) && q > 0 ? q : (ushort)1;
            Console.WriteLine($"[inv] dropping junk item {id} x{qty} (slot {slot}) to free a slot");
            s.Enqueue(DumpPacket.Build(c, slot, qty));
            return id;
        }
        return 0;
    }
}
