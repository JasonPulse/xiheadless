namespace XiHeadless.Routines;

// ============================================================================
// Reusable combat building blocks. A brain composes these by CALLING them — it
// is not a rule engine and these are not queued actions. Weapon-skill selection
// lives here; the actual kill loop is KillRoutine, driven by LevelGrind.
// ============================================================================

public static class CombatRoutines
{
    /// The SANCTIONED name exclusions (CLAUDE.md): sleep-lock mobs (Dream Flower / Sleepga chain-lock a
    /// low-DPS melee = con-blind certain death). Everything else is judged by CON alone.
    public static readonly string[] SleepLockMobs = { "Saplin", "Mandragora" };

    /// Non-combat objects that pass IsMob (chests, GoV books, maws, ???s) — never valid combat targets.
    /// (Extracted from LevelGrind; QuestRunner.KillWith once fought a Treasure_Casket for 15 seconds.)
    public static bool NotObject(Entity e) => !e.Name.Contains("Manual") && !e.Name.Contains("Maw")
        && !e.Name.Contains("Footprint") && !e.Name.Contains("Casket", StringComparison.OrdinalIgnoreCase)
        && !e.Name.Contains("Hieroglyphics", StringComparison.OrdinalIgnoreCase);

    /// The strongest weapon skill the SERVER says we can use right now (0x0AC: built from the weapon actually
    /// equipped, our skill and job). Not a brain's "which weapon at this level" guess: Mesae (NIN 29, no katana
    /// on) fired Blade: Teki 34x for nothing, and the logout defense always picked the H2H Combo (2026-11-18).
    /// Archery/Marksmanship WS only for a shooter with ammo on. Null until the server lists one.
    public static WeaponSkill? BestWeaponSkill(WorldState w, bool ranged)
    {
        bool shoot = ranged && w.Equipped.ContainsKey(EquipSlot.Ammo);
        return Actions.Ws.Values
            .Where(ws => w.CanUseWs((ushort)ws.Ws) && (shoot || ws.Type is not (25 or 26)))
            .OrderByDescending(ws => ws.SkillLevel)
            .Select(ws => (WeaponSkill?)ws.Ws)
            .FirstOrDefault();
    }
}
