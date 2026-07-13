using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

/// Builds 0x028 GP_CLI_COMMAND_ITEM_DUMP: hdr(4) ItemNum@4(u32 qty) Category@8(container) ItemIndex@9(slot). 12B/3 words.
internal static class DumpPacket
{
    public static byte[] Build(byte container, byte slot, ushort qty)
    {
        var p = new byte[12];
        SubPacket.WriteHeader(p, 0x028);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), qty);
        p[8] = container;
        p[9] = slot;
        return p;
    }
}

/// 0x029 GP_CLI_COMMAND_ITEM_MOVE: hdr(4) ItemNum@4(u32 qty) Category1@8(from) Category2@9(to) ItemIndex1@10(from slot)
/// ItemIndex2@11(to slot; only used for stack-unite — the server's InsertItem picks a free slot otherwise). 12B/3 words.
internal static class MovePacket
{
    public static byte[] Build(byte fromContainer, byte toContainer, byte fromSlot, ushort qty)
    {
        var p = new byte[12];
        SubPacket.WriteHeader(p, 0x029);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), qty);
        p[8] = fromContainer;
        p[9] = toContainer;
        p[10] = fromSlot;
        p[11] = 0xFF;   // no unite target — force the simple insert path
        return p;
    }
}

/// 0x084 GP_CLI_COMMAND_SHOP_SELL_REQ (appraise): hdr(4) ItemNum@4(u32 qty) ItemNo@8(u16 itemid) ItemIndex@10(u8 slot). 12B/3 words.
internal static class SellReqPacket
{
    public static byte[] Build(ushort itemId, byte slot, ushort qty)
    {
        var p = new byte[12];
        SubPacket.WriteHeader(p, 0x084);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), qty);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(8), itemId);
        p[10] = slot;
        return p;
    }
}

/// 0x085 GP_CLI_COMMAND_SHOP_SELL_SET (confirm): hdr(4) SellFlag@4(u16=1). 8B/2 words.
internal static class SellSetPacket
{
    public static byte[] Build()
    {
        var p = new byte[8];
        SubPacket.WriteHeader(p, 0x085);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), 1);   // SellFlag = 1 (confirm the appraised sale)
        return p;
    }
}

/// 0x037 GP_CLI_COMMAND_ITEM_USE: hdr(4) UniqueNo@4(u32 = target; self for a scroll) ItemNum@8(u32, must be 0)
/// ActIndex@0x0C(u16 = target targid; self) PropertyItemIndex@0x0E(u8 = inventory slot) padding@0x0F
/// Category@0x10(u32 = container id; 0 = LOC_INVENTORY). 20B/5 words. USING A SPELL SCROLL ON YOURSELF LEARNS
/// THE SPELL (the scroll is consumed and the 0x0AA known-spell bitmap updates).
internal static class UseItemPacket
{
    public static byte[] Build(uint selfId, ushort selfIndex, byte container, byte slot)
    {
        var p = new byte[20];
        SubPacket.WriteHeader(p, 0x037);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), selfId);  // UniqueNo = self
        // ItemNum @0x08 stays 0 (server asserts ItemNum == 0)
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(0x0C), selfIndex);  // ActIndex = self targid
        p[0x0E] = slot;                                                  // PropertyItemIndex
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0x10), container);  // Category (container)
        return p;
    }
}

/// 0x03A GP_CLI_COMMAND_ITEM_STACK: hdr(4) Category@4(u32 = container id). 8B/2 words (PacketSize=0x04). The
/// "Sort" command — server merges stackable items in the container and compacts slots (AH purchases arrive
/// one-per-slot and DON'T auto-stack, so a bot must sort to reclaim space). 1s server cooldown between sorts.
internal static class StackPacket
{
    public static byte[] Build(byte container)
    {
        var p = new byte[8];
        SubPacket.WriteHeader(p, 0x03A);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), container);
        return p;
    }
}

public sealed class Inventory(ISession s) : IInventory
{
    // Bag queries (main inventory = container 0). ToArray() guards against the packet threads mutating the
    // dictionary mid-scan (the pattern every hand-rolled caller used before these were centralized here).
    public int CountOf(ushort itemId)
    {
        int n = 0;
        foreach (var ((c, slot), id) in s.State.Inventory.ToArray())
            if (c == 0 && id == itemId) n += s.State.InventoryQty.TryGetValue((c, slot), out var q) && q > 0 ? q : 1;
        return n;
    }
    public bool Has(ushort itemId)
    {
        foreach (var ((c, _), id) in s.State.Inventory.ToArray()) if (c == 0 && id == itemId) return true;
        return false;
    }
    public ushort SlotOf(ushort itemId)
    {
        foreach (var ((c, slot), id) in s.State.Inventory.ToArray()) if (c == 0 && id == itemId) return slot;
        return 0;
    }
    public List<(byte slot, ushort qty)> SlotsOf(ushort itemId)
    {
        var slots = new List<(byte slot, ushort qty)>();
        foreach (var ((c, slot), id) in s.State.Inventory.ToArray())
        {
            if (c != 0 || slot == 0 || id != itemId) continue;
            slots.Add((slot, s.State.InventoryQty.TryGetValue((c, slot), out var q) && q > 0 ? q : (ushort)1));
        }
        return slots;
    }
    public int CountSlots()
    {
        int n = 0;
        foreach (var ((c, slot), id) in s.State.Inventory.ToArray()) if (c == 0 && slot != 0 && id != 0) n++;
        return n;
    }

    public void UseItem(byte container, byte slot) =>
        s.Enqueue(UseItemPacket.Build(s.State.MyId, s.State.MyIndex, container, slot));

    public void Sort(byte container = 0) => s.Enqueue(StackPacket.Build(container));

