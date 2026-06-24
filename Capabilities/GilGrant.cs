namespace XiHeadless.Capabilities;

/// The bot's HTTP client for the server's gil-grant endpoint. The bot's in-game char stays
/// zero-permission; this hits the server-side API which enqueues a UpdateItem(gil) for the bot.
public interface IGilGrant
{
    Task<bool> Grant(string player, int amount, string reason, CancellationToken ct = default);
}
