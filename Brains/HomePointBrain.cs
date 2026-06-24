namespace XiHeadless.Brains;

/// Sets the bot's home point at a Home Point crystal (so death-recovery revives somewhere
/// valid instead of zone-0 limbo). Finds the nearest "Home Point" NPC, walks to it, examines
/// it, and selects Set Home Point (option 1). Run once in a city with a crystal.
public sealed class HomePointBrain(IPerception p, INavigation nav, IEvents events) : IBrain
{
    public async Task RunAsync(CancellationToken ct)
    {
        // The headless char never completed the New Character Cutscene, where the server calls
        // player:setHomePoint() AND which (until finished) keeps the char "in event" and blocks ALL
        // npc interaction. It re-triggers onZoneIn while unfinished. Finishing it sets the home point
        // and unblocks NPCs. Event id is per-zone (Bastok Mines = 1).
        const ushort csEvent = 1;
        Console.WriteLine($"[hp] finishing new-char cutscene event {csEvent} (sets home point + unblocks NPCs; harmless if already done)");
        await Task.Delay(4000, ct);
        await events.Finish(p.World.MyId, 0, csEvent, 0, ct);
        await Task.Delay(3000, ct);   // let the cutscene's setPos warp land

        // Verify NPC interaction is now unblocked: walk to the Bastok Mines crystal and examine it.
        uint crystalId = 17735748u;
        Entity? c = null;
        for (int i = 0; i < 30 && c is null && !ct.IsCancellationRequested; i++)
        { p.World.Entities.TryGetValue(crystalId, out c); if (c is null) await Task.Delay(500, ct); }
        if (c is not null)
        {
            Console.WriteLine($"[hp] walking to crystal ({c.X:F0},{c.Z:F0}) to verify NPC interaction");
            for (int i = 0; i < 60 && p.DistanceTo(c.X, c.Z) > 4f && !ct.IsCancellationRequested; i++)
            { if (!nav.IsMoving) nav.MoveTo(c.X, c.Z); await Task.Delay(400, ct); }
            nav.Stop(); nav.Face(crystalId);
            bool got = await events.Examine(crystalId, ct);
            Console.WriteLine($"[hp] crystal examine: event received={got} id={events.CurrentEventId} (true => NPC/event interaction WORKS)");
            if (got) await events.FinishEvent(1, ct);
        }
        await Task.Delay(Timeout.Infinite, ct);
    }
}
