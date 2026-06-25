namespace XiHeadless.Brains;

/// Crafter: sources the recipe's mats from the Auction House (whatever isn't already in inventory),
/// then synthesizes. Recipe is CODE (consts below) — Bronze Sheet = Fire Crystal + 1 Bronze Ingot
/// (Smithing 4, recipe 10008). Does a small batch then logs out gracefully. Reuses IAuctionHouse +
/// ICrafting + IPerception (inventory) + ILifecycle.
public sealed class CraftBrain(IPerception p, IAuctionHouse ah, ICrafting craft, IZoning zoning, ILifecycle lifecycle) : IBrain
{
    // Recipe 10008: Crystal 4096 (Fire) + Ingredient 649 (Bronze Ingot) x1 -> Result 660 (Bronze Sheet).
    const ushort Crystal = 4096;
    const ushort Ingredient = 649;
    const ushort Result = 660;
    const int MaxSynths = 3;             // craft this many then log out (test batch)
    const int RecipeSkill = 50;          // Smithing (the skill this recipe trains); see SkillName/0x062
    const string AhZone = "Windurst Woods"; // has MISC_AH (Port Windurst, where we log in, doesn't)

    // The server charges our EXACT bid (not the listing price) and only fills if a listing <= bid
    // exists, so we escalate from low to high and stop the instant the item arrives — paying close to
    // the real price instead of overpaying. A failed bid costs nothing.
    static readonly uint[] BidLadder = { 50, 250, 1000, 4000, 15000 };

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(4000, ct);   // let inventory (0x01F/0x020) stream in after zone-in
        Console.WriteLine($"[craft] char='{p.World.MyName}' gil={p.World.Gil} zone={zoning.CurrentZone} — recipe: {Crystal}+{Ingredient}->{Result}");

        // AH bids are dropped at validation unless we're in a MISC_AH zone, so travel there first.
        if (!Game.Zonelines.HasAuctionHouse(zoning.CurrentZone))
        {
            Console.WriteLine($"[craft] no AH in zone {zoning.CurrentZone} — walking to {AhZone}");
            if (!await zoning.GoTo(AhZone, ct)) { Console.WriteLine($"[craft] failed to reach {AhZone} (in {zoning.CurrentZone}) — logging out"); lifecycle.Logout(); return; }
            await Task.Delay(2000, ct);   // let inventory/state resettle after the zone change
        }

        int startSkill = p.World.SkillLevel(RecipeSkill), startGains = p.World.SkillGains[RecipeSkill];
        Console.WriteLine($"[craft] Smithing skill {startSkill} at start");

        int made = 0;
        for (int n = 0; n < MaxSynths && !ct.IsCancellationRequested; n++)
        {
            int gainsBefore = p.World.SkillGains[RecipeSkill];
            if (!await EnsureItem(Crystal, ct))    { Console.WriteLine($"[craft] could not buy crystal {Crystal} from AH — stopping"); break; }
            if (!await EnsureItem(Ingredient, ct)) { Console.WriteLine($"[craft] could not buy ingredient {Ingredient} from AH — stopping"); break; }

            byte cSlot = SlotOf(Crystal)!.Value, iSlot = SlotOf(Ingredient)!.Value;
            Console.WriteLine($"[craft] synth #{n + 1}: crystal {Crystal}@{cSlot} + {Ingredient}@{iSlot}");
            craft.Synth(Crystal, cSlot, new[] { (Ingredient, iSlot) });

            // Wait for the synth to resolve: success consumes the mats and yields the result.
            bool ok = false;
            for (int i = 0; i < 12 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(500, ct);
                if (SlotOf(Result) != null) { ok = true; break; }     // result item appeared
                if (SlotOf(Crystal) == null) break;                   // crystal consumed (success or break)
            }
            if (ok) made++;
            int upThisSynth = p.World.SkillGains[RecipeSkill] - gainsBefore;
            Console.WriteLine($"[craft] synth #{n + 1} -> {(ok ? "produced " + Result : "no result (fail/break or low skill)")} (made {made}){(upThisSynth > 0 ? $"; Smithing +{upThisSynth / 10.0:0.0}" : "")}");
            await Task.Delay(2000, ct);   // synth animation/cooldown before the next
        }

        // Note: the synth skill-up message (0x029) can arrive 15s+ after the result — sometimes only
        // during logout — so this session total is best-effort. Each skill-up is logged authoritatively
        // as a "[skill-up] ..." line the moment it arrives, which is the real record.
        await Task.Delay(3000, ct);
        double gained = (p.World.SkillGains[RecipeSkill] - startGains) / 10.0;
        Console.WriteLine($"[craft] done — {made}/{MaxSynths} synth(s) produced {Result}; Smithing +{gained:0.0} this session (now ~{p.World.SkillLevel(RecipeSkill)}); logging out");
        lifecycle.Logout();
    }

    // First inventory (container 0) slot holding itemId, or null if we don't have it.
    byte? SlotOf(ushort itemId)
    {
        foreach (var ((container, slot), id) in p.World.Inventory)
            if (container == 0 && id == itemId) return slot;
        return null;
    }

    // Ensure itemId is in inventory: if missing, escalate AH bids (single then stack at each rung)
    // until it arrives or the ladder is exhausted.
    async Task<bool> EnsureItem(ushort itemId, CancellationToken ct)
    {
        if (SlotOf(itemId) != null) return true;   // already have one (leftover or GM-given)
        foreach (var bid in BidLadder)
        {
            foreach (var single in new[] { true, false })
            {
                if (bid > p.World.Gil) { Console.WriteLine($"[craft] bid {bid} > gil {p.World.Gil} — out of budget"); return SlotOf(itemId) != null; }
                ah.Bid(itemId, bid, single);
                Console.WriteLine($"[craft] bid {bid} for {itemId} (single={single})");
                for (int i = 0; i < 6 && !ct.IsCancellationRequested; i++)
                {
                    await Task.Delay(500, ct);
                    if (SlotOf(itemId) != null) { Console.WriteLine($"[craft] acquired {itemId} for <= {bid}"); return true; }
                }
            }
        }
        return SlotOf(itemId) != null;
    }
}
