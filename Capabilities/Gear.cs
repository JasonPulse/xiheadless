namespace XiHeadless.Capabilities;

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
        SubPacket.WriteHeader(p, 0x050);
        p[4] = invIndex; p[5] = equipSlot; p[6] = container;
        return p;
    }
}

public sealed class Gear(ISession s, IInventory inv) : IGear
{
    public int SkillLevel(int skillId) => s.State.SkillLevel(skillId);
    // MAIN inventory only (container 0) via the shared bag query — the old ContainsValue matched ANY container
    // (Mog Case/Safe too), so it reported items we can't equip. Equipping needs the item in the main bag.
    public bool HasItem(uint itemId) => inv.Has((ushort)itemId);

    public async Task<bool> EquipItem(uint itemId, byte equipSlot, CancellationToken ct = default)
    {
        ushort slot = inv.SlotOf((ushort)itemId);   // container 0 (main inventory) only — that's what EQUIP_SET reads
        if (slot == 0)
        {
            Log.Info($"[gear] item {itemId} not in inventory");
            return false;
        }
        Log.Info($"[gear] equip item {itemId} (container 0 slot {slot}) -> equip slot {equipSlot}");
        s.Enqueue(EquipPacket.Build((byte)slot, equipSlot, 0));
        await Task.Delay(800, ct);
        return true;
    }

    public async Task<int> EquipSet(IEnumerable<(byte slot, uint itemId)> set, CancellationToken ct = default)
    {
        int n = 0;
        foreach (var (slot, itemId) in set)
            if (await EquipItem(itemId, slot, ct)) n++;
        return n;
    }
}
