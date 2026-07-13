namespace XiHeadless.Routines;

/// Bag-pressure relief for non-stacking keepers (Beastmen's Seals filled 17/30 slots; the pinned bag made
/// treasure-pool drops — Rare/EX included — silently bounce). Two tools:
///  - StashExcess: move surplus into the MOG CASE (container 7) — accepts EX items and is reachable from
///    ANYWHERE (the server allows inventory<->case moves outside the Mog House). Preferred.
///  - MailExcess: delivery-box mail to a holder character. Only for items WITHOUT the EX flag (the server
///    refuses to stage EX items — seals cannot be mailed; this failed live) and only from a city.
public static class MailRoutines
{
    public const byte MogCase = 7;
    public const byte MogSafe = 1;   // Mog Safe: bigger, but moves to it only work INSIDE the Mog House

    static List<(byte slot, ushort qty)> SlotsOf(IPerception p, ushort itemId)
    {
        var slots = new List<(byte slot, ushort qty)>();
        foreach (var ((c, slot), id) in p.World.Inventory.ToArray())
        {
            if (c != 0 || slot == 0 || id != itemId) continue;
            ushort q = p.World.InventoryQty.TryGetValue((c, slot), out var qq) && qq > 0 ? qq : (ushort)1;
            slots.Add((slot, q));
        }
        return slots;
    }

    /// The standard bag-maintenance pair: bank surplus subjob-quest seals (1126/1127 — EX, unmailable,
    /// keep-set survivors that pin the bag) into `container`, then optionally junk-sell in place.
    /// This exact sequence was copy-pasted 5x across SubjobBrain/PartyLeechBrain.
    public static async Task BagMaintenance(IInventory inv, IPerception p, CancellationToken ct,
                                            HashSet<ushort>? sellKeep = null, byte container = MogCase)
    {
        await StashExcess(inv, p, 1126, keepMax: 2, ct, container);
        await StashExcess(inv, p, 1127, keepMax: 2, ct, container);
        if (sellKeep is not null) await inv.SellAllJunk(sellKeep, ct);
    }

    /// Move every slot of `itemId` beyond `keepMax` into `container` (default Mog Case — works in the field;
    /// Mog Safe works only inside the Mog House). Bails after the FIRST failed move: a full container fails
    /// every slot, and the old retry-every-cycle churn burned ~36s per bag-clear forever once the Case filled.
    public static async Task<int> StashExcess(IInventory inv, IPerception p, ushort itemId, int keepMax,
                                              CancellationToken ct, byte container = MogCase)
    {
        var slots = SlotsOf(p, itemId);
        int excess = slots.Count - keepMax;
        if (excess <= 0) return 0;

        Log.Info($"[stash] {slots.Count} slots of item {itemId} — moving {excess} to container {container} (keeping {keepMax})");
        int moved = 0;
        foreach (var (slot, qty) in slots.Take(excess))
        {
            if (ct.IsCancellationRequested) break;
            if (await inv.MoveToContainer(slot, container, qty, ct)) moved++;
            else { Log.Info($"[stash] container {container} refused the move (full?) — stopping this pass"); break; }
            await Task.Delay(400, ct);
        }
        Log.Info($"[stash] moved {moved}/{excess} slot(s) of item {itemId} to container {container}");
        return moved;
    }

    /// Mail every slot of `itemId` beyond `keepMax` to `recipient` (city only; NOT for EX items).
    public static async Task<int> MailExcess(IDelivery delivery, IPerception p, ushort itemId, int keepMax,
                                             string recipient, CancellationToken ct)
    {
        var slots = SlotsOf(p, itemId);
        int excess = slots.Count - keepMax;
        if (excess <= 0) return 0;

        Log.Info($"[mail] {slots.Count} slots of item {itemId} — mailing {excess} to '{recipient}' (keeping {keepMax})");
        int sent = 0;
        foreach (var (slot, qty) in slots.Take(excess))
        {
            if (ct.IsCancellationRequested) break;
            if (await delivery.SendItem(recipient, slot, qty, ct)) sent++;
            await Task.Delay(700, ct);
        }
        Log.Info($"[mail] sent {sent}/{excess} parcel(s) of item {itemId}");
        return sent;
    }
}
