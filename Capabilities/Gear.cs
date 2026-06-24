using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

/// Gear + skills. Equip items (the starting weapon, later AH upgrades) and read combat/magic
/// skill levels (for gating weaponskills/spells, which are skill-locked).
public interface IGear
{
    int SkillLevel(int skillId);                  // 1=H2H,3=Sword,5=Axe,32=Divine..36=Elemental..39=Ninjutsu
    bool HasItem(uint itemId);
    Task<bool> EquipItem(uint itemId, byte equipSlot, CancellationToken ct = default); // find in inventory + equip
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
}
