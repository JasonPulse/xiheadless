namespace XiHeadless.Brains;

/// Equips the starting Onion Sword (16534) to the main hand and reports inventory + skills.
/// Headless char creation adds the weapon to inventory but does NOT equip it.
public sealed class EquipBrain(IPerception p, IGear gear) : IBrain
{
    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(4000, ct);   // let inventory (0x01F) + skills (0x062) stream in
        Log.Info($"[equip] inventory={p.World.Inventory.Count} items: " +
            string.Join(", ", p.World.Inventory.Select(kv => $"item{kv.Value}@c{kv.Key.container}s{kv.Key.slot}")));
        Log.Info($"[equip] hasOnionSword(16534)={gear.HasItem(16534)}");
        Log.Info($"[equip] skills: H2H={gear.SkillLevel(1)} Sword={gear.SkillLevel(3)} Axe={gear.SkillLevel(5)} GSword={gear.SkillLevel(4)}");
        const uint sword = 16534;   // Onion Sword
        bool ok = await gear.EquipItem(sword, 0, ct);   // 0 = SLOT_MAIN
        Log.Info($"[equip] equip item {sword} to main hand: sent={ok}");
        await Task.Delay(Timeout.Infinite, ct);
    }
}
