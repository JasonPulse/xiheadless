namespace XiHeadless.Routines;

/// The ONE owner of duo split/reunite logic, run symmetrically by BOTH party bots (tank grind loop and healer
/// support loop poll SplitDetected each tick and hand control here on a split). Replaces the three ad-hoc
/// regroup paths that used to fight each other (PartySupport dead-tank branch, SubjobBrain.StayWithWhm timeout,
/// stuck-homepoint) — those each guessed the partner's state from a ~50y stale entity view and could homepoint
/// on phantom deaths.
///
/// Signals (authoritative, not view-based):
/// - PartyMember.Zone (0x0DD ZoneNo): the server tells us the partner's ZONE whenever it differs from ours.
///   Zone==0 + Hpp==0 (after seen alive) = partner is DEAD here; Zone!=0 = partner is elsewhere (NOT dead).
/// - Party chat (0x017 Kind=4) is relayed cross-zone: "RALLY" tells the partner to converge even when neither
///   bot can see the other.
///
/// Protocol (no field reunions — hard rule from the desync history):
/// 1) RALLY — wait out our own KO (the core death handler revives us at the town crystal; note that
///    combat.Homepoint is the DEATH prompt and does nothing while alive — an alive bot WALKS).
/// 2) ROUTE VIA STAGING — GoTo the staging town, then GoTo the grind zone. Every path ends at the SAME
///    physical spot: the grind zone's zone-in. No rendezvous coordinates, no alive-warp needed.
/// 3) HOLD at the zone-in until the partner's FRESHLY-SEEN entity is beside us (zoning in rebuilds both
///    views from co-located spawns, which is what actually clears the mutual-invisibility staleness).
///    Entity records persist across zone changes, so "visible" must mean recently-seen, not merely cached.
public sealed class Reunion(IPerception p, INavigation nav, IZoning zoning, IParty party, ICombat combat, IChat chat, Reunion.Config cfg)
{
    public sealed class Config
    {
        public uint PartnerId;
        public string PartnerName = "";
        public string GrindZone = "";     // where the duo grinds — Reunion delivers both bots back here together
        public ushort GrindZoneId;
        public string StagingZone = "";   // the safe town both route through (their home point city) so every
        public ushort StagingZoneId;      // rally lands at the SAME grind-zone zone-in
        public bool Inviter;              // this side re-sends the party invite if the roster is ever empty
        public Func<CancellationToken, Task>? AtStaging;   // town errand hook (e.g. mail excess seals) — runs
                                                           // once per rally while we're safely in the staging city
        public string Tag = "rally";
    }

    bool _sawPartnerAlive;   // guards the dead-partner signal: a not-yet-populated roster reads Hpp=0 at startup
    bool _forced;            // Force() latch — a caller-requested rally (initial entry, tether timeout)

    /// Request a rally that the signals can't see (initial co-entry into the grind zone, a tether that timed
    /// out in-zone). The next SplitDetected poll returns true and RunAsync consumes the latch.
    public void Force() => _forced = true;

    void Log(string m) => Console.WriteLine($"[{cfg.Tag}] {m}");

    PartyMember? Partner => p.World.PartyMembers.TryGetValue(cfg.PartnerId, out var pm) ? pm : null;

    bool PartnerSentRally() =>
        p.World.PartyChat.TryGetValue(cfg.PartnerName, out var c)
        && c.msg.StartsWith("RALLY", StringComparison.OrdinalIgnoreCase)
        && p.World.NowMs - c.ms < 120_000;

    /// Cheap per-tick split check for the grind/support loops. True = stop grinding and call RunAsync.
    public bool SplitDetected()
    {
        if (_forced) return true;
        if (party.MemberCount == 0 || Partner is not { } pm) return false;   // party formation is the brain's job
        if (pm.Zone == 0 && pm.Hpp > 0) _sawPartnerAlive = true;
        if (PartnerSentRally())
        {
            // RALLY ECHO GUARD: both sides send periodic RALLYs while holding, so one regularly lands after
            // the receiver already completed — without this check the sides ping-ponged full rally cycles at
            // the zone-in forever. A RALLY from a partner who is VERIFIABLY beside us right now is an echo of
            // the rally that just resolved — consume it; a real split re-announces itself.
            // "Verifiably" REQUIRES A FRESH SIGHTING: a partner that died beside us leaves a stale close-by
            // entity record, and trusting it made this guard eat every real RALLY after a death (the WAR held
            // at the zone-in for 10+ minutes while the WHM discarded its calls). Consuming a signal needs
            // current evidence; when in doubt, rally — a spurious rally converges, an eaten one deadlocks.
            bool besideUs = pm.Zone == 0 && pm.Hpp > 0
                && p.World.Entities.TryGetValue(cfg.PartnerId, out var e) && (e.X != 0 || e.Z != 0)
                && p.World.NowMs - e.LastSeenMs < 15000
                && p.DistanceTo(e.X, e.Z) <= 40f;
            if (!besideUs) return true;                   // partner detected a split we can't see yet
            p.World.PartyChat.Remove(cfg.PartnerName);
        }
        if (combat.Dead) return true;                     // our own death is a split by definition
        if (pm.Zone != 0 && pm.Zone != zoning.CurrentZone) return true;   // partner authoritatively in another zone
        if (_sawPartnerAlive && pm.Zone == 0 && pm.Hpp == 0) return true; // partner KO'd in OUR zone (not merely zoned — ZoneNo disambiguates)
        return false;
    }

