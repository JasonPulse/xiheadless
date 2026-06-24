using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

/// Delivery box (Mog House mail). Mail gil/items to another character by name — async, the recipient
/// can be offline. The bot must be in a delivery-capable zone (a city with AH/mog-menu, or a
/// residential area); cities qualify, so an RMT bot can deliver from where it spams.
public interface IDelivery
{
    Task<bool> EnterMogHouse(CancellationToken ct = default);   // 0x5E to the current city's MH entrance
    Task ExitMogHouse(CancellationToken ct = default);          // 0x5E mogHouseZoneLine -> back to the city
    Task<bool> SendGil(string player, int amount, CancellationToken ct = default);
}

/// Builds 0x4D GP_CLI_COMMAND_PBX (delivery box). Layout: hdr(4) Command@4 BoxNo@5 PostWorkNo@6
/// ItemWorkNo@7 ItemStacks@8(i32) Result@12 ResParam1@13 ResParam2@14 ResParam3@15 TargetName[16]@16.
/// 32 bytes (PacketSize[0x4D]=0 => any size). Result/ResParam must be 0 (left zero). Unused int8
/// fields are -1 per the server validator; ints are passed explicitly per command.
internal static class DeliveryPacket
{
    public const byte DeliOpen = 0x0D, Set = 0x02, Send = 0x03, Confirm = 0x07;
    public const sbyte BoxNone = -1, BoxOutgoing = 2;

    public static byte[] Build(byte command, sbyte boxNo, sbyte postWorkNo, sbyte itemWorkNo, int itemStacks, string target = "")
    {
        var p = new byte[32];
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)(0x04D | (8 << 9)));
        p[4] = command;
        p[5] = (byte)boxNo;
        p[6] = (byte)postWorkNo;
        p[7] = (byte)itemWorkNo;
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(8), itemStacks);
        if (target.Length > 0) System.Text.Encoding.ASCII.GetBytes(target).CopyTo(p, 16); // TargetName[16]
        return p;
    }
}

public sealed class Delivery(ISession s) : IDelivery
{
    // City zone -> its Mog House entrance maprect (zonelines rows with tozone=0). The bot only needs
    // to BE in the zone; entry (0x5e_maprect.cpp:209, m_toZone==0) resolves the rect by id in the
    // current zone with NO proximity check and sets m_moghouseID=id (no 0x0B zone-change). Bastok
    // mapped; add San d'Oria/Windurst the same way (their tozone=0 zonelines).
    static readonly Dictionary<ushort, uint> MogHouseEntry = new()
    {
        [230] = 812805498,   // Southern San d'Oria
        [231] = 846359930,   // Northern San d'Oria
        [232] = 879914362,   // Port San d'Oria
        [234] = 1735552378,  // Bastok Mines
        [235] = 1634889082,  // Bastok Markets
        [236] = 1769106810,  // Port Bastok
        [238] = 913468794,   // Windurst Waters
        [239] = 947023226,   // Windurst Waters (S)
        [240] = 1701997946,  // Port Windurst
        [241] = 1802661242,  // Windurst Woods
        [243] = 1836215674,  // Ru'Lude Gardens
        [244] = 1869770106,  // Upper Jeuno
        [245] = 1970433402,  // Lower Jeuno
        [246] = 1936878970,  // Port Jeuno
    };

    /// Enter the bot's Mog House (required before the delivery box works) by sending the current
    /// city's entrance maprect. Must already be in a mapped city zone. Reuses the 0x5E builder.
    public async Task<bool> EnterMogHouse(CancellationToken ct = default)
    {
        if (!MogHouseEntry.TryGetValue(s.State.ZoneId, out var rect))
        {
            Console.WriteLine($"[delivery] no Mog House entrance mapped for zone {s.State.ZoneId} — be in a home city first");
            return false;
        }
        s.Enqueue(ZoneRequestPacket.Build(rect, s.State.X, s.State.Y, s.State.Z)); // 0x5E maprect -> sets m_moghouseID
        await Task.Delay(1500, ct);
        return true;
    }

    // The special "mog house zone line" rect (0x05e_maprect.cpp). Sent with MyRoomExitMode=AreaEnteredFrom
    // (which ZoneRequestPacket leaves at 0) it returns the char to the city it entered from (m_moghouseID=0).
    const uint MogHouseExitRect = 1903324538;

    /// Leave the Mog House back to the city (so /yell reaches again). Triggers a 240->240 re-zone the
    /// existing DoZoneChange handles, same as entry.
    public async Task ExitMogHouse(CancellationToken ct = default)
    {
        s.Enqueue(ZoneRequestPacket.Build(MogHouseExitRect, s.State.X, s.State.Y, s.State.Z));
        await Task.Delay(1500, ct);
    }

    /// Mail gil to a player: open the send box, stage gil into a FREE outgoing slot addressed to the
    /// player, commit, finalize. The sender's outgoing box keeps a row per PENDING send (PK
    /// charid,box,slot) until the buyer picks it up, so a reused slot collides (the delivery_box INSERT
    /// fails and the server stays silent). We therefore try slots 0..7 and proceed only when the Set
    /// is ACKed (0x04B) — the server acks a Set only when it truly staged (free slot + receiver
    /// resolved). No ack on any slot => box full or the name didn't resolve. Async; recipient offline ok.
    public async Task<bool> SendGil(string player, int amount, CancellationToken ct = default)
    {
        // Open the outgoing send box (server must be in SEND_DELIVERYBOX state before Set/Send work).
        s.Enqueue(DeliveryPacket.Build(DeliveryPacket.DeliOpen, DeliveryPacket.BoxNone, -1, -1, -1));
        await Task.Delay(800, ct);

        for (sbyte slot = 0; slot < 8 && !ct.IsCancellationRequested; slot++)
        {
            s.State.DboxAck = -1;
            // Set: gil = inventory slot 0 (item 65535), ItemStacks = amount, into outgoing slot `slot`.
            s.Enqueue(DeliveryPacket.Build(DeliveryPacket.Set, DeliveryPacket.BoxOutgoing, slot, 0, amount, player));
            if (!await WaitAck(DeliveryPacket.Set, slot, 2500, ct)) continue;   // not staged here -> next slot

            s.Enqueue(DeliveryPacket.Build(DeliveryPacket.Send, DeliveryPacket.BoxOutgoing, slot, -1, -1));
            await Task.Delay(600, ct);
            s.Enqueue(DeliveryPacket.Build(DeliveryPacket.Confirm, DeliveryPacket.BoxNone, -1, -1, -1));
            await Task.Delay(400, ct);
            Console.WriteLine($"[delivery] sent {amount} gil -> '{player}' (outgoing slot {slot})");
            return true;
        }
        Console.WriteLine($"[delivery] FAILED to send {amount} gil -> '{player}' (no free outgoing slot, or name didn't resolve)");
        return false;
    }

    /// Wait up to timeoutMs for the delivery-box reply for (command, slot): DboxAck == (command<<8)|slot.
    async Task<bool> WaitAck(byte command, sbyte slot, int timeoutMs, CancellationToken ct)
    {
        int want = (command << 8) | (slot & 0xFF);
        for (int t = 0; t < timeoutMs && !ct.IsCancellationRequested; t += 100)
        {
            if (s.State.DboxAck == want) return true;
            await Task.Delay(100, ct);
        }
        return false;
    }
}
