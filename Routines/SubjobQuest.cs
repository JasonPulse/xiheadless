namespace XiHeadless.Routines;

/// The subjob-unlock quest ("The Old Lady" — Vera, Mhaura) as a reusable LIFE-GOAL routine. Per the user's
/// architecture: the subjob is a PHASE of every character's life at level 18+, not a brain — every job brain
/// calls into this (validated live: the WAR accepted via the old inline flow; the WHM accepted via this exact
/// sequence with zero GM help). Extracted verbatim from the proven SubjobBrain quest section.
///
/// Flow facts (server-verified): accept = ev131 option 40 at Vera; completion = trade Wild Rabbit Tail (542)
/// -> ev135, Cup of Dhalmel Saliva (541) -> ev136, Bloody Robe (540) -> ev137 (unlock). Quest state is READ
/// from the 0x056 quest-log: OtherAreas(active) 0x70 bit 10.
public sealed class SubjobQuest(
    IPerception p, INavigation nav, IZoning zoning, IQuests quests, ITradeNpc trade, ICombat combat,
    IGear gear, IInventory inv, IAuctionHouse ah, IShop shop, IEvents events)
{
    const ushort MhauraZone = 249;
    const string AhZone = "Windurst Woods";
    static readonly HashSet<ushort> Keep = new()
        { StealthRoutines.SilentOil, StealthRoutines.PrismPowder,
          QuestDefs.WildRabbitTail, QuestDefs.CupOfDhalmelSaliva, QuestDefs.BloodyRobe, 1126, 1127 };

    /// Quest state from the live 0x056 bitmap — The Old Lady = OtherAreas(active) 0x70 quest id 10.
    public bool Accepted() => QuestState.QuestAccepted(p.World, 0x70, 10);

    /// True when this character holds one of each trade item (its OWN set — the trade consumes them).
    public bool HasItems() =>
        inv.CountOf(QuestDefs.WildRabbitTail) >= 1 && inv.CountOf(QuestDefs.CupOfDhalmelSaliva) >= 1 && inv.CountOf(QuestDefs.BloodyRobe) >= 1;

    /// Run the appropriate step for the current state: accept if not accepted, or the trade-completion chain
    /// if accepted + items in bag. Handles the stealth crossing to Mhaura (powders bought at an AH if dry).
    /// Idempotent — safe to call every session at 18+. Returns true if the driven flow reported ok.
    public async Task<bool> Advance(CancellationToken ct)
    {
        if (p.World.MainJobLevel < 18) return false;
        bool accepted = Accepted();
        if (accepted && !HasItems()) return false;   // nothing to do until the farm produces this char's set

        CancellationTokenSource? stealthCts = null;
        if (zoning.CurrentZone != MhauraZone)
        {
            if (inv.CountOf(StealthRoutines.SilentOil) < 6 || inv.CountOf(StealthRoutines.PrismPowder) < 6)
            {
                if (!Game.Zonelines.HasAuctionHouse(zoning.CurrentZone))
                { Log.Info($"[sjquest] -> {AhZone} for powders"); await zoning.GoTo(AhZone, ct); }
                inv.Sort(); await Task.Delay(1500, ct);
                await ShopRoutines.SellNearby(shop, nav, zoning, inv, p, Keep, ct);
                Task<int> SellJunk(CancellationToken c) => ShopRoutines.SellNearby(shop, nav, zoning, inv, p, Keep, c);
                await StealthRoutines.EnsureStock(ah, p, inv, 12, Keep, SellJunk, ct);
                inv.Sort(); await Task.Delay(1500, ct);
            }
            Log.Info("[sjquest] stealth-crossing to Mhaura (Sneak + Invisible)");
            stealthCts = await StealthRoutines.BeginStealth(inv, p, ct);
        }

        var steps = accepted ? QuestDefs.SubjobComplete : QuestDefs.SubjobUnlock;
        Log.Info($"[sjquest] accepted={accepted} -> running {(accepted ? "completion trade-chain" : "accept")}");
        bool ok = await new QuestRunner(p, nav, zoning, quests, trade, combat, gear, events, inv).Run(steps, "sjquest", ct);
        stealthCts?.Cancel();
        Log.Info($"[sjquest] flow done (ok={ok}, lastEvent={p.World.EventId})");
        return ok;
    }
}
