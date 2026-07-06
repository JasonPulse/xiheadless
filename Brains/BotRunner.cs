namespace XiHeadless.Brains;

// ============================================================================
// A bot brain is an imperative coroutine the AUTHOR fully controls — no rule
// engine, no priority stack, no queued actions. The runtime starts RunAsync and
// cancels the token on shutdown. Capabilities (injected via the brain's ctor)
// expose awaitable actions; shared logic lives in reusable Routines that any
// brain can call (e.g. the shared LevelGrind/KillRoutine drive every job's fights).
// ============================================================================

/// Starts a brain's coroutine and cancels it on Stop.
public sealed class BotRunner
{
    readonly IBrain _brain;
    readonly CancellationTokenSource _cts = new();

    public BotRunner(IBrain brain) => _brain = brain;

    public void Start() => _ = Task.Run(async () =>
    {
        // RESTART on unexpected exceptions: a swallowed crash used to leave the bot a connected zombie for
        // the rest of the session (the WHM idled 10+ minutes in town after a transient collection-race threw).
        // Brains are written idempotent — they re-read world state and resume — so a fresh RunAsync recovers.
        for (int attempt = 0; !_cts.IsCancellationRequested; attempt++)
        {
            try { await _brain.RunAsync(_cts.Token); return; }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                // Full ToString (type + message + STACK) — a bare Message left an overnight NullReference
                // undiagnosable; the stack names the faulting routine.
                Log.Always($"[brain] CRASHED (restart {attempt + 1}): {e}");
                try { await Task.Delay(5000, _cts.Token); } catch (OperationCanceledException) { return; }
            }
        }
    });

    public void Stop() => _cts.Cancel();
}
