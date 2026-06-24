namespace XiHeadless.Brains;

// ============================================================================
// A bot brain is an imperative coroutine the AUTHOR fully controls — no rule
// engine, no priority stack, no queued actions. The runtime starts RunAsync and
// cancels the token on shutdown. Capabilities (injected via the brain's ctor)
// expose awaitable actions; shared logic lives in reusable Routines that any
// brain can call (e.g. WAR and MNK share BuildTp, each picks its own weaponskill).
// ============================================================================

public interface IBrain { Task RunAsync(CancellationToken ct); }

/// Starts a brain's coroutine and cancels it on Stop.
public sealed class BotRunner
{
    readonly IBrain _brain;
    readonly CancellationTokenSource _cts = new();

    public BotRunner(IBrain brain) => _brain = brain;

    public void Start() => _ = Task.Run(async () =>
    {
        try { await _brain.RunAsync(_cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception e) { Console.WriteLine($"[brain] {e.Message}"); }
    });

    public void Stop() => _cts.Cancel();
}
