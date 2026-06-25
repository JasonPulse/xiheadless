namespace XiHeadless.Capabilities;

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

public sealed class Quests(IEvents events) : IQuests
{
    public Task<bool> TalkTo(uint npcId, uint selection = 0, CancellationToken ct = default)
        => RunDialog(npcId, new[] { selection }, ct);

    public async Task<bool> RunDialog(uint npcId, IReadOnlyList<uint> selections, CancellationToken ct = default)
    {
        if (!await events.Examine(npcId, ct)) { Console.WriteLine($"[quest] npc 0x{npcId:X} opened no event"); return false; }
        foreach (var sel in selections)
        {
            // After the first answer, wait briefly for the NPC to open the next prompt; stop if none.
            if (!events.EventActive)
            {
                for (int t = 0; t < 3000 && !events.EventActive; t += 100) await Task.Delay(100, ct);
                if (!events.EventActive) { Console.WriteLine("[quest] no further prompt — dialog done"); break; }
            }
            await events.FinishEvent(sel, ct);
        }
        return true;
    }
}
