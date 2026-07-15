namespace XiHeadless.Routines;

/// A fleet bot's DAY PLAN: what it "decides" to do today (party up / solo / upkeep) and how long it plays
/// before it's done for the day. Deterministically seeded by (charId, UTC date) so a same-day relaunch (crash,
/// image roll, cooldown) resumes the SAME plan and the SAME end-of-day — no re-rolling a fresh 8 hours at
/// every login. Behavior is CODE (consts below), no env vars. Human-shaped: session lengths vary per char per
/// day, and a slice of the fleet solos or does chores instead of grinding in a group.
public static class SessionPlan
{
    public enum DayMode : byte { Party, Solo, Upkeep }

    // Weights (percent) for today's mode roll. Party is the default posture; some bots "decide" to solo
    // today; a few do town upkeep (AH/restock/sell) and keep a shorter day.
    const int PartyPct = 60, SoloPct = 25;   // Upkeep = the remainder (15)

    // Session-length band (minutes). The seeded roll lands inside; Upkeep days run the SHORTER band.
    const int MinMinutes = 120, MaxMinutes = 360;         // 2-6h (user rule: sessions are 2-6 hours, NEVER less)
    const int UpkeepMinMinutes = 120, UpkeepMaxMinutes = 180;  // chore days: 2-3h (the 2h floor applies to EVERY session)

    public readonly record struct Plan(DayMode Mode, DateTime StartUtc, DateTime EndUtc)
    {
        public bool DoneForToday => DateTime.UtcNow >= EndUtc;
    }

    /// Today's plan for this char. Stable for the whole UTC day: the END time is anchored to the char's FIRST
    /// login slot of the day (seeded start-of-day offset), not to "now" — so a relog at hour 5 of a 6-hour day
    /// has ~1 hour left, exactly like a human resuming their evening.
    public static Plan ForToday(uint charId)
    {
        var today = DateTime.UtcNow.Date;
        int seed = unchecked((int)(charId * 2654435761u) ^ (today.DayOfYear * 97) ^ today.Year);
        var rng = new Random(seed);

        int roll = rng.Next(100);
        var mode = roll < PartyPct ? DayMode.Party : roll < PartyPct + SoloPct ? DayMode.Solo : DayMode.Upkeep;

        (int lo, int hi) = mode == DayMode.Upkeep ? (UpkeepMinMinutes, UpkeepMaxMinutes) : (MinMinutes, MaxMinutes);
        int sessionMin = lo + rng.Next(hi - lo + 1);

        // The day's anchored start: a seeded offset into the UTC day. If the bot actually logs in later than
        // the anchor (fleet stagger, downtime), the remaining window just shrinks — never re-extends.
        var anchoredStart = today.AddMinutes(rng.Next(0, 24 * 60 - sessionMin));
        var end = anchoredStart.AddMinutes(sessionMin);

        // Logged in AFTER today's window already closed (late deploy/downtime): instant-done would churn
        // login->logout. Play a short seeded "evening check-in" instead, like a human squeezing in an hour.
        if (DateTime.UtcNow >= end) return new Plan(mode, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(30 + rng.Next(61)));

        var start = DateTime.UtcNow > anchoredStart ? anchoredStart : DateTime.UtcNow;
        return new Plan(mode, start, end);
    }
}
