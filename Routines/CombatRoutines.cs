namespace XiHeadless.Routines;

// ============================================================================
// Reusable combat building blocks. A brain composes these by CALLING them — it
// is not a rule engine and these are not queued actions. Weapon-skill selection
// lives here; the actual kill loop is KillRoutine, driven by LevelGrind.
// ============================================================================

public static class CombatRoutines
{
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
