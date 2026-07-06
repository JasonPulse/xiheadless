namespace XiHeadless.Interfaces;

/// Party formation. A bot invites another PC by char id (0x06E); the invitee receives a pending invite
/// (0x0DC) and accepts it (0x074, Res=1). The inviter<->invitee pairing is tracked server-side, so accept
/// takes no args. For two bots: the inviter finds the partner's entity in-zone (its Id == char id, Index ==
/// targid) and invites; the invitee polls InvitePending and accepts.
public interface IParty
{
    bool InvitePending { get; }                      // a party invite (0x0DC) arrived and isn't answered yet
    string InviterName { get; }                      // name of whoever sent the pending invite
    int MemberCount { get; }                          // fellow party members currently in the roster (0 = solo) — confirms a party formed
    void Invite(uint charId, ushort targid = 0);     // 0x06E — invite a PC to the party (targid 0 = lookup by id)
    void AcceptInvite();                             // 0x074 Res=1 — accept the pending invite
    // 0x077 GROUP_CHANGE2 SetLevelSync — the party LEADER syncs the whole party down to targetName's level
    // (EFFECT_LEVEL_SYNC). Lets a high char tank low mobs in a SAFE zone so a low member gets FULL (un-gapped)
    // exp. Pass the lowest member's name to sync everyone to them.
    void SetLevelSync(string targetName);
}
