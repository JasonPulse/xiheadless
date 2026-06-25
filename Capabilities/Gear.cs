using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

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

/// Equip-slot ids (server SLOT_* enum), so gear sets read by name instead of magic numbers.
public static class EquipSlot
{
    public const byte Main = 0, Sub = 1, Ranged = 2, Ammo = 3, Head = 4, Body = 5, Hands = 6, Legs = 7,
                      Feet = 8, Neck = 9, Waist = 10, Ear1 = 11, Ear2 = 12, Ring1 = 13, Ring2 = 14, Back = 15;
}

/// Builds 0x050 GP_CLI_COMMAND_EQUIP_SET. hdr(4) PropertyItemIndex@4 EquipKind@5 Category@6.
/// 8 bytes = 2 words. EquipKind: 0=MAIN,1=SUB,2=RANGED,4=HEAD... Category: 0=inventory.
internal static class EquipPacket
{
    public static byte[] Build(byte invIndex, byte equipSlot, byte container)
    {
        var p = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)(0x050 | (2 << 9)));
        p[4] = invIndex; p[5] = equipSlot; p[6] = container;
        return p;
    }
}

public sealed class Gear(ISession s) : IGear
{
    public int SkillLevel(int skillId) => s.State.SkillLevel(skillId);
    public bool HasItem(uint itemId) => s.State.Inventory.ContainsValue((ushort)itemId);

    public async Task<bool> EquipItem(uint itemId, byte equipSlot, CancellationToken ct = default)
    {
        foreach (var (key, id) in s.State.Inventory)
            if (id == itemId)
            {
                Console.WriteLine($"[gear] equip item {itemId} (container {key.container} slot {key.slot}) -> equip slot {equipSlot}");
                s.Enqueue(EquipPacket.Build(key.slot, equipSlot, key.container));
                await Task.Delay(800, ct);
                return true;
            }
        Console.WriteLine($"[gear] item {itemId} not in inventory");
        return false;
    }

    public async Task<int> EquipSet(IEnumerable<(byte slot, uint itemId)> set, CancellationToken ct = default)
    {
        int n = 0;
        foreach (var (slot, itemId) in set)
            if (await EquipItem(itemId, slot, ct)) n++;
        return n;
    }
}
