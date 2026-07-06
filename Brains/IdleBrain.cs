namespace XiHeadless.Brains;

/// Does nothing but stay logged in and still. The bot just sits where it is and lets the system
/// layer run — notably the event auto-completer, which finishes any server-pushed cutscene (incl.
/// the New Character Cutscene that calls setHomePoint()). Use it when the char should hold position
/// while core logic acts (e.g. letting the home point get set after a login), with no navigation or
/// combat to move it. Logs a one-line heartbeat so the session is observable.
public sealed class IdleBrain(IPerception p) : IBrain
{
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var w = p.World;
            Log.Info($"[idle] zone={w.ZoneId} pos=({w.X:F0},{w.Y:F0},{w.Z:F0}) event={(w.EventActive ? w.EventId : 0)}");
            await Task.Delay(3000, ct);
        }
    }
}
