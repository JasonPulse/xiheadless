namespace XiHeadless.Brains;

/// One-off verification / utility: walk to a vendor, open its shop, then sell ALL junk (everything not
/// in the keep-set) to confirm inventory clearing banks gil and frees slots. Selling requires a vendor's
/// shop to be OPEN (the 0x084 appraisal uses the shop container the open creates), so we Talk the vendor
/// first. Logs gil + item count before/after, then logs out.
public sealed class ClearBagBrain(IPerception p, IInventory inv, IShop shop, INavigation nav, IZoning zoning, ILifecycle lifecycle) : IBrain
{
    const uint Vendor = 17764456u;                              // Manyny, Windurst Woods scroll merchant (unconditional shop)
    static readonly (float x, float y, float z) VendorPos = (18.7f, -4.55f, -155.92f);
    static readonly HashSet<ushort> Keep = new()
    {
        16534, 16704, 13014, 17280, 13380, 13194, 13522,  // WAR gear
        4096, 649,                                         // craft inputs (kept); Bronze Sheets (660) NOT kept -> sell them to verify selling
    };

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(5000, ct);   // let inventory stream in

        if (!Game.Zonelines.HasAuctionHouse(zoning.CurrentZone))   // Windurst Woods (the vendor's zone) also has the AH
        {
            Console.WriteLine("[clearbag] traveling to Windurst Woods for the vendor");
            await zoning.GoTo("Windurst Woods", ct);
            await Task.Delay(2000, ct);
        }

        Console.WriteLine($"[clearbag] walking to vendor at ({VendorPos.x:F0},{VendorPos.z:F0})");
        nav.MoveTo(VendorPos.x, VendorPos.y, VendorPos.z);
        for (int t = 0; t < 60000 && p.DistanceTo(VendorPos.x, VendorPos.z) > 5f && nav.IsMoving && !ct.IsCancellationRequested; t += 200)
            await Task.Delay(200, ct);
        nav.Stop();
        Console.WriteLine($"[clearbag] at vendor, dist {p.DistanceTo(VendorPos.x, VendorPos.z):F0}y — opening shop");
        var stock = await shop.Open(Vendor, ct);
        Console.WriteLine($"[clearbag] shop open: {stock.Count} items listed (0 = open failed)");

        Console.WriteLine($"[clearbag] before: gil={p.World.Gil}, inventory items={p.World.Inventory.Count}");
        int sold = await inv.SellAllJunk(Keep, ct);
        Console.WriteLine($"[clearbag] after: sold {sold} items, gil={p.World.Gil}, inventory items={p.World.Inventory.Count}");
        lifecycle.Logout();
    }
}
