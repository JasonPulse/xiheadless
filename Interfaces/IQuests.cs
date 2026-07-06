namespace XiHeadless.Interfaces;

/// Quest / NPC-dialog flow, built on IEvents. A quest step is: start an NPC's event, then answer the
/// menu/dialog options it presents — often several in a row (talk -> menu -> confirm). The brain
/// scripts WHICH npc and WHICH options (and any navigation between NPCs); this drives the event back-
/// and-forth at one NPC. Reuses IEvents (no new packets).
public interface IQuests
{
    // Start npcId's event and answer with one selection (0 = just advance/close). True if it started.
    Task<bool> TalkTo(uint npcId, uint selection = 0, CancellationToken ct = default);
    // Start npcId's event, then answer each successive event it opens with the next selection in order.
    // Use for multi-prompt quests (e.g. dialog -> menu -> yes/no). True if the first event started.
    Task<bool> RunDialog(uint npcId, IReadOnlyList<uint> selections, CancellationToken ct = default);
}
