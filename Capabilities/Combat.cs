namespace XiHeadless.Capabilities;

public sealed class Combat(ISession s) : ICombat
{
    // Authoritative engaged signal. ServerStatus==1 is NOT a reliable engaged indicator, so we
    // track intent locally: set on Engage, cleared on Disengage / Homepoint, and self-clearing
    // when the engaged target dies or we die. This keeps WS from ever firing outside an active
    // fight (the "Character is not engaged" rejections came from trusting ServerStatus / raw HP).
    bool _engaged;
    public bool Engaged =>
        _engaged && !Dead
        && s.State.Entities.TryGetValue(s.State.CurrentTargetId, out var e) && e.Hpp > 0;
    public uint Tp => s.State.Tp;
    public bool CanWeaponSkill => s.State.Tp >= 1000;
    public bool Dead => s.State.MaxHp > 0 && s.State.Hpp == 0; // KO'd (once stats have loaded)
    // Target resolution (UniqueNo -> ActIndex/targid) is the shared WorldState.TargidOf (self + &0xFFF fallback).
    // The server resolves the target of an action by its ActIndex (0x01a_action.cpp: PAI->Engage(ActIndex)).

    // /check (con): 0x0DD with Kind=Check; the 0x029 reply carries mob level + 64+difficulty,
    // captured into WorldState by the parser. Returns the difficulty (0-7) or -1 if no reply.
    public async Task<int> Consider(uint target, CancellationToken ct = default)
    {
        s.State.ConTargetId = target;
        s.State.ConDifficulty = -1;
        s.Enqueue(CheckPacket.Build(target, s.State.TargidOf(target)));
        for (int t = 0; t < 3000 && s.State.ConDifficulty < 0; t += 100) await Task.Delay(100, ct);
        return s.State.ConDifficulty;
    }

    // Actions enqueue synchronously (before the first await); the Task completes after a
    // coarse action latency. TODO: complete on the real action-result packet.
    public async Task Engage(uint target, CancellationToken ct = default)
    {
        // NEVER engage from the seated /heal state (ANIMATION_HEALING=33): skipping the stand toggle makes
        // other clients render us out of combat (user-observed desync). Stand, let the state land, then attack.
        if (s.State.ServerStatus == 33)
        {
            s.Enqueue(CampPacket.Build(2));
            await Task.Delay(1200, ct);
        }
        s.State.CurrentTargetId = target; _engaged = true; s.Enqueue(ActionPacket.Build(ActionPacket.Attack, target, s.State.TargidOf(target))); await Task.Delay(300, ct);
    }
    public void Disengage() { _engaged = false; s.Enqueue(ActionPacket.Build(ActionPacket.AttackOff, s.State.CurrentTargetId, s.State.TargidOf(s.State.CurrentTargetId))); }
    public async Task WeaponSkill(WeaponSkill ws, uint target, CancellationToken ct = default)
    { s.Enqueue(ActionPacket.Build(ActionPacket.Weaponskill, target, s.State.TargidOf(target), (uint)ws)); await Task.Delay(1000, ct); }
    public async Task Ability(Ability ability, uint target, CancellationToken ct = default)
    { s.Enqueue(ActionPacket.Build(ActionPacket.JobAbility, target, s.State.TargidOf(target), (uint)ability)); s.State.AbilityUsedMs[ability] = s.State.NowMs; await Task.Delay(500, ct); }
    // Ranged pull (0x01A Shoot): needs a ranged/throwing item equipped — the puller carries a NON-EXPENDABLE
    // returning item (boomerang) so pulls never consume ammo and the mob chases to camp instead of beating
    // on a melee-range puller.
    public void RangedAttack(uint target) => s.Enqueue(ActionPacket.Build(ActionPacket.Shoot, target, s.State.TargidOf(target)));

    // Ready = the bot's main/sub job grants this ability at its level AND it's off recast. Recast is tracked
    // client-side from AbilityInfo.Recast (seconds); the server enforces the real timer regardless.
    public bool AbilityReady(Ability ability)
    {
        if (!Actions.Abilities.TryGetValue(ability, out var info)) return false;
        bool haveJob =
            (info.Job == s.State.MainJob && s.State.MainJobLevel >= info.Level) ||
            (info.Job == s.State.SubJob && s.State.SubJobLevel >= info.Level);
        if (!haveJob) return false;
        if (!s.State.AbilityUsedMs.TryGetValue(ability, out var used)) return true;
        return s.State.NowMs - used >= info.Recast * 1000L;
    }

    public async Task<bool> UseAbility(Ability ability, uint enemyTarget, CancellationToken ct = default)
    {
        if (!AbilityReady(ability)) return false;                       // not learned / on cooldown -> skip, no delay
        var info = Actions.Abilities[ability];
        // validTarget bit 0x01 = self (Berserk/Aggressor/Warcry/Mighty Strikes); everything else hits the enemy.
        uint tgt = (info.ValidTarget & 0x01) != 0 ? s.State.MyId : enemyTarget;
        s.Enqueue(ActionPacket.Build(ActionPacket.JobAbility, tgt, s.State.TargidOf(tgt), (uint)ability));
        s.State.AbilityUsedMs[ability] = s.State.NowMs;
        await Task.Delay(600, ct);
        return true;
    }
    // HomepointMenu(0x0B) StatusId=Accept(0): server sets requestedWarp -> revive at home point (a zone
    // change, handled by the 0x0B re-key path). Server-side validate requires the char to be dead.
    public async Task Homepoint(CancellationToken ct = default)
    { _engaged = false; s.Enqueue(ActionPacket.Build(ActionPacket.HomepointMenu, s.State.MyId, 0, 0)); await Task.Delay(3000, ct); }

    public async Task<bool> Rest(int hpPct, int mpPct = 0, Func<bool>? abort = null, CancellationToken ct = default)
    {
        bool Done() => s.State.Hpp >= hpPct && (mpPct <= 0 || s.State.Mpp >= mpPct);
        if (Done()) return true;
        if (_engaged) Disengage();         // never /heal in combat — the server refuses it and hits cancel it
        // Settle: stand for a moment after disengaging and make sure we're NOT still being hit. A mob with
        // hate keeps attacking after we disengage, so if HP is still slipping we're in a fight — don't sit.
        byte floor = s.State.Hpp;
        for (int t = 0; t < 2500 && !ct.IsCancellationRequested; t += 500)
        {
            await Task.Delay(500, ct);
            if (s.State.Hpp < floor) return false;   // still taking damage -> in combat, abort resting
        }
        s.Enqueue(CampPacket.Build(1));    // Mode On -> start resting (fast HP + MP regen)
        for (int t = 0; t < 60000 && !Done() && !Dead && !ct.IsCancellationRequested; t += 500)
        {
            await Task.Delay(500, ct);
            if (abort?.Invoke() == true) break;                  // caller needs us up NOW (e.g. tank pulled)
            if (s.State.Hpp > floor) floor = s.State.Hpp;        // healing is taking — raise the floor
            else if (t >= 3000 && s.State.Hpp < floor) break;    // got hit mid-rest -> stop feeding the mob
        }
        s.Enqueue(CampPacket.Build(2));    // Mode Off -> stand back up
        await Task.Delay(500, ct);
        return Done();
    }
}
