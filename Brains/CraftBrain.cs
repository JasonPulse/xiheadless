namespace XiHeadless.Brains;

/// Crafter: sources the recipe's mats from the Auction House (whatever isn't already in inventory),
/// then synthesizes. Recipe is CODE (consts below) — Bronze Sheet = Fire Crystal + 1 Bronze Ingot
/// (Smithing 4, recipe 10008). Does a small batch then logs out gracefully. Reuses IAuctionHouse +
/// ICrafting + IPerception (inventory) + ILifecycle.
public sealed class CraftBrain(IPerception p, IAuctionHouse ah, ICrafting craft, IZoning zoning, ILifecycle lifecycle, IInventory inv, IShop shop, INavigation nav) : IBrain
{
    // Recipe 10008: Crystal 4096 (Fire) + Ingredient 649 (Bronze Ingot) x1 -> Result 660 (Bronze Sheet).
    const ushort Crystal = 4096;
    const ushort Ingredient = 649;
    const int MaxSynths = 3;             // craft this many then log out (test batch)
    const int RecipeSkill = 50;          // Smithing (the skill this recipe trains); see SkillName/0x062
    const string AhZone = "Windurst Woods"; // has MISC_AH (Port Windurst, where we log in, doesn't)

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(4000, ct);   // let inventory (0x01F/0x020) stream in after zone-in
        Console.WriteLine($"[craft] char='{p.World.MyName}' gil={p.World.Gil} zone={zoning.CurrentZone} — recipe: {Crystal}+{Ingredient} (Bronze Sheet)");

        // AH bids are dropped at validation unless we're in a MISC_AH zone, so travel there first.
        if (!Game.Zonelines.HasAuctionHouse(zoning.CurrentZone))
        {
            Console.WriteLine($"[craft] no AH in zone {zoning.CurrentZone} — walking to {AhZone}");
            if (!await zoning.GoTo(AhZone, ct)) { Console.WriteLine($"[craft] failed to reach {AhZone} (in {zoning.CurrentZone}) — logging out"); lifecycle.Logout(); return; }
            await Task.Delay(2000, ct);   // let inventory/state resettle after the zone change
        }

        int startGains = p.World.SkillGains[RecipeSkill];
        Console.WriteLine($"[craft] Smithing level {p.World.SkillLevel(RecipeSkill)} at start");

        int made = 0;
        for (int n = 0; n < MaxSynths && !ct.IsCancellationRequested; n++)
        {
            int gainsBefore = p.World.SkillGains[RecipeSkill];
            if (!await EnsureItem(Crystal, ct))    { Console.WriteLine($"[craft] could not buy crystal {Crystal} from AH — stopping"); break; }
            if (!await EnsureItem(Ingredient, ct)) { Console.WriteLine($"[craft] could not buy ingredient {Ingredient} from AH — stopping"); break; }

            byte cSlot = (byte)inv.SlotOf(Crystal), iSlot = (byte)inv.SlotOf(Ingredient);   // EnsureItem guarantees both present
            Console.WriteLine($"[craft] synth #{n + 1}: crystal {Crystal}@{cSlot} + {Ingredient}@{iSlot}");

            // Synth waits for the server's 0x06F result (0=Success, 1/2/14=fail/break, 6=skill too low,
            // 13=must wait — retry). The skill-up (0x029) lands right around the result now.
            int res = await craft.Synth(Crystal, cSlot, new[] { (Ingredient, iSlot) }, ct);
            if (res == 13) { Console.WriteLine("[craft] server: must wait longer — retrying"); await Task.Delay(3000, ct); n--; continue; }
            if (res == 0) made++;
            await Task.Delay(1500, ct);   // let the trailing skill-up (0x029) for this synth land
            int upThisSynth = p.World.SkillGains[RecipeSkill] - gainsBefore;
            string outcome = res == 0 ? "Success" : res < 0 ? "timeout" : res is 1 or 2 or 14 ? "broke" : res == 6 ? "skill too low" : $"code{res}";
            Console.WriteLine($"[craft] synth #{n + 1} -> {outcome} (made {made}){(upThisSynth > 0 ? $"; Smithing +{upThisSynth / 10.0:0.0}" : "")}");
        }

        double gained = (p.World.SkillGains[RecipeSkill] - startGains) / 10.0;
        Console.WriteLine($"[craft] done — {made}/{MaxSynths} succeeded; Smithing +{gained:0.0} this session (now level {p.World.SkillLevel(RecipeSkill)}); logging out");
        lifecycle.Logout();
    }

    // Keep the recipe mats when freeing inventory space (never drop what we're about to synth).
    static readonly HashSet<ushort> Keep = new() { Crystal, Ingredient };

    // Ensure itemId is in inventory: buy it from the AH (shared coroutine: escalate bid + free space if the
    // bag is full) if missing. A full bag triggers a sell trip to the nearest vendor (SellNearby), not a
    // blind in-place sell.
    Task<bool> EnsureItem(ushort itemId, CancellationToken ct) =>
        ShopRoutines.BuyItem(ah, p, inv, itemId, Keep,
            c => ShopRoutines.SellNearby(shop, nav, zoning, inv, p, Keep, c), ct);
}
