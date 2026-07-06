using System.Buffers.Binary;

namespace XiHeadless.Interfaces;

/// NPC events / menus / cutscenes. Examine an NPC (Talk) to start its event, then finish
/// it with a menu selection. Needed e.g. to set a home point at a Home Point crystal.
public interface IEvents
{
    bool EventActive { get; }
    ushort CurrentEventId { get; }
    Task Trigger(uint npcId, CancellationToken ct = default);                // Talk (0x1A) only — fire onTrigger, don't wait for an event (messageSpecial qm)
    Task<bool> Examine(uint npcId, CancellationToken ct = default);          // Talk + await event start
    Task FinishEvent(uint selection, CancellationToken ct = default);        // finish the PARSED active event
    Task Finish(uint npcId, ushort npcIndex, ushort eventId, uint selection, CancellationToken ct = default); // finish a KNOWN event blind
}
