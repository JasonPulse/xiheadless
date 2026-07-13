namespace XiHeadless.Routines;

/// Stealth travel through aggressive zones (e.g. a low-level bot crossing Buburimu to Mhaura). Uses
/// consumables — Silent Oil (Sneak, blocks sound aggro) + Prism Powder (Invisible, blocks sight aggro) — so
/// the bot runs past goblins/Zu/etc. without being attacked. Reusable by ANY brain (this is the general
/// answer to "how does a bot survive a dangerous transit", not a teleport/handout). The powders are cheap
/// AH items; keep a stock (a crossing may outlast one application). USES the 0x037 USE_ITEM capability.
public static class StealthRoutines
{
    public const ushort SilentOil = 4165;    // -> Sneak  (sound aggro)
    public const ushort PrismPowder = 4164;  // -> Invisible (sight aggro)

    public static bool HasPowders(IInventory inv) => inv.Has(SilentOil) && inv.Has(PrismPowder);

    /// Top the Sneak/Invis stock up to `to` of EACH powder from the AH (a crossing outlasts one application,
    /// so keep a dozen). The buy-to-N block was copy-pasted across LevelGrind, JobLifecycle, SubjobQuest and
    /// the fragile brains — this is that block. Callers keep their own reachability guard (only useful at an
    /// AH) and pass their own free-space callback (vendor sell vs no-op).
    public static async Task EnsureStock(IAuctionHouse ah, IPerception p, IInventory inv, int to,
                                         IReadOnlySet<ushort> keep, Func<CancellationToken, Task<int>>? freeSpace,
                                         CancellationToken ct)
    {
        await ShopRoutines.BuyAtLeast(ah, p, inv, SilentOil, to, keep, freeSpace, ct);
        await ShopRoutines.BuyAtLeast(ah, p, inv, PrismPowder, to, keep, freeSpace, ct);
    }

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