    // Slots that wouldn't sell (NOSALE/locked/key). The server silently refuses them; remembering them
    // lets the clear skip them instead of stalling. Sold slots are NOT marked (they free up + may be
    // reused by new drops, which should be sellable next time).
    readonly HashSet<(byte, byte)> _stuck = new();

    public void Drop(byte container, byte slot, ushort qty) => s.Enqueue(DumpPacket.Build(container, slot, qty));

    public async Task<bool> MoveToContainer(byte fromSlot, byte toContainer, ushort qty, CancellationToken ct = default)
    {
        if (!s.State.Inventory.TryGetValue((0, fromSlot), out var id) || id == 0) return false;
        for (int c = 0; c < 3 && !ct.IsCancellationRequested; c++)
        {
            s.Enqueue(MovePacket.Build(0, toContainer, fromSlot, qty));
            for (int t = 0; t < 2000 && !ct.IsCancellationRequested; t += 200)
            {
                await Task.Delay(200, ct);
                if (!s.State.Inventory.TryGetValue((0, fromSlot), out var nn) || nn != id)
                {
                    Log.Info($"[inv] moved item {id} (slot {fromSlot}) -> container {toContainer}");
                    return true;
                }
            }
        }
        return false;
    }

    public async Task<int> SellAllJunk(IReadOnlySet<ushort> keep, CancellationToken ct = default)
    {
        // Sort first: AH deliveries arrive as SINGLES and pin slots (10 loose oils = 10 slots that stack
        // to 1) — consolidating before counting/selling keeps "bag full" meaning actually full.
        Sort(0);
        await Task.Delay(1500, ct);   // server rate-limits sorts to ~1/s and pushes 0x01D updates back
        int sold = 0;
        while (!ct.IsCancellationRequested)
        {
            // Find the next sellable junk slot (main inventory, not gil, not gear, not known-stuck).
            (byte c, byte slot, ushort id, ushort qty)? pick = null;
            foreach (var ((c, slot), id) in s.State.Inventory.ToArray())   // snapshot — same mutation guard as every scan above
            {
                if (c != 0 || slot == 0 || id == 0 || keep.Contains(id) || _stuck.Contains((c, slot))) continue;
                ushort q = s.State.InventoryQty.TryGetValue((c, slot), out var qq) && qq > 0 ? qq : (ushort)1;
                pick = (c, slot, id, q);
                break;
            }
            if (pick is null) break;   // nothing left to sell

            var (pc, pslot, pid, pqty) = pick.Value;

            // 1+2) SELL: appraise (0x084) + confirm (0x085) as an ATOMIC PAIR. The server's PacketGuard
            //    requires the 0x085 to be processed IMMEDIATELY after the 0x084 — if any other packet
            //    (even a 0x015 position keepalive) lands between them it drops the 0x085 as "out-of-order".
            //    So we enqueue both in one frame (EnqueueAtomic) and do NOT wait for the 0x03D price reply
            //    between them: the 0x084 stages the item server-side and the 0x085 sells the staged item;
            //    the 0x03D is informational. A NOSALE item doesn't stage, so the 0x085 no-ops and we fall
            //    through to dropping. Retry the pair (outbound UDP is lossy) until the item leaves its slot.
            uint gilBefore = s.State.Gil;
            bool soldIt = false;
            for (int c = 0; c < 4 && !soldIt && !ct.IsCancellationRequested; c++)
            {
                s.State.SellAppraisePrice = -1;
                s.EnqueueAtomic(SellReqPacket.Build(pid, pslot, pqty), SellSetPacket.Build());
                for (int t = 0; t < 4000 && !soldIt && !ct.IsCancellationRequested; t += 200)
                {
                    await Task.Delay(200, ct);
                    soldIt = !s.State.Inventory.TryGetValue((pc, pslot), out var nn) || nn != pid;
                }
            }
            if (soldIt)
            {
                sold++; Log.Info($"[inv] SOLD {pid} x{pqty} (slot {pslot}) +{(long)s.State.Gil - gilBefore}g -> gil {s.State.Gil}");
                continue;
            }

            // 3) Unsellable (no appraisal), or the sale didn't apply — DROP to free the slot (e.g. seals).
            bool dropped = false;
            for (int d = 0; d < 3 && !dropped && !ct.IsCancellationRequested; d++)
            {
                s.Enqueue(DumpPacket.Build(pc, pslot, pqty));   // 0x028 (retry — outbound may be dropped)
                for (int t = 0; t < 1600 && !dropped && !ct.IsCancellationRequested; t += 200)
                {
                    await Task.Delay(200, ct);
                    dropped = !s.State.Inventory.TryGetValue((pc, pslot), out var nn) || nn != pid;
                }
            }
            if (dropped) { sold++; Log.Info($"[inv] dropped {pid} x{pqty} (slot {pslot}) — unsellable junk"); }
            else { _stuck.Add((pc, pslot)); Log.Info($"[inv] item {pid} (slot {pslot}) won't sell or drop (equipped/locked) — skipping"); }
        }
        if (sold == 0)
        {
            // A no-op clear with a full bag SILENTLY LOSES treasure-pool drops (Rare/EX included) — list
            // exactly what's occupying the bag and why it survived (K=keep set, S=stuck/unsellable, ?=other).
            var residue = new List<string>();
            foreach (var ((c, slot), id) in s.State.Inventory.ToArray())
            {
                if (c != 0 || slot == 0 || id == 0) continue;
                residue.Add($"{slot}:{id}{(keep.Contains(id) ? "K" : _stuck.Contains((c, slot)) ? "S" : "?")}");
            }
            Log.Info($"[inv] bag residue ({residue.Count}): {string.Join(" ", residue)}");
        }
        Log.Info($"[inv] junk clear done — sold {sold} item(s) for gil");
        return sold;
    }
}
