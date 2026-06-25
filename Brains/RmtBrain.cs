namespace XiHeadless.Brains;

/// The RMT bot — one brain for the whole loop: yells gil-seller spam (pointing at the in-bot web
/// intake) AND fulfills gil requests that arrive there. On a request it acquires gil from the server
/// grant endpoint (credit the bot), ducks into its Mog House to mail the gil to the buyer, then comes
/// back out to resume spamming. Behavior is CODE (consts below). Reuses IChat / IDelivery / IGilGrant.
public sealed class RmtBrain(IPerception p, IChat chat, IDelivery delivery, IGilGrant gil, IZoning zoning, ILifecycle lifecycle) : IBrain
{
    static readonly bool UseYell = true;   // /yell (city-area); false = /shout (current zone)
    const int SpamIntervalSec = 1800;      // 30 minutes between spam broadcasts
    const int Port = 8088;                 // in-bot intake: POST {player,amount} to http://localhost:8088/request
    const int MaxOrders = 100;             // end check: log out after fulfilling this many orders (0 = run until stopped)

    // Gil-seller spam, auto-translate brackets and all (the real RMT look). {Phrase} tokens must be
    // entries in AutoTranslate.Ids; literal text passes through. Edit to taste / add phrases there.
    static readonly string[] Lines =
    {
        "{Hello!} cheap {Gil} 1M=$5 fast delivery! {Do you need any help?} Https://RMT.Network-Gnomes.com",
        "WTS {Gil} 24/7 stock, best price, instant trade! {Thank you.} Https://RMT.Network-Gnomes.com",
        "{Gil} {Gil} {Gil} lowest rates >> visit our shop! {Good luck!} Https://RMT.Network-Gnomes.com",
        "{Gil} {Do you need it?} {Buy?} 100k $2 Https://RMT.Network-Gnomes.com"
    };

    public async Task RunAsync(CancellationToken ct)
    {
        var intake = new RmtIntake(Port);
        intake.Start();
        Console.WriteLine($"[rmt] loop: spam /{(UseYell ? "yell" : "shout")} every {SpamIntervalSec}s + delivery intake on :{Port} (char='{p.World.MyName}')");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        long lastSpamMs = -SpamIntervalSec * 1000L;   // spam immediately on start
        int line = 0;
        int orders = 0;   // fulfilled orders, for the MaxOrders end check

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 1) Fulfill pending gil requests — one Mog House excursion for all queued, then come back out.
                if (intake.TryDequeue(out var first))
                {
                    var batch = new List<(string player, int amount)> { first };
                    while (intake.TryDequeue(out var more)) batch.Add(more);

                    if (await delivery.EnterMogHouse(ct))
                    {
                        foreach (var (player, amount) in batch)
                        {
                            await gil.Grant(p.World.MyName, amount, "rmt_purchase",
                                ct); // credit the bot (stub until endpoint live)
                            bool sent = await delivery.SendGil(player, amount, ct);
                            if (sent) orders++;
                            Console.WriteLine($"[rmt] deliver {amount} gil -> '{player}': {sent} ({orders} fulfilled)");
                        }

                        await delivery.ExitMogHouse(ct); // back to the city so /yell reaches again
                    }
                    else
                        Console.WriteLine(
                            $"[rmt] can't deliver from zone {zoning.CurrentZone} (no Mog House here); dropped {batch.Count} request(s)");

                    lastSpamMs = clock.ElapsedMilliseconds; // don't spam the instant we get back

                    // End check: fulfilled the quota -> log out from inside the brain (graceful shutdown).
                    if (MaxOrders > 0 && orders >= MaxOrders)
                    {
                        Console.WriteLine($"[rmt] fulfilled {orders} orders (quota {MaxOrders}) -> logging out");
                        lifecycle.Logout();
                        break;
                    }
                }

                // 2) Spam on the timer (only runs while out in the city, never mid-excursion).
                if (clock.ElapsedMilliseconds - lastSpamMs >= SpamIntervalSec * 1000L)
                {
                    var msg = Lines[line++ % Lines.Length];
                    if (UseYell) chat.Yell(msg);
                    else chat.Shout(msg);
                    Console.WriteLine($"[rmt] spam: {msg}");
                    lastSpamMs = clock.ElapsedMilliseconds;
                }

                await Task.Delay(1000, ct);
            }
        }
        finally
        {
            intake.Stop(); // close the storefront listener on stop (e.g. SIGTERM) so we exit cleanly
            Console.WriteLine("[rmt] stopped (storefront closed)");
        }
    }
}
