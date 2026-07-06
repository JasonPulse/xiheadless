namespace XiHeadless.Routines;

/// ONE read of the 0x056 quest-log bitmap (WorldState.QuestLog): quest N -> byte[N/8] bit (N%8). Replaces
/// the byte-for-byte copies that lived in SubjobQuest + SubjobBrain (and now backs JobLifecycle's unlock
/// mid-chain resume). Ports: active/accepted bitmaps 0x50-0x88, completed bitmaps 0x90-0xC8 (per nation area).
public static class QuestState
{
    static bool Bit(WorldState w, ushort port, int questId) =>
        w.QuestLog.TryGetValue(port, out var bits) && bits.Length > questId / 8
        && (bits[questId / 8] & (1 << (questId % 8))) != 0;

    /// True if quest `questId` is ACCEPTED/active in its area's active bitmap `port`.
    public static bool QuestAccepted(WorldState w, ushort port, int questId) => Bit(w, port, questId);

    /// True if quest `questId` is COMPLETE in its area's complete bitmap `port`.
    public static bool QuestComplete(WorldState w, ushort port, int questId) => Bit(w, port, questId);
}
