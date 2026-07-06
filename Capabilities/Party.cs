using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

public sealed class Party(ISession s) : IParty
{
    public bool InvitePending => s.State.PartyInvitePending;
    public string InviterName => s.State.PartyInviterName;
    // Fellow members whose group vitals arrived recently (the server stops sending them once they leave, so
    // age out stale entries rather than trusting the dictionary to shrink).
    public int MemberCount
    {
        get
        {
            // LATCH the roster: once a member packet (0x0DD) has added someone, count them. Member-vitals only
            // re-push on combat/HP changes, so any staleness window ages a not-yet-fighting party out to 0,
            // collapsing heal + grind (both gate on MemberCount>0) and spamming re-invites — a chicken-egg
            // (combat needs the party, the roster needs combat). The duo doesn't leave mid-run, so just count.
            return s.State.PartyMembers.Count;
        }
    }
    public void Invite(uint charId, ushort targid = 0) => s.Enqueue(GroupInvitePacket.Build(charId, targid));
    public void AcceptInvite() { s.Enqueue(GroupAnswerPacket.Build(1)); s.State.PartyInvitePending = false; }
    public void SetLevelSync(string targetName) => s.Enqueue(GroupChange2Packet.Build(targetName, 6)); // ChangeKind 6 = SetLevelSync
}

/// 0x077 GP_CLI_COMMAND_GROUP_CHANGE2: hdr(4) sName[16]@4 (member name, ASCII, null-padded) Kind@20(u8; 0=Party)
/// ChangeKind@21(u8; 6=SetLevelSync, 7=DisableLevelSync). 24 bytes = 6 words. Sent by the party LEADER only.
internal static class GroupChange2Packet
{
    public static byte[] Build(string memberName, byte changeKind)
    {
        var p = new byte[24];
        SubPacket.WriteHeader(p, 0x077);
        var name = System.Text.Encoding.ASCII.GetBytes(memberName);
        Array.Copy(name, 0, p, 4, Math.Min(name.Length, 15));   // sName[16] @4, leave a null terminator
        p[20] = 0;            // Kind = Party
        p[21] = changeKind;   // ChangeKind = SetLevelSync (6)
        return p;
    }
}

/// 0x06E GP_CLI_COMMAND_GROUP_SOLICIT_REQ (party invite): hdr(4) UniqueNo@4(u32 = invitee char id)
/// ActIndex@8(u16 targid; 0 => look up by char id) Kind@10(u8; 0 = Party). 12 bytes = 3 words (PacketSize[0x06E]=0x06).
internal static class GroupInvitePacket
{
    public static byte[] Build(uint charId, ushort targid)
    {
        var p = new byte[12];
        SubPacket.WriteHeader(p, 0x06E);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), charId);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(8), targid);
        p[10] = 0; // Kind = Party
        return p;
    }
}

/// 0x074 GP_CLI_COMMAND_GROUP_SOLICIT_RES (invite answer): hdr(4) Res@4(u8; 1 = Accept, 0 = Decline).
/// 8 bytes = 2 words (PacketSize[0x074]=0x00 = any size accepted). The server matches the inviter from our
/// stored InvitePending, so no inviter fields are needed.
internal static class GroupAnswerPacket
{
    public static byte[] Build(byte res)
    {
        var p = new byte[8];
        SubPacket.WriteHeader(p, 0x074);
        p[4] = res;
        return p;
    }
}
