namespace XiHeadless.Brains;

/// Bazaar vendor: on login, prices its sellable inventory items and opens a personal bazaar so other
/// players can buy from it. Behavior is code (flat price below; no item-value data yet). The bot just
/// stands where it logged in with its bazaar open. Reuses IBazaar + IPerception (inventory).
public sealed class BazaarBrain(IPerception p, IBazaar bazaar) : IBrain
{
    const uint Price = 1000;   // flat per-item bazaar price (until we have item-value data)

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(4000, ct);   // let inventory (0x01F/0x020) stream in after zone-in

        // Setup order matters: enter price-editing mode (hides the bazaar), set prices, then open it.
        bazaar.BeginEdit();
        await Task.Delay(500, ct);

        int priced = 0;
        foreach (var ((container, slot), itemId) in p.World.Inventory)
        {
            // Container 0 = main inventory; skip the gil slot (0 / item 65535) and empty slots.
            if (container != 0 || slot == 0 || itemId is 0 or 0xFFFF) continue;
            bazaar.SetPrice(slot, Price);
            Log.Info($"[bazaar] price item {itemId} @ inv slot {slot} = {Price} gil");
            priced++;
            await Task.Delay(300, ct);
        }

        bazaar.Open();   // finish editing -> bazaar visible to buyers (even with 0 priced, this just closes the menu)
        Log.Info(priced > 0 ? $"[bazaar] opened with {priced} item(s) for sale" : "[bazaar] no sellable items in inventory");

        await Task.Delay(Timeout.Infinite, ct);   // keep the bazaar open
    }
}
