namespace XiHeadless.Brains;

/// Read-only: logs nearby entities (id, distance, HP%) each second. Used to understand
/// what's targetable in a zone before wiring real combat — never sends an action.
public sealed class ObserveBrain(IPerception p) : IBrain
{
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var w = p.World;
            var near = w.Entities.Values
                .Where(e => e.Id != w.MyId)
                .OrderBy(e => p.DistanceTo(e.X, e.Z))
                .Take(8)
                .Select(e => $"id=0x{e.Id:X}#{e.Index} d={p.DistanceTo(e.X, e.Z):F0} hpp={e.Hpp} alg={e.Allegiance} mob={e.IsMob} pos=({e.X:F0},{e.Y:F0},{e.Z:F0}) '{e.Name}'");
            Console.WriteLine($"[observe] zone={w.ZoneId} entities={w.Entities.Count}\n    " + string.Join("\n    ", near));
            await Task.Delay(2000, ct);
        }
    }
}
