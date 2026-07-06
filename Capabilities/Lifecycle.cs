namespace XiHeadless.Capabilities;

public sealed class Lifecycle(Action onLogout) : ILifecycle
{
    public void Logout() => onLogout();
}
