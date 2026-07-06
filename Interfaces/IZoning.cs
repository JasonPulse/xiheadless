using System.Buffers.Binary;

namespace XiHeadless.Interfaces;

/// Inter-zone travel. Walks to a zone line then crosses it (0x5E request). The map
/// connection handles the underlying 0x0B re-key/re-zone handshake; ToZone just waits
/// for the zone id to flip. Composes INavigation with the zone-connectivity graph.
public interface IZoning
{
    ushort CurrentZone { get; }
    void RequestZoneLine(uint rectId);                              // low-level: send 0x5E
    Task ToZone(ushort targetZone, CancellationToken ct = default); // high-level: walk the route hop by hop
    Task<bool> GoTo(string zoneName, CancellationToken ct = default); // resolve a zone by name, then ToZone
    // Optional per-leg hook, awaited STANDING STILL at the start of every route leg. Item use is interrupted
    // by movement, so this is where stealth re-application belongs — a background maintainer firing mid-walk
    // never lands. Null (fleet default) = no-op. Set around a trek, clear in a finally.
    Func<CancellationToken, Task>? BeforeLeg { get; set; }
}
