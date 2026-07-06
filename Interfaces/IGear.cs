using System.Buffers.Binary;

namespace XiHeadless.Interfaces;

/// Gear + skills. Equip items (the starting weapon, later AH upgrades) and read combat/magic
/// skill levels (for gating weaponskills/spells, which are skill-locked).
public interface IGear
{
    int SkillLevel(int skillId);                  // 1=H2H,3=Sword,5=Axe,32=Divine..36=Elemental..39=Ninjutsu
    bool HasItem(uint itemId);
    Task<bool> EquipItem(uint itemId, byte equipSlot, CancellationToken ct = default); // find in inventory + equip
    // Apply a gear set: (equip slot, item id) pairs, equipped in the given order (order matters — a
    // two-handed main-hand clears sub, so list main before sub). Returns how many equips succeeded.
    Task<int> EquipSet(IEnumerable<(byte slot, uint itemId)> set, CancellationToken ct = default);
}
