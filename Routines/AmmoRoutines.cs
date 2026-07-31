namespace XiHeadless.Routines;

/// Ranged-ammo upkeep. On this server a QUIVER/POUCH is USABLE and unpacks into ONE 99-stack of arrows
/// (bone_quiver.lua: giveItem{BONE_ARROW,99}); the quiver item itself stacks to 12. So a RNG carries one
/// 12-quiver stack (~1 bag slot) and opens a quiver whenever the equipped arrow runs low — ~1,188 arrows a
/// session (user: ~400/2h) from ~2 slots, instead of a dozen loose stacks. Reused by RNG (arrows) and COR
/// (bullet pouches). Movement/AH stay in their own layers — this only opens + re-equips.
public static class AmmoRoutines
{
    const int RefillBelow = 12;   // open the next quiver when the equipped stack drops under this

    /// Keep arrows in the ammo slot: if we're low AND hold a quiver, USE one (0x037 → +99 arrows) and
    /// re-equip. Also does the FIRST unpack (a fresh RNG owns quivers, no loose arrows yet). No-op for
    /// non-ranged configs (quiver/arrow 0) or when out of quivers (the fight loop falls back to melee).
    public static async Task EnsureArrows(IInventory inv, IGear gear, IPerception p,
                                          ushort quiver, ushort arrow, string tag, CancellationToken ct)
    {
        if (quiver == 0 || arrow == 0) return;
        if (inv.CountOf(arrow) >= RefillBelow) return;   // still stocked
        ushort qslot = inv.SlotOf(quiver);
        if (qslot == 0) return;                          // out of quivers — melee fallback handles it
        Log.Info($"[{tag}] arrows low ({inv.CountOf(arrow)}) — opening a quiver ({inv.CountOf(quiver)} left)");
        inv.UseItem(0, (byte)qslot);                     // unpacks one 99-stack into the bag
        await Task.Delay(1500, ct);                      // let the arrows land before equipping
        await gear.EquipItem(arrow, Capabilities.EquipSlot.Ammo, ct);
    }
}
