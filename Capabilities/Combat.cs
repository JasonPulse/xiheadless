namespace XiHeadless.Capabilities;

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
    Task Homepoint(CancellationToken ct = default);  // accept "return to home point" -> revive at HP
}

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
    // The server resolves the target of an action by its ActIndex (targid), NOT UniqueNo
    // (0x01a_action.cpp: PAI->Engage(this->ActIndex)). We send both: UniqueNo for reference and
    // ActIndex (looked up from the tracked entity) as the actual target. ActIndex=0 => no-op.
    ushort IndexOf(uint id) => s.State.Entities.TryGetValue(id, out var e) ? e.Index : (ushort)0;

    // /check (con): 0x0DD with Kind=Check; the 0x029 reply carries mob level + 64+difficulty,
    // captured into WorldState by the parser. Returns the difficulty (0-7) or -1 if no reply.
    public async Task<int> Consider(uint target, CancellationToken ct = default)
    {
        s.State.ConTargetId = target;
        s.State.ConDifficulty = -1;
        s.Enqueue(CheckPacket.Build(target, IndexOf(target)));
        for (int t = 0; t < 3000 && s.State.ConDifficulty < 0; t += 100) await Task.Delay(100, ct);
        return s.State.ConDifficulty;
    }

    // Actions enqueue synchronously (before the first await); the Task completes after a
    // coarse action latency. TODO: complete on the real action-result packet.
    public async Task Engage(uint target, CancellationToken ct = default)
    { s.State.CurrentTargetId = target; _engaged = true; s.Enqueue(ActionPacket.Build(ActionPacket.Attack, target, IndexOf(target))); await Task.Delay(300, ct); }
    public void Disengage() { _engaged = false; s.Enqueue(ActionPacket.Build(ActionPacket.AttackOff, s.State.CurrentTargetId, IndexOf(s.State.CurrentTargetId))); }
    public async Task WeaponSkill(WeaponSkill ws, uint target, CancellationToken ct = default)
    { s.Enqueue(ActionPacket.Build(ActionPacket.Weaponskill, target, IndexOf(target), (uint)ws)); await Task.Delay(1000, ct); }
    public async Task Ability(Ability ability, uint target, CancellationToken ct = default)
    { s.Enqueue(ActionPacket.Build(ActionPacket.JobAbility, target, IndexOf(target), (uint)ability)); await Task.Delay(500, ct); }
    // HomepointMenu(0x0B) StatusId=Accept(0): server sets requestedWarp -> revive at home point (a zone
    // change, handled by the 0x0B re-key path). Server-side validate requires the char to be dead.
    public async Task Homepoint(CancellationToken ct = default)
    { _engaged = false; s.Enqueue(ActionPacket.Build(ActionPacket.HomepointMenu, s.State.MyId, 0, 0)); await Task.Delay(3000, ct); }
}
