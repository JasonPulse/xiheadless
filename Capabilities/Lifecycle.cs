namespace XiHeadless.Capabilities;

/// Lets a brain END ITS OWN SESSION from inside its logic — its "end check". Logout() requests a
/// graceful shutdown (the host cancels the brain, then sends the clean ~40s FFXI logout); no external
/// signal/command needed. The brain decides WHEN (e.g. after N deliveries, a quota, a schedule), calls
/// Logout(), and returns — it will also be cancelled shortly after.
public interface ILifecycle
{
    void Logout();
}

public sealed class Lifecycle(Action onLogout) : ILifecycle
{
    public void Logout() => onLogout();
}
