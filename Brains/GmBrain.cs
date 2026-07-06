namespace XiHeadless.Brains;

/// The GM grant bot — the fleet counterpart to RmtBrain. A dedicated GM character (permission=1) logs
/// in, starts the in-bot GmIntake console, then loops: drain every queued grant request and issue the
/// matching persistent server command via chat — "!grantjob <player> <value>" for a job unlock,
/// "!setcap <player> <value>" for a level cap / limit-break — spacing them out (the server processes
/// one command per tick). Otherwise it idles in place (stays logged in; no movement). Behavior is CODE
/// (consts below). Reuses IChat / ILifecycle — no new chat or movement code.
public sealed class GmBrain(IPerception p, IChat chat, ILifecycle lifecycle) : IBrain
{
    const int Port = 8089;             // in-bot intake: POST {player,kind,value} to http://localhost:8089/request
    const int CommandSpacingMs = 1500; // gap between GM commands (server processes ~one per tick)
    const int MaxGrants = 0;           // end check: log out after this many grants (0 = run until stopped)

    public async Task RunAsync(CancellationToken ct)
    {
        var intake = new GmIntake(Port);
        intake.Start();
        Console.WriteLine($"[gm] loop: grant intake on :{Port} (char='{p.World.MyName}')");

        int grants = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Drain all queued grant requests and issue each as a GM command.
                while (intake.TryDequeue(out var req))
                {
                    var (player, kind, value) = req;
                    if (!GmIntake.IsValid(player, kind, value))
                    {
                        Console.WriteLine($"[gm] skip malformed request: player='{player}' kind='{kind}' value='{value}'");
                        continue;
                    }

                    string cmd = kind == "job"
                        ? $"!grantjob {player} {value}"
                        : $"!setcap {player} {value}";
                    chat.Say(cmd);
                    grants++;
                    Console.WriteLine($"[gm] issued {kind} grant -> '{player}': {cmd} ({grants} issued)");

                    await Task.Delay(CommandSpacingMs, ct); // let the server process one command per tick

                    if (MaxGrants > 0 && grants >= MaxGrants)
                    {
                        Console.WriteLine($"[gm] issued {grants} grants (quota {MaxGrants}) -> logging out");
                        lifecycle.Logout();
                        return;
                    }
                }

                await Task.Delay(1000, ct); // idle in place; stay logged in
            }
        }
        finally
        {
            intake.Stop(); // close the console listener on stop (e.g. SIGTERM) so we exit cleanly
            Console.WriteLine("[gm] stopped (console closed)");
        }
    }
}