    // The partner's entity, only if seen RECENTLY — entity records persist across zone changes, so a cached
    // position from the previous zone must not count as "visible" (it produced dist=389 phantoms).
    Entity? FreshPartner(long maxAgeMs = 15000) =>
        p.World.Entities.TryGetValue(cfg.PartnerId, out var e) && (e.X != 0 || e.Z != 0)
        && p.World.NowMs - e.LastSeenMs < maxAgeMs ? e : null;

    /// Execute the full rally: route via the staging town to the grind zone's zone-in (the ONE spot every
    /// path lands on) and hold there until the partner's freshly-seen entity is beside us. Returns true when
    /// the duo is standing together in the grind zone; false after repeated failures (caller re-detects and
    /// retries — idling at a zone-in near town is safe, so failing here never strands anyone in the field).
    public async Task<bool> RunAsync(CancellationToken ct)
    {
        _forced = false;
        Log($"SPLIT detected (dead={combat.Dead}, partnerZone={Partner?.Zone ?? 0}, partnerHp={Partner?.Hpp ?? 0}%) — RALLY at the {cfg.GrindZone} zone-in");
        chat.Party("RALLY");
        for (int attempt = 1; attempt <= 4 && !ct.IsCancellationRequested; attempt++)
        {
            // 1) Wait out our own KO — the core death handler (BotHost.AutoHandleDeath) accepts the death
            //    prompt and revives us at the staging town's crystal. (Homepoint does NOTHING while alive.)
            while (combat.Dead && !ct.IsCancellationRequested) await Task.Delay(2000, ct);

            // 2) Route via staging so we ALWAYS enter the grind zone at its zone-in — even a bot already deep
            //    inside the grind zone walks out and back in, landing on the same spot as its partner.
            if (zoning.CurrentZone != cfg.StagingZoneId)
            {
                Log($"rally attempt {attempt}: routing via {cfg.StagingZone}");
                if (!await zoning.GoTo(cfg.StagingZone, ct)) { Log("couldn't reach staging — retrying"); continue; }
            }
            if (combat.Dead) continue;   // died walking out — revive and re-route
            // Town errands while we're here (attempt 1 only — retries shouldn't repeat them).
            if (attempt == 1 && cfg.AtStaging is { } errand)
            {
                try { await errand(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { Log($"staging errand failed (continuing rally): {e.Message}"); }
            }
            Log($"rally attempt {attempt}: crossing into {cfg.GrindZone} to hold at the zone-in");
            if (!await zoning.GoTo(cfg.GrindZone, ct)) { Log("cross failed — retrying"); continue; }

            // 3) Hold at the zone-in until the partner's FRESH entity spawns beside us (fresh co-located
            //    spawns rebuild both views — the fix for mutual-invisibility staleness).
            long hold = p.World.NowMs + 240_000;
            while (p.World.NowMs < hold && !ct.IsCancellationRequested && !combat.Dead)
            {
                if (party.InvitePending) party.AcceptInvite();
                if (party.MemberCount == 0 && cfg.Inviter && FreshPartner() is { } pe)
                    party.Invite(cfg.PartnerId, pe.Index);
                var pm = Partner;
                var e = FreshPartner();
                if (pm is { Zone: 0, Hpp: > 0 } && e is not null && p.DistanceTo(e.X, e.Z) <= 40f && party.MemberCount > 0)
                {
                    Log("duo together at the zone-in — reunion complete");
                    p.World.PartyChat.Remove(cfg.PartnerName);   // consume the RALLY so SplitDetected doesn't re-fire on it
                    return true;
                }
                long tickNow = p.World.NowMs;
                if (tickNow % 30000 < 2500)
                {
                    chat.Party("RALLY");
                    // PARTYLESS after a relog, party chat reaches nobody — and the partner's roster may be
                    // STALE (phantom party=1, no re-invite ever fires). Tells work without a party: REFORM
                    // asks the inviter side to purge its roster and re-invite us.
                    if (party.MemberCount == 0 && !cfg.Inviter) chat.Tell(cfg.PartnerName, "REFORM");
                    Log($"holding at zone-in — partnerZone={pm?.Zone ?? 0} hp={pm?.Hpp ?? 0}% fresh={(e != null ? p.DistanceTo(e.X, e.Z) : -1):F0}y party={party.MemberCount}");
                }
                await Task.Delay(2500, ct);
            }
            if (combat.Dead) { Log("KO'd holding at the zone-in — restarting the rally"); continue; }
            Log($"rally attempt {attempt}: partner didn't appear at the zone-in — re-rallying");
        }
        Log("reunion failed after 4 attempts — caller will re-detect and retry (idling at the zone-in is safe)");
        return false;
    }
}
