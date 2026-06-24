using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

/// NPC events / menus / cutscenes. Examine an NPC (Talk) to start its event, then finish
/// it with a menu selection. Needed e.g. to set a home point at a Home Point crystal.
public interface IEvents
{
    bool EventActive { get; }
    ushort CurrentEventId { get; }
    Task<bool> Examine(uint npcId, CancellationToken ct = default);          // Talk + await event start
    Task FinishEvent(uint selection, CancellationToken ct = default);        // finish the PARSED active event
    Task Finish(uint npcId, ushort npcIndex, ushort eventId, uint selection, CancellationToken ct = default); // finish a KNOWN event blind
}

/// Builds 0x05B GP_CLI_COMMAND_EVENTEND (finish/respond to an NPC event).
/// Layout: hdr(4) UniqueNo@4 EndPara(selection)@8 ActIndex@10... NOTE EndPara is u32 so:
/// UniqueNo@4, EndPara@8, ActIndex@12, Mode@14, EventNum@16, EventPara(id)@18. 20 bytes = 5 words.
internal static class EventEndPacket
{
    public static byte[] Build(uint npcId, ushort npcIndex, ushort eventId, uint selection, ushort mode = 0)
    {
        var p = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)(0x05B | (5 << 9)));
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
    // Use the streamed index if known, else derive it from the id (targid = id & 0xFFF for FFXI entities).
    ushort IndexOf(uint id) => s.State.Entities.TryGetValue(id, out var e) && e.Index != 0 ? e.Index : (ushort)(id & 0xFFF);

    public async Task<bool> Examine(uint npcId, CancellationToken ct = default)
    {
        s.State.EventActive = false;
        ushort idx = IndexOf(npcId);
        Console.WriteLine($"[events] Talk npc=0x{npcId:X} idx={idx} mypos=({s.State.X:F0},{s.State.Y:F0},{s.State.Z:F0})");
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
        Console.WriteLine($"[events] EVENTEND npc=0x{npcId:X} idx={npcIndex} event={eventId} sel={selection}");
        s.Enqueue(EventEndPacket.Build(npcId, npcIndex, eventId, selection));
        s.State.EventActive = false;
        await Task.Delay(800, ct);
    }
}
