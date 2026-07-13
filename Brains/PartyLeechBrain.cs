namespace XiHeadless.Brains;

/// WHM duo HEALER. Solo is unviable (sleep-lock death), so the WHM parties the WAR and keeps it alive while it
/// tanks the Buburimu mobs to farm the subjob items. This brain is THIN: it only does setup (stage in Mhaura,
/// form the party, cross to the grind zone) and then hands off to the shared `PartySupport` routine — ALL the
/// heal/follow/rest/self-preserve logic lives there now, not hardcoded here (see CLAUDE.md: reuse routines).
public sealed class PartyLeechBrain(
    IParty party, IPerception p, INavigation nav, IZoning zoning, IInventory inv, IAuctionHouse ah, IMagic magic, ICombat combat, IChat chat, ILifecycle lifecycle, IDelivery delivery) : IBrain
{
    const uint WarId = 30;                            // the WAR (tank/kills). WHM invites; WAR auto-accepts (BotHost).
    const string WarName = "Zzthenenfen";             // for the Reunion party-chat handshake (sender-keyed)
    const string GrindZone = "Buburimu_Peninsula";
    const ushort GrindZoneId = 118;
    const ushort ScrollParalyze = 4666;   // Paralyze (WHM lv4) — "the #1 helpful WHM spell" (user)
    // Quest items are Rare/EX but still DROPPABLE (user) — protect them in every keep-set this character uses.
    static readonly HashSet<ushort> Keep = new()
        { StealthRoutines.SilentOil, StealthRoutines.PrismPowder, ScrollParalyze,
          QuestDefs.WildRabbitTail, QuestDefs.CupOfDhalmelSaliva, QuestDefs.BloodyRobe, 1126, 1127,
          940 };   // Revival Tree Root — Bogy side-drop, the PLD unlock quest item (bank it for the WAR char)

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(4000, ct);
        Log.Info($"[leech] char='{p.World.MyName}' lvl={p.World.MainJobLevel} zone={zoning.CurrentZone}");

        // Stock Sneak/Invis powders ONLY if we're already standing at an AH (don't detour — the only AH is across
        // the very crossing we'd need them for). Maintain keeps them up across the session, so buy a full dozen.
        if ((inv.CountOf(StealthRoutines.SilentOil) < 12 || inv.CountOf(StealthRoutines.PrismPowder) < 12) && Game.Zonelines.HasAuctionHouse(zoning.CurrentZone))
        {
            await StealthRoutines.EnsureStock(ah, p, inv, 12, Keep, ShopRoutines.NoFree, ct);
            Log.Info($"[leech] powders: oil={inv.CountOf(StealthRoutines.SilentOil)} prism={inv.CountOf(StealthRoutines.PrismPowder)}");
        }

        // SPELL-KNOWLEDGE DIAGNOSTIC: the user GM-granted ALL spells 3 days ago, yet Paralyze never casts —
        // so either the 0x0AA bitmap doesn't carry the bit or our decode is off. Print the ground truth once.
        Log.Info($"[leech] spells known: Cure={magic.Known(Spell.Cure)} CureII={magic.Known(Spell.CureII)} CureIII={magic.Known(Spell.CureIII)} Dia={magic.Known(Spell.Dia)} Paralyze={magic.Known(Spell.Paralyze)} Protect={magic.Known(Spell.Protect)}");
        Log.Info($"[leech] spell bitmap[0..16]: {Convert.ToHexString(p.World.KnownSpellBits.Take(16).ToArray())} (len={p.World.KnownSpellBits.Length})");

        // LEARN PARALYZE the moment we possess the scroll (a GM-provisioned one is learned automatically at
        // login; we also buy it whenever we happen to be at an AH). PartySupport's enfeeble cascade casts it
        // per foe as soon as it's Known — the missing piece was only ever the scroll.
        if (!magic.Known(Spell.Paralyze))
        {
            if (inv.CountOf(ScrollParalyze) == 0 && Game.Zonelines.HasAuctionHouse(zoning.CurrentZone))
                await ShopRoutines.BuyAtLeast(ah, p, inv, ScrollParalyze, 1, Keep, ShopRoutines.NoFree, ct);
            await MagicRoutines.LearnFromScroll(inv, magic, p, ScrollParalyze, Spell.Paralyze, ct, "leech");
        }

        // NO TOWN STAGING — the char-id invite lands CROSS-ZONE (proven live), so invite immediately from
        // wherever we logged in. The Reunion protocol co-locates the duo at the grind-zone zone-in afterward.
        var stealthCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Log.Info($"[leech] inviting WAR id={WarId} (cross-zone, no staging)");
        for (int t = 0; t < 300 && party.MemberCount == 0 && !ct.IsCancellationRequested; t++)
        {
            if (t % 3 == 0) party.Invite(WarId);
            if (t % 15 == 0) Log.Info($"[leech] awaiting party... MemberCount={party.MemberCount}");
            await Task.Delay(1000, ct);
        }
        // NO solo crossing: zone entry belongs to the Reunion protocol (the WAR's grind loop forces a rally
        // and both bots cross together from the crystal). We just hand off to the support loop; its Reunion
        // check answers the WAR's RALLY.
        Log.Info($"[leech] party={party.MemberCount} — handing off to PartySupport (Reunion owns the {GrindZone} entry)");

        // ITEM REPORTER: the quest items are Rare/EX — the WAR can never see this bag, so broadcast our
        // tally over party chat ("SJITEMS tail cup robe", cross-zone) every ~90s. The WAR's farm-done
        // condition reads it; the treasure pool routes second drops here automatically.
        _ = Task.Run(async () =>
        {
            try
            {
                int tick = 0;
                while (!ct.IsCancellationRequested)
                {
                    // Each step individually guarded: ONE exception in the bag chain silently killed this
                    // whole task (reporter went dark; the WAR farmed blind with whm:no-report for a session).
                    try { chat.Party($"SJITEMS {inv.CountOf(QuestDefs.WildRabbitTail)} {inv.CountOf(QuestDefs.CupOfDhalmelSaliva)} {inv.CountOf(QuestDefs.BloodyRobe)}"); }
                    catch (Exception e) { Log.Info($"[leech] reporter send failed: {e.Message}"); }
                    // BAG MAINTENANCE (every ~4.5 min): this brain had NONE — days of pool-junk lot shares
                    // filled the 30 slots and Rare/EX pool drops (the WHM's own TAIL/ROBE!) bounce off a
                    // full bag exactly like the WAR's cup did. Sell junk in place + stash surplus seals.
                    if (++tick % 3 == 0)
                    {
                        try
                        {
                            await MailRoutines.StashExcess(inv, p, 1126, keepMax: 2, ct);
                            await MailRoutines.StashExcess(inv, p, 1127, keepMax: 2, ct);
                            await inv.SellAllJunk(Keep, ct);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { Log.Info($"[leech] bag maintenance failed (retrying next cycle): {e.Message}"); }
                    }
                    await Task.Delay(90_000, ct);
                }
            }
            catch (OperationCanceledException) { }
        }, ct);

        // Hand off to the shared party-support routine. No hardcoded cure/follow logic in this brain anymore.
        // Splits (either side dying, zoning, or desyncing) route through the shared Reunion protocol: both bots
        // rally at the Mhaura crystal and cross back together. The WHM is the party's inviter, so it re-invites
        // during a rally if the roster ever empties.
        var reunion = new Reunion(p, nav, zoning, party, combat, chat, new Reunion.Config
        {
            PartnerId = WarId, PartnerName = WarName,
            GrindZone = GrindZone, GrindZoneId = GrindZoneId,
            StagingZone = "Mhaura", StagingZoneId = 249,
            Inviter = true, Tag = "heal-rally",
            // Same seal offload as the WAR: stash to the Mog Case (EX — mailing is server-refused).
            AtStaging = async c =>
            {
                await MailRoutines.StashExcess(inv, p, 1126, keepMax: 2, c);
                await MailRoutines.StashExcess(inv, p, 1127, keepMax: 2, c);
            },
        });
        var support = new PartySupport(party, p, nav, zoning, magic, combat);
        await support.RunAsync(new PartySupport.Config
        {
            TankId = WarId, TankName = WarName,
            Reunion = reunion,    // follow the tank; any split rallies both bots (Reunion owns zone re-entry)
            Heal = Spell.Cure,
            CureSelfBelow = 80, // keep itself topped to ~full when safe — so it reaches the WAR's "ready" gate (>=70)
                                // instead of idling in a 55-85% dead band that deadlocks the pull
            Buff = true,        // keep Protect + Shell on the WAR + self (less incoming damage -> Cure keeps up)
            Enfeeble = true,    // Dia (DoT + Def-down) + Paralyze on the WAR's foe (faster kills, less damage taken)
            OnConverged = () => stealthCts.Cancel(),   // drop Invis once we're with the WAR (it holds the hate)
            Inviter = true,     // we own party formation — re-invite the WAR after a detected disband (tank relog)
            Tag = "heal",
        }, ct);
        _ = lifecycle;   // reserved (graceful logout handled by BotHost)
    }
}
