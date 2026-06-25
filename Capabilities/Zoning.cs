using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

/// Inter-zone travel. Walks to a zone line then crosses it (0x5E request). The map
/// connection handles the underlying 0x0B re-key/re-zone handshake; ToZone just waits
/// for the zone id to flip. Composes INavigation with the zone-connectivity graph.
public interface IZoning
{
    ushort CurrentZone { get; }
    void RequestZoneLine(uint rectId);                              // low-level: send 0x5E
    Task ToZone(ushort targetZone, CancellationToken ct = default); // high-level: walk the route hop by hop
    Task<bool> GoTo(string zoneName, CancellationToken ct = default); // resolve a zone by name, then ToZone
}

/// Builds the 0x05E GP_CLI_COMMAND_MAPRECT zone-line request.
/// Layout: hdr(4) RectID@4 x@8 y@12 z@16 ActIndex@20 MyRoomExitBit@22 MyRoomExitMode@23.
/// 24 bytes = 6 words. The server ignores x/y/z; ExitBit/ExitMode 0 are valid enum values.
internal static class ZoneRequestPacket
{
    public static byte[] Build(uint rectId, float x, float y, float z)
    {
        var p = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)(0x05E | (6 << 9)));
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), rectId);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(8), x);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(12), y);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(16), z);
        return p;
    }
}

public sealed class Zoning(ISession s, INavigation nav) : IZoning
{
    public ushort CurrentZone => s.State.ZoneId;

    public void RequestZoneLine(uint rectId)
        => s.Enqueue(ZoneRequestPacket.Build(rectId, s.State.X, s.State.Y, s.State.Z));

    public async Task<bool> GoTo(string zoneName, CancellationToken ct = default)
    {
        if (Zonelines.Resolve(zoneName) is not ushort target)
        {
            Console.WriteLine($"[travel] unknown zone '{zoneName}'");
            return false;
        }
        await ToZone(target, ct);
        return CurrentZone == target;
    }

    public async Task ToZone(ushort targetZone, CancellationToken ct = default)
    {
        var route = Zonelines.Route(CurrentZone, targetZone);
        if (route is null) { Console.WriteLine($"[travel] no route {CurrentZone} -> {targetZone}"); return; }
        foreach (var hop in route)
        {
            ct.ThrowIfCancellationRequested();
            ushort from = CurrentZone;
            Console.WriteLine($"[travel] zone {from}: walking to the {hop.To} zone line");
            await WalkTo(hop.TriggerX, hop.TriggerY, hop.TriggerZ, ct);

            // Cross it. Position isn't server-validated, so one 0x5E should do it; retry for UDP loss.
            for (int attempt = 0; attempt < 15 && CurrentZone == from; attempt++)
            {
                RequestZoneLine(hop.RectId);
                await WaitFor(() => CurrentZone != from, 1000, ct);
            }
            if (CurrentZone != hop.To)
            {
                Console.WriteLine($"[travel] crossing to {hop.To} failed (now in {CurrentZone}); aborting route");
                return;
            }
            await Task.Delay(1500, ct);  // let the new zone's navmesh + initial packets settle
            Console.WriteLine($"[travel] arrived in zone {CurrentZone}");
        }
    }

    async Task WalkTo(float x, float y, float z, CancellationToken ct)
    {
        nav.MoveTo(x, y, z);
        await Task.Delay(250, ct);                          // let the path compute / IsMoving latch
        await WaitFor(() => !nav.IsMoving, 60000, ct);      // walk until the path is exhausted (or stuck)
        nav.Stop();
    }

    static async Task WaitFor(Func<bool> cond, int timeoutMs, CancellationToken ct)
    {
        for (int t = 0; t < timeoutMs && !cond(); t += 100) await Task.Delay(100, ct);
    }
}
