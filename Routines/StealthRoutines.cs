namespace XiHeadless.Routines;

/// Stealth travel through aggressive zones (e.g. a low-level bot crossing Buburimu to Mhaura). Uses
/// consumables — Silent Oil (Sneak, blocks sound aggro) + Prism Powder (Invisible, blocks sight aggro) — so
/// the bot runs past goblins/Zu/etc. without being attacked. Reusable by ANY brain (this is the general
/// answer to "how does a bot survive a dangerous transit", not a teleport/handout). The powders are cheap
/// AH items; keep a stock (a crossing may outlast one application). USES the 0x037 USE_ITEM capability.
public static class StealthRoutines
{
    public const ushort SilentOil = Game.Items.SilentOil;      // -> Sneak  (sound aggro)
    public const ushort PrismPowder = Game.Items.PrismPowder;  // -> Invisible (sight aggro)

    public static bool HasPowders(IInventory inv) => inv.Has(SilentOil) && inv.Has(PrismPowder);

    /// Top the Sneak/Invis stock up to `to` of EACH powder from the AH (a crossing outlasts one application,
    /// so keep a dozen). The buy-to-N block was copy-pasted across LevelGrind, JobLifecycle, SubjobQuest and
    /// the fragile brains — this is that block. Callers keep their own reachability guard (only useful at an
    /// AH) and pass their own free-space callback (vendor sell vs no-op).
    public static async Task EnsureStock(IAuctionHouse ah, IPerception p, IInventory inv, int to,
                                         IReadOnlySet<ushort> keep, Func<CancellationToken, Task<int>>? freeSpace,
                                         CancellationToken ct, long keepGil = 0)
    {
        await ShopRoutines.BuyAtLeast(ah, p, inv, SilentOil, to, keep, freeSpace, ct, keepGil);
        await ShopRoutines.BuyAtLeast(ah, p, inv, PrismPowder, to, keep, freeSpace, ct, keepGil);
    }

    /// The fleet's travel stock: only a character that CAN'T ride (below the lv-20 mount) needs powders, and only
    /// a few. They cost 300-350 gil EACH here: topping every bot up to 12 of each drained the broke ones (Thifae,
    /// a mount-eligible SAM 27, spent ~1,100 gil on oils it never needed, 2026-10-08). Bought within the reserve.
    public const int TravelStock = 3;
    public static bool NeedsTravelStock(IPerception p) => p.World.MainJobLevel < 20;

    /// The full stealth-crossing: apply standing still, walk the zone route, drop stealth on arrival.
    /// (JobLifecycle and HomePointBrain carried copies; HomePointBrain's also leaked the maintainer —
    /// it discarded the CTS, so Sneak/Invis re-application kept burning powders after arrival.)
    public static async Task<bool> StealthCross(IZoning zoning, INavigation nav, IInventory inv, IPerception p,
                                                string zone, CancellationToken ct)
    {
        nav.Stop();
        using var cts = await BeginStealth(inv, p, ct);
        bool ok = await zoning.GoTo(zone, ct);
        cts.Cancel();
        await Task.Delay(2000, ct);
        return ok;
    }

    /// Apply Sneak+Invis once (standing still — item use is interrupted by movement) then start the background
    /// Maintain on a token linked to `ct`. Returns the CTS so the caller cancels it on arrival (SubjobQuest
    /// deliberately HOLDS its stealth past arrival, so it calls this directly instead of StealthCross).
    public static async Task<CancellationTokenSource> BeginStealth(IInventory inv, IPerception p, CancellationToken ct)
    {
        await Apply(inv, p, ct);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Maintain(inv, p, cts.Token);
        return cts;
    }

    /// TRAVEL PROTECTION for every walk across hostile ground (user 2026-10-07: all travel, chocobos too): ride if
    /// we can (mounted travel isn't aggroed, and it's faster), else Sneak + Invisible from the powder stock, applied
    /// standing still. Cheap when already covered (mounted, or both effects up), so it runs before every route leg
    /// (BotHost wires it as the zoning BeforeLeg default) and before long in-zone walks. Without it the fleet
    /// walked bare: a SAM19 and a NIN27 died to goblins/Quadavs walking to their party camp (2026-10-07).
    public static async Task PrepareTravel(INavigation nav, IInventory inv, IPerception p, CancellationToken ct)
    {
        var w = p.World;
        if (w.IsMounted) return;
        // Nothing spawns in this zone (a town): no aggro to hide from, so no powders burned on the walk out.
        if (Game.SpawnClusters.PointsIn(w.ZoneId).Count == 0) { nav.TryMount(); return; }
        if (nav.TryMount())   // eligible (lv20+, outdoor, off recast): give the server a moment to seat us
            for (int t = 0; t < 3000 && !w.IsMounted && !ct.IsCancellationRequested; t += 250) await Task.Delay(250, ct);
        // Use whatever stock we have: a broke bot that afforded only oils still gets Sneak (sound aggro).
        bool needSneak = !w.IsSneaked && inv.Has(SilentOil), needInvis = !w.IsInvisible && inv.Has(PrismPowder);
        if (w.IsMounted || (!needSneak && !needInvis)) return;
        nav.Stop();
        await Task.Delay(400, ct);   // settle: item use is interrupted by movement
        Log.Info($"[travel] on foot — {(needSneak && needInvis ? "Sneak + Invisible" : needSneak ? "Sneak" : "Invisible")} for the walk");
        int oil0 = inv.CountOf(SilentOil), prism0 = inv.CountOf(PrismPowder);
        bool Landed() => (!needSneak || w.IsSneaked) && (!needInvis || w.IsInvisible);
        for (int attempt = 0; attempt < 2 && !Landed(); attempt++)
        {
            // A use the server ignores consumes nothing and changes no status (live: a lone Prism Powder sent ~3s
            // after login never landed, 16 -> 16). Pause and try once more before walking out bare.
            if (attempt > 0) { Log.Info("[travel] stealth didn't take — retrying"); await Task.Delay(3000, ct); }
            await Apply(inv, p, ct);
            for (int t = 0; t < 2000 && !Landed(); t += 250) await Task.Delay(250, ct);
        }
        // used != landed: the counts show whether the server consumed the item at all
        Log.Info($"[travel] stealth status: sneak={w.IsSneaked} invisible={w.IsInvisible} (oil {oil0}->{inv.CountOf(SilentOil)}, prism {prism0}->{inv.CountOf(PrismPowder)})");
    }

    /// Apply Sneak + Invisible once (use both powders, spaced by the item recast). Returns true if both used.
    public static async Task<bool> Apply(IInventory inv, IPerception p, CancellationToken ct)
    {
        bool any = false;
        if (inv.SlotOf(SilentOil) is var oil && oil != 0) { inv.UseItem(0, (byte)oil); any = true; await Task.Delay(6000, ct); }       // Sneak
        if (inv.SlotOf(PrismPowder) is var prism && prism != 0) { inv.UseItem(0, (byte)prism); any = true; await Task.Delay(6000, ct); } // Invisible
        return any;
    }

    /// Background maintainer: keep Sneak + Invisible refreshed while traveling. Run it concurrently with a
    /// GoTo and cancel it on arrival. Re-applies every ~minute so the effects never lapse mid-transit (powder
    /// Sneak/Invis last a few minutes; refreshing early leaves no aggro window). Stops if out of powders.
    public static async Task Maintain(IInventory inv, IPerception p, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!HasPowders(inv)) { Log.Info("[stealth] out of powders!"); return; }
            await Apply(inv, p, ct);
            try { await Task.Delay(90000, ct); } catch (OperationCanceledException) { return; }
        }
    }
}
