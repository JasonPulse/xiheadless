namespace XiHeadless.Interfaces;

public interface ICombat
{
    bool Engaged { get; }
    uint Tp { get; }                                 // 0-3000; weaponskills need >= 1000
    bool CanWeaponSkill { get; }                     // Tp >= 1000
    bool Dead { get; }                               // KO'd (HP 0); needs Homepoint to recover
    Task<int> Consider(uint target, CancellationToken ct = default); // /check: 0=TooWeak..7=IncrediblyTough, -1=no reply
    Task Engage(uint target, CancellationToken ct = default);
    void Disengage();
    Task WeaponSkill(WeaponSkill ws, uint target, CancellationToken ct = default);
    Task Ability(Ability ability, uint target, CancellationToken ct = default);
    bool AbilityReady(Ability ability);              // job/level held AND off recast (client-side timer)
    // Fire a job ability IF the bot's job+level grants it and it's off cooldown — self-buffs auto-target
    // the bot, others target `enemyTarget`. Returns whether it actually fired (and only then delays).
    Task<bool> UseAbility(Ability ability, uint enemyTarget, CancellationToken ct = default);
    Task Homepoint(CancellationToken ct = default);  // accept "return to home point" -> revive at HP
    // Sit and /heal to regen HP (and MP — mages rest for MP) until both thresholds are met or it times out,
    // then stand. Disengages first and won't sit while still being attacked (the server refuses healing in
    // combat and hits cancel it). mpPct<=0 ignores MP. abort: polled ~500ms during the rest — return true to
    // stand immediately (a healer aborts the moment its tank acquires attackers, instead of sitting through
    // a pull). Returns true if the thresholds were reached.
    Task<bool> Rest(int hpPct, int mpPct = 0, Func<bool>? abort = null, CancellationToken ct = default);
}
