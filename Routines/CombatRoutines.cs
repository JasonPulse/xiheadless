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

    /// Turn a tracked combat-skill level into an actual weaponskill choice: the strongest WS of
    /// the given skill type (3=Sword, 5=Axe, 1=H2H, ...) that this skill level has unlocked.
    /// Returns null if none is unlocked yet (e.g. the first sword WS, Fast Blade, needs skill 5).
    /// This is how a brain uses its skills data instead of a hardcoded "fire at skill 10".
    public static WeaponSkill? BestWeaponSkill(byte skillType, int skillLevel) =>
        Actions.Ws.Values
            // SkillLevel 0 = merit/relic/mythic WS (e.g. Metatron Torment, King's Justice), NOT unlocked
            // by skill — exclude them so we pick the highest genuinely skill-unlocked WS (first tier = 5).
            .Where(w => w.Type == skillType && w.SkillLevel > 0 && w.SkillLevel <= skillLevel)
            .OrderByDescending(w => w.SkillLevel)
            .Select(w => (WeaponSkill?)w.Ws)
            .FirstOrDefault();
}
