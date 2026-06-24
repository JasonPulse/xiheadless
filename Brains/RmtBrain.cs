namespace XiHeadless.Brains;

/// The RMT bot — one brain for the whole loop: yells gil-seller spam (pointing at the in-bot web
/// intake) AND fulfills gil requests that arrive there. On a request it acquires gil from the server
/// grant endpoint (credit the bot), ducks into its Mog House to mail the gil to the buyer, then comes
/// back out to resume spamming. Behavior is CODE (consts below). Reuses IChat / IDelivery / IGilGrant.
public sealed class RmtBrain(IPerception p, IChat chat, IDelivery delivery, IGilGrant gil, IZoning zoning) : IBrain
{
    static readonly bool UseYell = true;   // /yell (city-area); false = /shout (current zone)
    const int SpamIntervalSec = 30;
    const int Port = 8088;                 // in-bot intake: POST {player,amount} to http://localhost:8088/request

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
                        await gil.Grant(p.World.MyName, amount, "rmt_purchase", ct);   // credit the bot (stub until endpoint live)
                        bool sent = await delivery.SendGil(player, amount, ct);
                        Console.WriteLine($"[rmt] deliver {amount} gil -> '{player}': {sent}");
                    }
                    await delivery.ExitMogHouse(ct);   // back to the city so /yell reaches again
                }
                else Console.WriteLine($"[rmt] can't deliver from zone {zoning.CurrentZone} (no Mog House here); dropped {batch.Count} request(s)");

                lastSpamMs = clock.ElapsedMilliseconds;   // don't spam the instant we get back
            }

            // 2) Spam on the timer (only runs while out in the city, never mid-excursion).
            if (clock.ElapsedMilliseconds - lastSpamMs >= SpamIntervalSec * 1000L)
            {
                var msg = Lines[line++ % Lines.Length];
                if (UseYell) chat.Yell(msg); else chat.Shout(msg);
                Console.WriteLine($"[rmt] spam: {msg}");
                lastSpamMs = clock.ElapsedMilliseconds;
            }

            await Task.Delay(1000, ct);
        }
    }
}
