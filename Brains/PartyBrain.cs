namespace XiHeadless.Brains;

/// Party-formation test brain. Runs on BOTH bots: each travels to a shared rendezvous town, then invites the
/// other fleet member BY CHAR ID (the 0x06E packet resolves the invitee by char id when targid=0, so this
/// doesn't depend on parsing PC entity names — which a crowded town makes unreliable) and accepts any incoming
/// invite. The first invite to land forms the party; the partner accepts; party.MemberCount confirms it.
public sealed class PartyBrain(IParty party, IPerception p, INavigation nav, IZoning zoning, IDelivery delivery) : IBrain
{
    // Fleet char ids are stable across logins (entity Id == char id). Invite by id rather than name/targid.
    static readonly Dictionary<string, uint> Fleet = new(StringComparer.OrdinalIgnoreCase)
    { ["Zzthenenfen"] = 30, ["Zzshekashi"] = 32 };   // WAR, WHM
    const string Rendezvous = "Windurst Woods";       // shared town both Windurst chars can route to

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(3000, ct);
        Log.Info($"[party] char='{p.World.MyName}' id={p.World.MyId} zone={zoning.CurrentZone}");

        if (zoning.CurrentZone == 0)
        {
            Log.Info("[party] inside the Mog House — exiting to the city");
            await delivery.ExitMogHouse(ct);
            await Task.Delay(2500, ct);
        }

        // Co-locate in the rendezvous zone (the char-id invite still wants both in the same zone).
        if (Game.Zonelines.Resolve(Rendezvous) is ushort rz && zoning.CurrentZone != rz)
        {
            Log.Info($"[party] traveling to rendezvous {Rendezvous}");
            await zoning.GoTo(Rendezvous, ct);
        }

        var partner = Fleet.FirstOrDefault(kv => !string.Equals(kv.Key, p.World.MyName, StringComparison.OrdinalIgnoreCase));
        uint partnerId = partner.Value;
        // Single leader (lower char id) sends the invite; the other only accepts. Avoids a mutual-invite race
        // where both have an outgoing invite pending and neither side accepts.
        bool leader = p.World.MyId < partnerId;
        Log.Info($"[party] rendezvous reached (zone {zoning.CurrentZone}); partner='{partner.Key}' id={partnerId} role={(leader ? "leader/invite" : "member/accept")}");

        // Converge on one fixed spot (by Manyny, the AH-area vendor) so we're near the partner — and so the
        // partner's entity (hence a real targid) is in view if the char-id-only lookup needs it.
        const float MeetX = 15f, MeetZ = -157f;
        if (!nav.CanReach(MeetX, p.World.Y, MeetZ)) Log.Info("[party] meet point off-mesh from here — holding in place");
        else nav.MoveTo(MeetX, MeetZ);
        for (int i = 0; i < 90 && !ct.IsCancellationRequested && p.DistanceTo(MeetX, MeetZ) > 6f; i++) await Task.Delay(1000, ct);
        nav.Stop();
        Log.Info($"[party] at meet point dist={p.DistanceTo(MeetX, MeetZ):F0}; entities={p.World.Entities.Count}; partnerEntity={(p.World.Entities.ContainsKey(partnerId) ? "visible" : "no")}");

        bool announced = false;
        for (int tick = 0; !ct.IsCancellationRequested; tick++)
        {
            // Accept any incoming invite (private server, only our fleet is online).
            if (party.InvitePending)
            {
                Log.Info($"[party] invite from '{party.InviterName}' — accepting");
                party.AcceptInvite();
            }

            if (party.MemberCount > 0)
            {
                if (!announced) { Log.Info($"[party] PARTY FORMED — roster has {party.MemberCount} other member(s)"); announced = true; }
            }
            else if (leader)
            {
                announced = false;
                ushort targid = p.World.Entities.TryGetValue(partnerId, out var e) ? e.Index : (ushort)0;
                party.Invite(partnerId, targid);
                if (tick % 3 == 0) Log.Info($"[party] invited id={partnerId} targid={targid} ({(targid != 0 ? "entity visible" : "by id, lookup")})");
            }
            else announced = false;
            await Task.Delay(3000, ct);
        }
    }
}
