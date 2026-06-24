namespace XiHeadless.Brains;

/// MNK: same shared BuildTp, different weaponskill — no duplicated combat code.
public sealed class MnkBrain(IPerception p, ICombat combat) : IBrain
{
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var mob = p.Nearest(e => e.Hpp is > 0 and < 100);
            if (mob is null) { await Task.Delay(1000, ct); continue; }
            await CombatRoutines.BuildTp(combat, p, mob.Id, 1000, ct);     // SAME routine
            await combat.WeaponSkill(WeaponSkill.Combo, mob.Id, ct);        // MNK-specific
        }
    }
}
