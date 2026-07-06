using System.Buffers.Binary;

namespace XiHeadless.Interfaces;

/// Inventory management. A headless bot grinding for hours fills its 30-slot inventory with mob drops;
/// once full, AH purchases fail (server returns 0x04C result 0xE5 "inventory full"). We free slots by
/// SELLING junk to a vendor for gil (better than dropping it — the server pays the item's base price and
/// the slot frees). When we clear, we clear EVERYTHING sellable (not just one slot), so the bot doesn't
/// thrash sell-one/buy-one/full-again. Items that can't be sold (NOSALE/locked/key) are skipped.
public interface IInventory
{
    /// Total quantity of `itemId` held in MAIN inventory (container 0), summing stack quantities
    /// (InventoryQty, defaulting to 1). The one place "how many of X do I have?" is answered.
    int CountOf(ushort itemId);

    /// True if any MAIN-inventory (container 0) slot holds `itemId`. Container-0 only, so it does NOT match
    /// items sitting in the Mog Case/Safe (a bag scan, not an "owned anywhere" check).
    bool Has(ushort itemId);

    /// The first MAIN-inventory (container 0) slot holding `itemId`, or 0 if none. Slot 0 is reserved
    /// (never a real item), so 0 doubles as the "not found" sentinel.
    ushort SlotOf(ushort itemId);

    /// Drop `qty` of the item in (container, slot) — 0x028 ITEM_DUMP. Last-resort; prefer SellAllJunk.
    void Drop(byte container, byte slot, ushort qty);

    /// Use the item in (container, slot) on yourself — 0x037 ITEM_USE. For a spell scroll this learns the
    /// spell (scroll consumed; the 0x0AA known-spell bitmap updates). container 0 = main inventory.
    void UseItem(byte container, byte slot);

    /// Sort/stack a container — 0x03A ITEM_STACK. Merges stackable items + compacts slots. Needed because AH
    /// purchases arrive one-per-slot and don't auto-stack. container 0 = main inventory. (1s server cooldown.)
    void Sort(byte container = 0);

    /// Sell EVERY main-inventory item not in `keep` to a vendor (0x084 appraise + 0x085 confirm) for gil,
    /// freeing as many slots as possible in one pass. Skips items that won't sell. Returns the number of
    /// items actually sold.
    Task<int> SellAllJunk(IReadOnlySet<ushort> keep, CancellationToken ct = default);

    /// Move `qty` from main-inventory `fromSlot` into another container (0x029 ITEM_MOVE; server picks the
    /// destination slot). Mog Case (7) is movable-to from ANYWHERE and accepts EX items — the pressure valve
    /// for non-stacking keepers (Beastmen's Seals pinned the 30-slot bag and pool drops bounced). Waits for
    /// the source slot to clear; false = the move didn't apply (container full/locked).
    Task<bool> MoveToContainer(byte fromSlot, byte toContainer, ushort qty, CancellationToken ct = default);
}
