namespace XiHeadless.Routines;

/// Ask the central GM bot to unlock a job for us — the sanctioned way a fleet bot gets a job unlock (the one
/// thing it can't self-do: Maat/quest-gated jobs). Sends an in-game /tell to the GM character; the GM bot
/// (GmBrain) grants to the tell's SENDER, so a bot can only ever unlock ITS OWN job (no target spoofing).
/// This just sends + waits for the grant to land server-side; the CALLER verifies by then attempting the
/// job change (a granted job changes cleanly, a still-locked one does not).
public static class GmGrant
{
    const string GmChar = "guge";   // the central GM bot's character (tell target)
    const int SettleMs = 10000;     // GM polls tells ~1s + ~1.5s command spacing + server tick; 10s is ample

    public static async Task Request(IChat chat, string jobName, string tag, CancellationToken ct)
    {
        Log.Always($"[{tag}] GM-grant: /tell {GmChar} \"grantjob {jobName}\" — waiting {SettleMs / 1000}s to land");
        chat.Tell(GmChar, $"grantjob {jobName}");
        await Task.Delay(SettleMs, ct);
    }
}
