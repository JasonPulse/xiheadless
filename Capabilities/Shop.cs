using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

/// Builds 0x083 GP_CLI_COMMAND_SHOP_BUY: hdr(4) ItemNum@4(u32) ShopNo@8(u16) ShopItemIndex@10(u16)
/// PropertyItemIndex@12(u8) pad[3]. 16 bytes = 4 words. The server ignores ShopNo; it buys the shop
/// slot at ShopItemIndex (price/item come from the shop container it set up on open).
internal static class ShopPacket
{
    public static byte[] Buy(byte shopIndex, ushort qty)
    {
        var p = new byte[16];
        SubPacket.WriteHeader(p, 0x083);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), qty);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(10), shopIndex);
        return p;
    }
}

public sealed class Shop(ISession s) : IShop
{
    public async Task<IReadOnlyDictionary<byte, (ushort itemId, uint price)>> Open(uint npcId, CancellationToken ct = default)
    {
        // A pure vendor's onTrigger runs player:showShop -> 0x03E + 0x03C (no cutscene/event), parsed
        // into WorldState.Shop. ActIndex (targid) = npcId & 0xFFF. The server's reply can be SLOW (seen
        // ~11s), so re-Talk every few seconds and wait generously — the old 4s wait gave up too early
        // (that was the "flaky shop open"). Report the real latency.
        s.State.Shop.Clear();
        ushort idx = s.State.TargidOf(npcId);   // shared resolver (tracked-entity index, else id & 0xFFF)
        long start = s.State.NowMs;
        for (int attempt = 0; attempt < 6 && !ct.IsCancellationRequested; attempt++)
        {
            s.State.LastEventDrivenUtc = DateTime.UtcNow;   // a shop Talk IS event-driven — warn off the auto-completer (Events.Trigger does the same)
            s.Enqueue(ActionPacket.Build(ActionPacket.Talk, npcId, idx));
            for (int t = 0; t < 4000 && s.State.Shop.Count == 0 && !ct.IsCancellationRequested; t += 100)
                await Task.Delay(100, ct);
            if (s.State.Shop.Count > 0)
            {
                Log.Info($"[shop] opened after {s.State.NowMs - start}ms, {s.State.Shop.Count} items (attempt {attempt + 1})");
                return s.State.Shop;
            }
        }
        Log.Info($"[shop] FAILED to open after {s.State.NowMs - start}ms / 6 talks");
        return s.State.Shop;
    }

    public void Buy(byte shopIndex, ushort qty) => s.Enqueue(ShopPacket.Buy(shopIndex, qty));
}
