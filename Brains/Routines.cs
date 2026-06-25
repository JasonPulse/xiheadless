namespace XiHeadless.Brains;

// ============================================================================
// Reusable combat building blocks. A brain composes these by CALLING them — it
// is not a rule engine and these are not queued actions. WAR and MNK both call
// BuildTp to share non-WS melee logic, then each issues its own weaponskill.
// ============================================================================

public static class CombatRoutines
{
    /// Engage the target and hold auto-attack until TP reaches tpGoal (0-3000).
    /// Shared by every melee brain — the caller supplies the weaponskill afterward.
    public static async Task BuildTp(ICombat combat, IPerception p, uint target, int tpGoal, CancellationToken ct)
    {
        if (!combat.Engaged) await combat.Engage(target, ct);
        while (!ct.IsCancellationRequested && combat.Tp < tpGoal)
        {
            // Bail if the mob is gone/dead — nothing left to build TP on.
            var e = p.World.Entities.TryGetValue(target, out var ent) ? ent : null;
            if (e is null || e.Hpp == 0) return;
            await Task.Delay(500, ct);
        }
    }

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
