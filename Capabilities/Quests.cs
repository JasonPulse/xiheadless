namespace XiHeadless.Capabilities;

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
