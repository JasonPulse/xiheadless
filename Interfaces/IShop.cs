using System.Buffers.Binary;

namespace XiHeadless.Interfaces;

/// NPC vendor shop: open a vendor by talking to it (loads its stock into WorldState.Shop), then buy
/// by shop slot. Crafting mats (crystals, ingredients) are bought here; the economy reuses it too.
public interface IShop
{
    // Talk the vendor to open its shop; resolves to the loaded stock (slot index -> item id, price).
    Task<IReadOnlyDictionary<byte, (ushort itemId, uint price)>> Open(uint npcId, CancellationToken ct = default);
    void Buy(byte shopIndex, ushort qty);   // 0x083 — buy `qty` of the shop's slot `shopIndex`
}
