namespace XiHeadless.Game;

/// Vana'diel clock — pure wall-clock math, no packet needed. The server's own formula
/// (LSB src/common/earth_time.h): vanadiel_epoch = 1009810800 Unix seconds, running at 25x earth speed.
public static class VanaTime
{
    const long Epoch = 1009810800;

    public static int Hour => (int)((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Epoch) * 25 / 3600 % 24);

    /// FFXI night (20:00-04:00) — the undead/nocturnal spawn window (Bogy, Ghoul, skeletons).
    public static bool IsNight => Hour is >= 20 or < 4;
}
