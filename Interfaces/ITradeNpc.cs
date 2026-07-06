using System.Buffers.Binary;

namespace XiHeadless.Interfaces;

/// Trade inventory items to an NPC (quest turn-ins). The bot must be within ~6 yalms of the NPC (the
/// server checks UniqueNo + ActIndex + distance). Reuses inventory state to resolve each item's slot.
public interface ITradeNpc
{
    // Trade up to 9 (itemId, quantity) pairs to the NPC. Returns false if an item isn't in inventory.
    Task<bool> Trade(uint npcId, ushort npcIndex, IReadOnlyList<(ushort itemId, uint qty)> items, CancellationToken ct = default);
}
