using XiHeadless.Game;

namespace XiHeadless.Routines;

/// Shout-driven party formation, the way real players do it (user design, 2026-07-08):
///   * A PARTY-day bot LISTENS for an "LFM" /shout in its hunt zone and answers with a /tell
///     ("LFP WHM 18 — invite me!"). Zone-wide shout only — never /yell (region-wide).
///   * If nothing is heard for a seeded stagger window, it becomes the RECRUITER: shouts an LFM,
///     parses LFP tells (LENIENTLY — parties are OPEN, real players answer freeform), and invites
///     against the classic composition: START at Tank+Healer+DD minimum, keep recruiting while
///     grinding toward Tank+Healer+Support+3DD (PartyRoles, per the BG-Wiki guide).
///   * Responders are invited when their entity is visible (same zone; bots drift to the same camps
///     via HuntZonePlan, so they meet). Level sync collapses level spread (leader syncs to lowest).
/// One instance per brain; the brain calls Step() from its idle/roam beat while it wants a party,
/// and TopUp() occasionally once partied (leader keeps filling seats — "attempt to get more").
public sealed class PartyFinder(IPerception p, IParty party, IChat chat, INavigation nav, string tag)
{
    const int ListenBeforeRecruitMs = 180_000;  // solo-listen this long before shouting our own LFM (+ seeded jitter)
    const int ShoutEveryMs = 90_000;            // recruiter re-shout cadence while seats are open
    const int AnswerCooldownMs = 300_000;       // don't re-answer the same shouter within this window
    const int InviteRetryMs = 15_000;           // retry invites for accepted responders until their entity is visible
    // SHOUT IS NOT ZONE-WIDE: the server delivers it within 180y (zone_entities CHAR_INSHOUT < 180.0f).
    // Formation therefore happens around a shared MEET SPOT (FleetDay walks there first), and the accept
    // tell carries a rendezvous coordinate the recruit walks to ("meet at (X Z)" — humans read that fine).

    readonly long _startMs = Environment.TickCount64;
    readonly int _jitterMs = (int)(p.World.MyId * 7919 % 120_000);   // per-char stagger: not everyone shouts at once
    readonly Dictionary<string, long> _answered = new(StringComparer.OrdinalIgnoreCase);   // shouters we already told
    readonly Dictionary<string, (PartyRoles.Role role, int level, long firstMs, long lastTryMs)> _accepted = new(StringComparer.OrdinalIgnoreCase);
    long _lastShoutMs, _seenTellMs, _seenMeetMs;
    int _membersAtInvite;
    bool _recruiting, _yielded;

    public bool Recruiting => _recruiting;

