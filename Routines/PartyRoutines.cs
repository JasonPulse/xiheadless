namespace XiHeadless.Routines;

/// Reusable party-formation helpers, shared by any brain that needs to group up. Invites and accepts are
/// restricted to a trusted name set (our own bot fleet) so the bot never grabs a real player by mistake,
/// and the inviter↔invitee pairing the server expects is satisfied (we invite by the live in-zone entity,
/// whose Id == char id and Index == targid).
public static class PartyRoutines
{
    /// Accept a pending invite iff it came from a trusted fleet name. Returns true if we accepted.
    public static bool AutoAccept(IParty party, IReadOnlyCollection<string> trusted)
    {
        if (!party.InvitePending) return false;
        bool ok = trusted.Any(n => string.Equals(n, party.InviterName, StringComparison.OrdinalIgnoreCase));
        if (ok) party.AcceptInvite();
        return ok;
    }

    /// Invite a named fleet PC if it's visible in-zone. Match by NAME (the reliable fleet identifier) and not
    /// TypeKnown/Allegiance: an idle PC sends position-only updates, so those type fields may never be set,
    /// but its Id/Index/Name are populated once it's in view. Returns true if an invite was sent this call.
    public static bool InviteIfPresent(IParty party, IPerception p, string partnerName)
    {
        var pc = p.Nearest(e => e.Id != p.World.MyId
            && string.Equals(e.Name, partnerName, StringComparison.OrdinalIgnoreCase));
        if (pc is null) return false;
        party.Invite(pc.Id, pc.Index);
        return true;
    }

    /// The pre-pull tether/ready-check (user rule: NEVER pull until ALL members are good). Waits until the
    /// partner is within `within` yards AND healthy (HP/MP at least the given floors), WALKING TOWARD it the
    /// whole time — standing still is what made views go stale (a stationary PC stops broadcasting), and
    /// moving actively halves the gap. Returns immediately-true if something is hitting US (never stand still
    /// while beaten — the caller's aggro defense takes over), and false on timeout (caller forces a rally).
    // maxTicks 150 (~5 min): an MP rest from near-zero takes ~2 min, and the old 140s budget force-rallied
    // over healthy recovery — a rally round-trip costs far more than finishing the sit.
    public static async Task<bool> WaitAllGood(
        IPerception p, INavigation nav, IParty party, uint partnerId,
        float within, byte minHp, byte minMp, CancellationToken ct, int maxTicks = 150)
    {
        if (party.MemberCount == 0) return true;   // solo — nothing to wait for
        bool weFollowed = false;   // only Stop navigation WE started — an unconditional Stop on success clipped
                                   // the caller's in-flight trek every tick (18 re-issued treks, zero progress)
        for (int w = 0; w < maxTicks && !ct.IsCancellationRequested; w++)
        {
            if (p.AttackersOn(p.World.MyId) > 0) return true;   // being hit — go fight, don't wait
            bool visible = p.World.Entities.TryGetValue(partnerId, out var e) && (e.X != 0 || e.Z != 0);
            float dist = visible ? p.DistanceTo(e!.X, e.Z) : 999f;
            // NO freshness checks — both signals here are EVENT-DRIVEN, so "stale" means "unchanged", not
            // "unknown". Party vitals (0x0DD/0x0DF) arrive when HP/MP/zone actually change (an idle topped
            // partner sends nothing — demanding a fresh record starved this gate forever, twice: entity-
            // freshness vetoed a stationary WHM at 2y, then pm-freshness vetoed an idle one at 0y). The
            // far-and-stale entity case is already rejected by the distance check itself.
            bool good = p.World.PartyMembers.TryGetValue(partnerId, out var pm)
                        && pm.Zone == 0 && pm.Hpp >= minHp && pm.Mpp >= minMp;
            if (dist <= within && good) { if (weFollowed) nav.Stop(); return true; }
            if (visible) { nav.Follow(partnerId); weFollowed = true; }   // close the gap ourselves — Navigator.Follow repaths continuously on-mesh
            if (w % 8 == 0) Log.Info($"[tether] waiting for partner good (dist={dist:F0} hp={pm?.Hpp ?? 0}% mp={pm?.Mpp ?? 0}% zone={pm?.Zone ?? 0})");
            await Task.Delay(2000, ct);
        }
        nav.Stop();
        return false;   // couldn't get together/ready in-zone — the caller escalates to a rally
    }
}
