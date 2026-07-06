using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

/// Builds 0x05B GP_CLI_COMMAND_EVENTEND (finish/respond to an NPC event).
/// Layout: hdr(4) UniqueNo@4 EndPara(selection)@8 ActIndex@10... NOTE EndPara is u32 so:
/// UniqueNo@4, EndPara@8, ActIndex@12, Mode@14, EventNum@16, EventPara(id)@18. 20 bytes = 5 words.
internal static class EventEndPacket
{
    public static byte[] Build(uint npcId, ushort npcIndex, ushort eventId, uint selection, ushort mode = 0)
    {
        var p = new byte[20];
        SubPacket.WriteHeader(p, 0x05B);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), npcId);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), selection);   // EndPara; server uses (option & 0xFF) as the choice
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(12), npcIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(14), mode);       // 0 = end (-> onEventFinish)
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(18), eventId);    // must match the event's id (csid)
        return p;
    }
}

public sealed class Events(ISession s) : IEvents
{
    public bool EventActive => s.State.EventActive;
    public ushort CurrentEventId => s.State.EventId;
    // Target resolution (streamed index if known, else id & 0xFFF) is the shared WorldState.TargidOf.

    // Send only the 0x1A Talk (fires the NPC's onTrigger server-side) and return immediately — for qm whose
    // onTrigger is a messageSpecial / var-set / key-item grant (no event start, so nothing to await/EVENTEND,
    // e.g. Ordelle's pool & Stalactite Dew qm in A Squire's Test II).
    public async Task Trigger(uint npcId, CancellationToken ct = default)
    {
        s.State.LastEventDrivenUtc = DateTime.UtcNow;   // keep the auto-completer off anything this stirs up
        ushort idx = s.State.TargidOf(npcId);
        Log.Info($"[events] Trigger npc=0x{npcId:X} idx={idx} mypos=({s.State.X:F0},{s.State.Y:F0},{s.State.Z:F0})");
        s.Enqueue(ActionPacket.Build(ActionPacket.Talk, npcId, idx));
        await Task.Delay(600, ct);
    }

    public async Task<bool> Examine(uint npcId, CancellationToken ct = default)
    {
        s.State.EventActive = false;
        s.State.LastEventDrivenUtc = DateTime.UtcNow;   // the brain is deliberately driving this event; keep the auto-completer off it
        ushort idx = s.State.TargidOf(npcId);
        Log.Info($"[events] Talk npc=0x{npcId:X} idx={idx} mypos=({s.State.X:F0},{s.State.Y:F0},{s.State.Z:F0})");
        s.Enqueue(ActionPacket.Build(ActionPacket.Talk, npcId, idx));   // starts the NPC's event server-side
        for (int t = 0; t < 8000 && !s.State.EventActive; t += 100) await Task.Delay(100, ct);
        return s.State.EventActive;
    }

    public async Task FinishEvent(uint selection, CancellationToken ct = default)
    {
        var st = s.State;
        await Finish(st.EventNpcId, st.EventNpcIndex, st.EventId, selection, ct);
    }

    // Finish a KNOWN event without having parsed its start packet (event-recv is still WIP). The
    // server matches on currentEvent's id, so EventPara must be the real csid.
    public async Task Finish(uint npcId, ushort npcIndex, ushort eventId, uint selection, CancellationToken ct = default)
    {
        Log.Info($"[events] EVENTEND npc=0x{npcId:X} idx={npcIndex} event={eventId} sel={selection}");
        s.Enqueue(EventEndPacket.Build(npcId, npcIndex, eventId, selection));
        s.State.EventActive = false;
        s.State.LastEventDrivenUtc = DateTime.UtcNow;
        await Task.Delay(800, ct);
    }
}