    /// One formation beat while UNPARTIED. Returns true once we're in a party.
    public bool Step()
    {
        var w = p.World;
        if (party.MemberCount > 0) return true;

        // 0) OPEN parties: accept any pending invite (a recruiter — bot or human — wants us).
        if (party.InvitePending) { Log.Info($"[{tag}] accepting party invite from '{party.InviterName}'"); party.AcceptInvite(); return false; }

        // 1) SEEKER: answer a fresh LFM shout with our role/level. YIELD rule: if we were recruiting and an
        // LFM arrives from an alphabetically-SMALLER name, we stop recruiting and answer them — deterministic
        // tie-break, so two simultaneous recruiters merge instead of dueling forever (live trio bug).
        foreach (var (shouter, (msg, ms)) in w.Shouts.ToArray())
        {
            if (shouter.Equals(w.MyName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!msg.Contains("LFM", StringComparison.OrdinalIgnoreCase)) continue;
            if (_recruiting || _yielded)
            {
                if (_recruiting && string.Compare(shouter, w.MyName, StringComparison.OrdinalIgnoreCase) >= 0) continue;   // they yield to us
                if (_recruiting)
                {
                    Log.Info($"[{tag}] yielding my LFM to {shouter} (tie-break) — answering theirs");
                    _recruiting = false;
                    _yielded = true;      // LATCH: don't re-arm recruiting next beat (live flip-flop bug)
                    _accepted.Clear();
                }
            }
            if (_answered.TryGetValue(shouter, out var last) && w.NowMs - last < AnswerCooldownMs) continue;
            _answered[shouter] = w.NowMs;
            string job = JobName(w.MainJob);
            chat.Tell(shouter, $"LFP {job} {w.MainJobLevel} - invite me!");
            Log.Info($"[{tag}] answered {shouter}'s LFM as {job} {w.MainJobLevel}");
        }

        // 1b) RENDEZVOUS: a recruiter we answered told us where to meet ("meet at (X Z)") — walk there so
        // our entity becomes visible for the invite (invites need the entity in view).
        foreach (var (sender, (msg, ms)) in w.Tells.ToArray())
        {
            if (ms <= _seenMeetMs || !_answered.ContainsKey(sender)) continue;
            int at = msg.IndexOf("meet at (", StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var nums = msg[(at + 9)..].TrimEnd('!', '.', ')').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (nums.Length >= 2 && float.TryParse(nums[0], out var mx) && float.TryParse(nums[1], out var mz))
            {
                Log.Info($"[{tag}] walking to {sender}'s rendezvous ({mx:F0},{mz:F0})");
                nav.MoveTo(mx, mz);
            }
        }
        _seenMeetMs = w.NowMs;

        // 2) RECRUITER: nothing to join after the stagger window -> run our own LFM. (_yielded latches us
        // out of recruiting after a tie-break — we're committed to the winner's party.)
        if (!_recruiting && !_yielded && Environment.TickCount64 - _startMs > ListenBeforeRecruitMs + _jitterMs)
        {
            _recruiting = true;
            Log.Info($"[{tag}] no LFM heard — recruiting my own party");
        }
        if (_recruiting) RecruitBeat();
        return false;
    }

    /// Recruiter beat: shout while seats are open, parse LFP tells, invite visible responders.
    /// Also called via TopUp() AFTER the party starts, to keep filling toward the full 6.
    void RecruitBeat()
    {
        var w = p.World;
        var need = MissingRoles();
        if (need == PartyRoles.Role.None) return;   // full house

        if (w.NowMs - _lastShoutMs > ShoutEveryMs)
        {
            _lastShoutMs = w.NowMs;
            chat.Shout($"LFM exp party - need {Describe(need)}! /tell me to join");
        }

        // Parse incoming tells for an LFP answer: any job token (WHM/PLD/...) or role word + optional level.
        // Lenient: "LFP WHM 18", "invite me pls whm", "tank lfg 25" all parse. (Real players answer freeform.)
        foreach (var (sender, (msg, ms)) in w.Tells.ToArray())
        {
            if (ms <= _seenTellMs) continue;
            byte job = 0; var role = PartyRoles.Role.None; int level = 0;
            foreach (var word in msg.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (job == 0) job = PartyRoles.ParseJobToken(word.Trim('!', '.', ','));
                if (role == PartyRoles.Role.None) role = PartyRoles.ParseRoleWord(word.Trim('!', '.', ','));
                if (level == 0 && int.TryParse(word, out var n) && n is >= 1 and <= 99) level = n;
            }
            if (job != 0) role = PartyRoles.PrimaryOf(job);
            if (role == PartyRoles.Role.None) continue;               // not an LFP answer — ignore
            if (_accepted.ContainsKey(sender)) continue;
            if ((PartyRoles.Role.None != (role & need)) || (job != 0 && (PartyRoles.CanFillOf(job) & need) != 0))
            {
                _accepted[sender] = (role, level, w.NowMs, 0);
                // Carry the rendezvous: invites need the recruit's ENTITY visible (~50y), and shout only
                // reaches 180y — the recruit walks to us. Humans read "meet at (X Z)" just fine.
                chat.Tell(sender, $"sweet - sending an invite! meet at ({w.X:F0} {w.Z:F0})");
                Log.Info($"[{tag}] recruit accepted: {sender} ({role}{(level > 0 ? $" lv{level}" : "")})");
            }
            else chat.Tell(sender, "thanks - that seat's filled, maybe next time!");
        }
        _seenTellMs = w.NowMs;

        // Invite accepted responders whose entity is visible (same zone — they drift to camp like we do).
        // Retries EXPIRE after a few minutes: by then the recruit either joined (0x0DD roster has no names,
        // so we can't tell directly) or isn't coming — endless re-invites spammed existing members (trio #4).
        foreach (var (name, (role, level, firstMs, lastTry)) in _accepted.ToArray())
        {
            if (w.NowMs - firstMs > 180_000) { _accepted.Remove(name); continue; }
            // The roster packet has no names, but a member-count INCREASE after our invite is joined-enough:
            // stop retrying (live: a joined THF was re-invited 11x over the retry window).
            if (lastTry > 0 && party.MemberCount > _membersAtInvite) { _accepted.Remove(name); continue; }
            if (w.NowMs - lastTry < InviteRetryMs) continue;
            _membersAtInvite = party.MemberCount;
            _accepted[name] = (role, level, firstMs, w.NowMs);
            if (PartyRoutines.InviteIfPresent(party, p, name)) Log.Info($"[{tag}] invited {name} ({role})");
            else
            {
                // DIAGNOSTIC (live trio: zero invites despite everyone at the meet spot): show what PC
                // entities we actually see, so "name not visible" vs "entity table empty" is decidable.
                var pcs = string.Join(",", w.Entities.Values.Where(e => e.Id != w.MyId && e.Name.Length > 0)
                                                            .Select(e => $"{e.Name}@{p.DistanceTo(e.X, e.Z):F0}y").Take(8));
                Log.Info($"[{tag}] can't invite {name} yet — not in view (named entities: {(pcs.Length > 0 ? pcs : "NONE")})");
            }
        }
    }

    /// Once partied (leader): keep filling open seats + level-sync to the lowest known member.
    public void TopUp()
    {
        if (!_recruiting) return;                    // only the recruiter/leader tops up
        RecruitBeat();
        var lowest = _accepted.Where(kv => kv.Value.level > 0).OrderBy(kv => kv.Value.level).FirstOrDefault();
        if (lowest.Key is not null && p.World.MainJobLevel > lowest.Value.level) party.SetLevelSync(lowest.Key);
    }

    /// Party is viable to START grinding: Tank + Healer + DD minimum (recruiter counts itself).
    public bool MinimumMet()
    {
        var have = RolesInParty();
        return have.HasFlag(PartyRoles.Role.Tank) && have.HasFlag(PartyRoles.Role.Healer) && have.HasFlag(PartyRoles.Role.Dps);
    }

    PartyRoles.Role RolesInParty()
    {
        var have = PartyRoles.CanFillOf(p.World.MainJob);            // us
        foreach (var kv in _accepted) have |= kv.Value.role;         // promised roles of joiners
        return have;
    }

    PartyRoles.Role MissingRoles()
    {
        var have = RolesInParty();
        var need = PartyRoles.Role.None;
        if (!have.HasFlag(PartyRoles.Role.Tank)) need |= PartyRoles.Role.Tank;
        if (!have.HasFlag(PartyRoles.Role.Healer)) need |= PartyRoles.Role.Healer;
        if (!have.HasFlag(PartyRoles.Role.Support) && party.MemberCount >= 3) need |= PartyRoles.Role.Support;
        if (party.MemberCount < 5) need |= PartyRoles.Role.Dps;      // room for DD up to the full 6
        return need;
    }

    static string Describe(PartyRoles.Role need)
    {
        var parts = new List<string>();
        if (need.HasFlag(PartyRoles.Role.Tank)) parts.Add("tank");
        if (need.HasFlag(PartyRoles.Role.Healer)) parts.Add("healer");
        if (need.HasFlag(PartyRoles.Role.Support)) parts.Add("support");
        if (need.HasFlag(PartyRoles.Role.Dps)) parts.Add("dps");
        return string.Join("/", parts);
    }

    static string JobName(byte j) => Game.PartyRoles.NameOf(j);
}
