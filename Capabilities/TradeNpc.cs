using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

/// Trade inventory items to an NPC (quest turn-ins). The bot must be within ~6 yalms of the NPC (the
/// server checks UniqueNo + ActIndex + distance). Reuses inventory state to resolve each item's slot.
public interface ITradeNpc
{
    // Trade up to 9 (itemId, quantity) pairs to the NPC. Returns false if an item isn't in inventory.
    Task<bool> Trade(uint npcId, ushort npcIndex, IReadOnlyList<(ushort itemId, uint qty)> items, CancellationToken ct = default);
}

/// Builds 0x036 GP_CLI_COMMAND_ITEM_TRANSFER (NPC trade). Layout: hdr(4) UniqueNo@4(u32, npc id)
/// ItemNumTbl[10]@8(u32 each, quantities) PropertyItemIndexTbl[10]@48(u8 each, inventory slots)
/// ActIndex@58(u16, npc target index) ItemNum@60(u8, count). 64 bytes = 16 words (PacketSize[0x36]=0x20).
internal static class TradePacket
{
    public static byte[] Build(uint npcId, ushort npcIndex, IReadOnlyList<(byte slot, uint qty)> items)
    {
        var p = new byte[64];
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)(0x036 | (16 << 9)));
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), npcId);
        for (int i = 0; i < items.Count && i < 10; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8 + i * 4), items[i].qty);
            p[48 + i] = items[i].slot;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(58), npcIndex);
        p[60] = (byte)Math.Min(items.Count, 9);
        return p;
    }
}

public sealed class TradeNpc(ISession s) : ITradeNpc
{
    public Task<bool> Trade(uint npcId, ushort npcIndex, IReadOnlyList<(ushort itemId, uint qty)> items, CancellationToken ct = default)
    {
        var slots = new List<(byte slot, uint qty)>();
        foreach (var (itemId, qty) in items)
        {
            byte? slot = null;
            foreach (var ((container, s2), id) in s.State.Inventory)
                if (container == 0 && id == itemId) { slot = s2; break; }
            if (slot is null) { Console.WriteLine($"[trade] item {itemId} not in inventory — abort"); return Task.FromResult(false); }
            slots.Add((slot.Value, qty));
        }
        Console.WriteLine($"[trade] {items.Count} item(s) -> npc 0x{npcId:X} idx={npcIndex}");
        s.Enqueue(TradePacket.Build(npcId, npcIndex, slots));
        return Task.FromResult(true);
    }
}
