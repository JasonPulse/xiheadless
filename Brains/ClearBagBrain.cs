namespace XiHeadless.Brains;

/// One-off verification / utility: buy a guaranteed-sellable item, then call the general SellNearby
/// subroutine (which finds the nearest vendor itself — no shop hardcoded) and confirm it banks gil and
/// frees slots. Logs gil + item count before/after, then logs out.
public sealed class ClearBagBrain(IPerception p, IInventory inv, IShop shop, INavigation nav, IZoning zoning, IAuctionHouse ah, ILifecycle lifecycle) : IBrain
{
    static readonly HashSet<ushort> Keep = new()
    {
        16704, 16534, 13014, 17280, 13380, 13194, 13522,  // WAR gear + Onion Sword kept; only the bought crystal is junk.
    };

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(5000, ct);   // let inventory stream in

        if (!Game.Zonelines.HasAuctionHouse(zoning.CurrentZone))
        {
            Log.Info("[clearbag] traveling to an AH zone to buy the test item");
            await zoning.GoTo("Windurst Woods", ct);
            await Task.Delay(2000, ct);
        }

        // Buy a guaranteed-sellable item (Fire Crystal 4096) so there's something to verify selling against.
        Log.Info("[clearbag] buying a Fire Crystal (4096) as a sellable test item");
        await ShopRoutines.BuyItem(ah, p, inv, 4096, Keep,
            c => ShopRoutines.SellNearby(shop, nav, zoning, inv, p, Keep, c), ct);

        Log.Info($"[clearbag] before: gil={p.World.Gil}, inventory items={p.World.Inventory.Count}");
        // The whole sell flow in one call — find nearest vendor, travel, open shop, sell all junk.
        int sold = await ShopRoutines.SellNearby(shop, nav, zoning, inv, p, Keep, ct);
        Log.Info($"[clearbag] after: sold {sold} items, gil={p.World.Gil}, inventory items={p.World.Inventory.Count}");
        lifecycle.Logout();
    }
}
