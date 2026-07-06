namespace XiHeadless.Brains;

/// Sets the home point at a Home Point crystal — the reliable, game-intended action (examine crystal,
/// select "Set Home Point" = option 1, server calls setHomePoint()). Used as a workaround when the New
/// Character Cutscene can't run (e.g. a headless char whose notSeen was never set), so death would
/// otherwise warp to zone-0 limbo. Run once in a starting city; the char must be IN that city. Pure
/// navigation + a deliberate event (brain activity); the home point set itself is server-side.
public sealed class HomePointBrain(IPerception p, INavigation nav, IZoning zoning, IEvents events, ICombat combat, IInventory inv, IAuctionHouse ah, ILifecycle lifecycle) : IBrain
{
    static readonly HashSet<ushort> Keep = new() { StealthRoutines.SilentOil, StealthRoutines.PrismPowder };

    const ushort TargetZone = 249;  // Mhaura — the home point we want all southern-op bots to use

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(4000, ct);
        // Force-travel to Mhaura (TargetZone) even if we're standing at another mapped crystal — we want
        // EVERY southern-op bot homepointed at Mhaura (next to Buburimu + Vera). The Mhaura route crosses
        // aggressive Buburimu, so: (1) rest to full HP, and (2) stock + maintain Sneak (Silent Oil) + Invis
        // (Prism Powder) so a low-level char (the lv9 WHM) survives the crossing — "using items to get there".
        if (zoning.CurrentZone != TargetZone)
        {
            if (!combat.Dead && p.World.Hpp < 90) { Log.Info($"[hp] resting to full HP ({p.World.Hpp}%) before crossing to Mhaura"); await combat.Rest(95, 0, null, ct); }
            if (inv.CountOf(StealthRoutines.SilentOil) < 6 || inv.CountOf(StealthRoutines.PrismPowder) < 6)
            {
                if (!Game.Zonelines.HasAuctionHouse(zoning.CurrentZone)) { Log.Info("[hp] -> Windurst Woods for stealth powders"); await zoning.GoTo("Windurst Woods", ct); }
                await StealthRoutines.EnsureStock(ah, p, inv, 12, Keep, ShopRoutines.NoFree, ct);
                Log.Info($"[hp] powders: oil={inv.CountOf(StealthRoutines.SilentOil)} prism={inv.CountOf(StealthRoutines.PrismPowder)}");
            }
            _ = await StealthRoutines.BeginStealth(inv, p, ct);
            Log.Info("[hp] crossing to Mhaura under Sneak+Invis to set the home point");
            await zoning.GoTo("Mhaura", ct);
        }
        // Crystal-set MECHANICS (walk to the crystal, clear blocking zone-in events, Examine + blind EVENTEND
        // option 1 = SET_HOMEPOINT) live in the shared HomePointRoutines.SetHere — a hardened superset of the
        // old inline flow. This brain only CHOOSES to set at Mhaura and drove the crossing above.
        ushort zone = zoning.CurrentZone;
        if (!await HomePointRoutines.SetHere(p, nav, events, combat, zone, ct))
            Log.Info($"[hp] home point NOT set for zone {zone} (no crystal mapped, or arrived out of range)");
        await Task.Delay(2500, ct);
        lifecycle.Logout();
    }
}
