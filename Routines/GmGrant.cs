namespace XiHeadless.Routines;

/// Ask the central GM bot to unlock a job / add a spell for us — the sanctioned way a fleet bot gets the
/// things it can't self-do. Sends an in-game /tell to the GM character; the GM bot (GmBrain) grants to the
/// tell's SENDER (no target spoofing) and REPLIES — that reply is our ACK.
///
/// RETRY UNTIL ACKED (user rule): the GM bot's login can lag OURS by ~5 minutes (its cron tick + init gate),
/// and a tell to an offline player is silently LOST. So we re-send every minute until the GM's reply arrives,
/// for up to MaxWaitMs. Re-sent requests are harmless — the grants are idempotent (!grantjob/!addspell).
/// Returns true once acked; false if the window expires (caller falls back, e.g. the unlock quest).
public static class GmGrant
{
    const string GmChar = "Guge";      // the central GM bot's character (tell target)
    const int RetryEveryMs = 60_000;   // re-send cadence while unacked
    const int MaxWaitMs = 8 * 60_000;  // covers the GM's worst-case login lag behind us

    public static Task<bool> RequestJob(IPerception p, IChat chat, string jobName, string tag, CancellationToken ct)
        => Request(p, chat, $"grantjob {jobName}", "granted", tag, ct);

    public static Task<bool> RequestSpell(IPerception p, IChat chat, string spellIdOrName, string tag, CancellationToken ct)
        => Request(p, chat, $"spell {spellIdOrName}", "added", tag, ct);

    static async Task<bool> Request(IPerception p, IChat chat, string request, string ackWord, string tag, CancellationToken ct)
    {
        var w = p.World;
        long askedMs = w.NowMs;
        long startTick = Environment.TickCount64;
        for (int attempt = 1; Environment.TickCount64 - startTick < MaxWaitMs && !ct.IsCancellationRequested; attempt++)
        {
            Log.Always($"[{tag}] GM request (try {attempt}): /tell {GmChar} \"{request}\"");
            chat.Tell(GmChar, request);
            // Poll for the GM's reply (the ACK) until the next retry slot.
            for (int t = 0; t < RetryEveryMs && !ct.IsCancellationRequested; t += 1000)
            {
                await Task.Delay(1000, ct);
                if (w.Tells.TryGetValue(GmChar, out var reply) && reply.ms > askedMs
                    && reply.msg.Contains(ackWord, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Always($"[{tag}] GM acked: \"{reply.msg}\" — waiting 5s for the grant to apply");
                    await Task.Delay(5000, ct);
                    return true;
                }
            }
            Log.Info($"[{tag}] no GM ack yet — {GmChar} may not be logged in; retrying");
        }
        Log.Always($"[{tag}] GM request UNACKED after {MaxWaitMs / 60000} min — falling back");
        return false;
    }
}
