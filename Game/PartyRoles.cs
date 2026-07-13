namespace XiHeadless.Game;

/// FFXI party roles per job, following https://www.bg-wiki.com/ffxi/Jobs_%26_party_roles (user-designated
/// guide). Fleet parties recruit against the classic composition: Tank + Healer + Support + 3 DD, with
/// Tank + Healer + DD as the minimum viable start. The wiki's DD variants (heavy/light/magic) all collapse
/// to Dps for composition purposes; what matters for a slot is Tank/Healer/Support/Dps.
/// Primary = the role the job is recruited AS; CanFill adds the wiki's secondary roles (e.g. no WHM around ->
/// a SCH or RDM fills the Healer slot; no PLD/RUN -> NIN/WAR/MNK/PUP can tank).
public static class PartyRoles
{
    [Flags]
    public enum Role : byte { None = 0, Tank = 1, Healer = 2, Support = 4, Dps = 8 }

    // job id (Capabilities.Job consts) -> (primary, all roles it can fill). From the BG-Wiki table:
    //   WAR/MNK heavy DD + emergency tank; RDM light/magic DD + support/healer; NIN/PUP light DD + tank;
    //   SMN/COR/DNC light DD + support; SCH magic DD + healer; GEO magic DD/support; BRD support + light DD;
    //   PLD tank; RUN tank + heavy DD; WHM healer; BLM/THF/BST/RNG/SAM/DRK/DRG/BLU pure DD.
    static readonly (Role primary, Role canFill)[] ByJob =
    {
        /* 0 NON */ (Role.None, Role.None),
        /* 1 WAR */ (Role.Dps, Role.Dps | Role.Tank),
        /* 2 MNK */ (Role.Dps, Role.Dps | Role.Tank),
        /* 3 WHM */ (Role.Healer, Role.Healer),
        /* 4 BLM */ (Role.Dps, Role.Dps),
        /* 5 RDM */ (Role.Dps, Role.Dps | Role.Support | Role.Healer),
        /* 6 THF */ (Role.Dps, Role.Dps),
        /* 7 PLD */ (Role.Tank, Role.Tank),
        /* 8 DRK */ (Role.Dps, Role.Dps),
        /* 9 BST */ (Role.Dps, Role.Dps),
        /* 10 BRD */ (Role.Support, Role.Support | Role.Dps),
        /* 11 RNG */ (Role.Dps, Role.Dps),
        /* 12 SAM */ (Role.Dps, Role.Dps),
        /* 13 NIN */ (Role.Dps, Role.Dps | Role.Tank),
        /* 14 DRG */ (Role.Dps, Role.Dps),
        /* 15 SMN */ (Role.Dps, Role.Dps | Role.Support),
        /* 16 BLU */ (Role.Dps, Role.Dps),
        /* 17 COR */ (Role.Dps, Role.Dps | Role.Support),
        /* 18 PUP */ (Role.Dps, Role.Dps | Role.Tank),
        /* 19 DNC */ (Role.Dps, Role.Dps | Role.Support),
        /* 20 SCH */ (Role.Dps, Role.Dps | Role.Healer),
        /* 21 GEO */ (Role.Support, Role.Support | Role.Dps),
        /* 22 RUN */ (Role.Tank, Role.Tank | Role.Dps),
    };

    public static Role PrimaryOf(byte job) => job < ByJob.Length ? ByJob[job].primary : Role.None;
    public static Role CanFillOf(byte job) => job < ByJob.Length ? ByJob[job].canFill : Role.None;

    /// The wiki's HEAVY physical DDs (heavy armor, two-handers, survivable): WAR MNK DRK SAM DRG. Used for
    /// the SATA sub-tank pick — the puller who briefly holds the mob's face must survive it (user rule).
    public static bool IsHeavyDd(byte job) => job is 1 or 2 or 8 or 12 or 14;

    /// Parse a role word or job token from recruitment chatter ("LFP WHM 18", "tank lfg", "blm dd").
    /// Lenient by design — OPEN parties mean real players answer in freeform text.
    public static Role ParseRoleWord(string word) => word.ToUpperInvariant() switch
    {
        "TANK" => Role.Tank,
        "HEALER" or "HEAL" or "HEALS" or "WHITEMAGE" => Role.Healer,
        "SUPPORT" or "BUFFER" => Role.Support,
        "DPS" or "DD" or "DAMAGE" or "MELEE" or "NUKER" => Role.Dps,
        _ => Role.None,
    };

    // THE job-id <-> short-name table (was copied verbatim in PartyFinder, PartyCombat, and JobLifecycle).
    static readonly string[] JobNames =
        { "NON", "WAR", "MNK", "WHM", "BLM", "RDM", "THF", "PLD", "DRK", "BST", "BRD",
          "RNG", "SAM", "NIN", "DRG", "SMN", "BLU", "COR", "PUP", "DNC", "SCH", "GEO", "RUN" };

    /// Job id -> short name ("WAR"); out-of-range ids read as "ADV" (an unknown adventurer).
    public static string NameOf(byte j) => j >= 1 && j < JobNames.Length ? JobNames[j] : "ADV";

    /// Job short-name (WAR/WHM/...) -> job id, for parsing "LFP WHM 18"-style responses. 0 = no match.
    public static byte ParseJobToken(string word)
    {
        var u = word.ToUpperInvariant();
        for (byte i = 1; i < JobNames.Length; i++) if (JobNames[i] == u) return i;
        return 0;
    }
}
