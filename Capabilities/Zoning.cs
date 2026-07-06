using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

/// Builds the 0x05E GP_CLI_COMMAND_MAPRECT zone-line request.
/// Layout: hdr(4) RectID@4 x@8 y@12 z@16 ActIndex@20 MyRoomExitBit@22 MyRoomExitMode@23.
/// 24 bytes = 6 words. The server ignores x/y/z; ExitBit/ExitMode 0 are valid enum values.
internal static class ZoneRequestPacket
{
    public static byte[] Build(uint rectId, float x, float y, float z)
    {
        var p = new byte[24];
        SubPacket.WriteHeader(p, 0x05E);
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

    public Func<CancellationToken, Task>? BeforeLeg { get; set; }

    public void RequestZoneLine(uint rectId)
        => s.Enqueue(ZoneRequestPacket.Build(rectId, s.State.X, s.State.Y, s.State.Z));

    public async Task<bool> GoTo(string zoneName, CancellationToken ct = default)
    {
        if (Zonelines.Resolve(zoneName) is not ushort target)
        {
            Log.Info($"[travel] unknown zone '{zoneName}'");
            return false;
        }
        await ToZone(target, ct);
        return CurrentZone == target;
    }

    public async Task ToZone(ushort targetZone, CancellationToken ct = default)
    {
        var route = Zonelines.Route(CurrentZone, targetZone);
        if (route is null) { Log.Info($"[travel] no route {CurrentZone} -> {targetZone}"); return; }
        foreach (var hop in route)
        {
            ct.ThrowIfCancellationRequested();
            if (BeforeLeg is { } beforeLeg)
            {
                nav.Stop();
                await Task.Delay(400, ct);   // settle: item use is interrupted by movement
                await beforeLeg(ct);
            }
            ushort from = CurrentZone;
            Log.Info($"[travel] zone {from}: walking to the {hop.To} zone line (status={s.State.ServerStatus})");
            await WaitOutEvent(ct);   // don't walk (char is locked) or cross while in a cutscene
            await WalkTo(hop.TriggerX, hop.TriggerY, hop.TriggerZ, ct);

            // Cross it. Position isn't server-validated, so one 0x5E should do it; retry for UDP loss.
            // BUT the server's PacketGuard SILENTLY DROPS a 0x5E while the char is in a cutscene/event
            // (SUBSTATE_IN_CS -> 0x05E isn't in the allow-list), so a stray event turns every attempt into
            // a no-op and the route aborts. San d'Oria's gates fire proximity-triggered events mid-walk
            // (e.g. 568/569), which is exactly what stalled the PLD trek's 230->100 hop. Wait for the
            // background auto-completer (BotHost.AutoCompleteEvents) to clear the event before each send.
            for (int attempt = 0; attempt < 15 && CurrentZone == from; attempt++)
            {
                await WaitOutEvent(ct);
                RequestZoneLine(hop.RectId);
                await WaitFor(() => CurrentZone != from, 1000, ct);
            }
            if (CurrentZone != hop.To)
            {
                Log.Info($"[travel] crossing to {hop.To} failed (now in {CurrentZone}, status={s.State.ServerStatus}, pos=({s.State.X:F0},{s.State.Y:F0},{s.State.Z:F0})); aborting route");
                return;
            }
            await Task.Delay(1500, ct);  // let the new zone's navmesh + initial packets settle
            Log.Info($"[travel] arrived in zone {CurrentZone}");
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

    // ServerStatus 4 = ANIMATION_EVENT: the char is in a cutscene/event, during which PacketGuard drops
    // the zone-line 0x5E (and the char is movement-locked). The background auto-completer finishes stray
    // events after its ~7s grace + startup-blocker sweep, so wait it out (generous cap) rather than firing
    // packets into the void. No-op for a normal crossing (status != 4 returns immediately).
    async Task WaitOutEvent(CancellationToken ct)
    {
        // Block crossings while status is EVENT (4, cutscene → PacketGuard drops the 0x5E) or DEATH (3, the
        // char died mid-trek and the core handler is homepointing — crossing races into a no-op). Wait for a
        // normal/engaged state. If DEATH doesn't clear in the window, the death handler will homepoint us and
        // the route re-computes from the new zone next call.
        if (s.State.ServerStatus is not (3 or 4)) return;
        Log.Info($"[travel] status={s.State.ServerStatus} (3=death,4=cutscene) blocks the 0x5E; waiting to clear");
        await WaitFor(() => s.State.ServerStatus is not (3 or 4), 20000, ct);
    }
}
