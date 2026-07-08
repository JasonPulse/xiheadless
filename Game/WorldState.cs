namespace XiHeadless.Game;

/// Live game state, updated by inbound packet handlers. The brain reads this.
public sealed class WorldState
{
    public uint MyId;
    public ushort MyIndex;      // our own targid (ActIndex from the 0x00A zone-in) — to target self-buff job abilities
    public string MyName = "";
    public ushort ZoneId;
    public float X, Y, Z;       // X/Z horizontal plane, Y vertical (height)
    public byte Rotation;       // 0-255 heading
    public bool Moving;         // set by the navigator; drives the 0x015 RunMode flag
    public long NowMs;          // monotonic clock (set by the runtime each update) for entity aging
    public long RevivedMs;      // last home-point revive — post-revive WEAKNESS guts stats for ~5 min (no new pulls)
    public long SelfGroupListMs; // last 0x0DD group_list entry about SELF — the server refreshes the roster on
                                 // leave/disband, so a self entry NOT followed by a member's re-listing means
                                 // that member left the party (disband detection, event-driven)
    public int MaxHp, MaxMp;    // from 0x01b job_info
    public uint Hp, Mp, Tp;     // current absolute values (from 0x0DF group_attr); TP 0-3000
    public uint Gil;            // gil balance (from the item-65535 entry in 0x01F/0x020)
    public byte MainJob, MainJobLevel, SubJob, SubJobLevel;
    // Level attained on EVERY job (0x1B job_lev[16]) — the main/sub seesaw reads this to decide which job
    // needs leveling (a sub must sit at ceil(main/2), and subs never earn exp directly).
    public readonly Dictionary<byte, int> JobLevels = new();
    public ushort ExpNow, ExpNext;   // exp into current level / exp needed to ding (from 0x061 CLISTATUS)
    public readonly ushort[] Stats = new ushort[7]; // STR,DEX,VIT,AGI,INT,MND,CHR (base)
    public byte Hpp, Mpp;       // HP%/MP% (0x037 / 0x0DF)
    public byte ServerStatus;   // animation/status (idle, engaged, dead, ...) from 0x037
    public byte VisibleGmLevel; // flags0 bits 29-31 of 0x037 — nonzero once !togglegm applied (GM icon shown)
    public byte[] StatusIcons = new byte[32]; // active status-effect icon ids from 0x037
    public byte[] KnownSpellBits = System.Array.Empty<byte>(); // 0x0AA bitmap; bit N = spell N known
    public uint CurrentTargetId; // last target we engaged/acted on (for disengage etc.)
    public bool InZone;         // true once 0x00A zone-in parsed

    // Job-ability recast tracking (client-side): NowMs when each ability was last fired. Combat.AbilityReady
    // compares against AbilityInfo.Recast (seconds); the server enforces the real recast, so this only has
    // to be approximately right. No entry = never used = ready.
    public readonly Dictionary<Ability, long> AbilityUsedMs = new();

    // Party invite (0x0DC GROUP_SOLICIT_REQ): set when another PC invites us to a party; the invitee
    // answers with 0x074. Cleared on accept.
    public volatile bool PartyInvitePending;
    public string PartyInviterName = "";

    // Other party members seen via 0x0DD group_list (key = char id). A non-empty fresh roster confirms a
    // party formed; it's also the basis for party healing/assist. Self is tracked via the HP/MP fields above.
    public readonly Dictionary<uint, PartyMember> PartyMembers = new();
    // Last PARTY chat line received per sender name (0x017 Kind=4) — the fleet's cross-process message bus.
    // Party chat is relayed cross-zone by the server, so two bots can coordinate (e.g. the Reunion RALLY
    // handshake) even when neither can see the other's entity. Includes our own echoed lines — filter by name.
    public readonly Dictionary<string, (string msg, long ms)> PartyChat = new(StringComparer.OrdinalIgnoreCase);

    // Tells (Kind 3) — the PARTYLESS coordination channel: a relogged tank with no party can't party-chat,
    // so the REFORM handshake (stale-roster recovery) rides tells. Keyed by sender, latest message wins.
    public readonly Dictionary<string, (string msg, long ms)> Tells = new(StringComparer.OrdinalIgnoreCase);

    // Active NPC event/cutscene/menu (from 0x32/0x34). Needed to respond (0x5B EVENTEND).
    public bool EventActive;
    public uint EventNpcId;
    public ushort EventNpcIndex;
    public ushort EventId;
    public DateTime LastEventDrivenUtc;   // when a brain capability last deliberately drove an event (so the auto-completer doesn't stomp it)

    // Quest log (from 0x056 MISSION::OTHER). Keyed by the LogType "Port" (QuestOffer::* = active/accepted
    // bitmaps 0x50-0x88+, QuestComplete::* = completed bitmaps 0x90-0xC8+). Each value is a 256-bit (32-byte)
    // bitmap: quest N -> byte[N/8] bit (N%8). Sent per area on zone-in. Lets the bot READ real quest state
    // (accepted/completed) instead of guessing — e.g. confirm the subjob quest before attempting it.
    public readonly Dictionary<ushort, byte[]> QuestLog = new();

    // /check (con) result, filled by the 0x029 reply to a 0x0DD check (Consider).
    public uint ConTargetId;     // mob we're awaiting a con for
    public int ConDifficulty;    // 0=TooWeak,1=IncrediblyEasy,2=EasyPrey,3=DecentChallenge,4=EvenMatch,5=Tough,6=VeryTough,7=IncrediblyTough; -1=pending
    public byte ConMobLevel;

    // Last synthesis result (0x06F COMBINE_ANS). SynthResult: -1=none/pending since reset, else the
    // SynthesisResult code (0=Success,1=Failed,2=Interrupted,6=SkillTooLow,13=MustWaitLonger,14=InterruptedCritical).
    // The crafter resets it to -1 before each synth and waits for the server to set it.
    public volatile int SynthResult = -1;
    public ushort SynthItemNo;   // result item id (on success)
    public byte SynthCount;      // result quantity

    // Last delivery-box (0x04B GP_SERV_COMMAND_PBX_RESULT) reply, encoded as (command << 8) | slot.
    // The server only replies to a Set when it SUCCEEDS (a failed insert/bad receiver sends nothing),
    // so Delivery uses this to confirm a send and find a free outgoing slot. -1 = none since reset.
    public volatile int DboxAck = -1;

    // Skill levels (0x062 skill_base[64]); skill id -> level. Masks the capped (0x8000) bit.
    // Encoding differs by type: combat/magic (1-45) are raw skill points; CRAFT skills (48-57) are
    // packed as (level<<5 | rank), so we shift them down to whole craft levels. SkillLevel returns the
    // brain-meaningful value for either (e.g. raw 32 for Smithing => level 1, not 32).
    public readonly ushort[] Skills = new ushort[64]; // [1]=H2H,[3]=Sword,[5]=Axe,[36]=Elemental,[50]=Smithing,...
    public int SkillLevel(int id)
    {
        if (id < 0 || id >= 64) return 0;
        int v = Skills[id] & 0x7FFF;
        return id is >= 48 and <= 57 ? v >> 5 : v;   // craft = level<<5 | rank; others = raw points
    }
    // Cumulative skill-up amount this session, per skill id, in 0.1-level units (from 0x029 SKILL_GAIN).
    // 0x062 only resends the integer level on a level cross, so this is how we see fine-grained gains.
    public readonly int[] SkillGains = new int[64];
    // Inventory: (container,slot) -> itemId (from 0x01F item list). Lets us find an item to equip.
    public readonly Dictionary<(byte container, byte slot), ushort> Inventory = new();
    // Parallel (container,slot) -> stack quantity, so we can drop a whole stack to free a slot.
    public readonly Dictionary<(byte container, byte slot), ushort> InventoryQty = new();

    // Last Auction House bid result (0x04C). 0 = none/pending since reset; else the server Result byte:
    // 0x01 = bought, 0xC5 = no listing at/below bid, 0xE5 = inventory full OR rare item already owned.
    public volatile int AucResult;
    public ushort AucResultItem;

    // Shop-sell appraisal (0x03D GP_SERV_COMMAND_SHOP_SELL, the reply to a 0x084 SHOP_SELL_REQ). The
    // server ONLY replies — with the base price, and stages the item for sale — if the item is sellable
    // (not NOSALE). No reply => unsellable. -1 = none/pending since reset.
    public volatile int SellAppraisePrice = -1;
    public byte SellAppraiseSlot;
    // NPC shop inventory (0x03C shop_list): shop slot index -> (item id, unit price). Buying references
    // the slot index (the server ignores the packet's ShopNo). Cleared by IShop before each OpenShop.
    public readonly Dictionary<byte, (ushort itemId, uint price)> Shop = new();

    /// True if the bot has learned the given spell (from the 0x0AA magic-data bitmap).
    public bool KnowsSpell(ushort spellId)
    {
        int i = spellId >> 3;
        return i < KnownSpellBits.Length && (KnownSpellBits[i] & (1 << (spellId & 7))) != 0;
    }

    public readonly Dictionary<uint, Entity> Entities = new();

    /// Resolve a UniqueNo (entity id) to the server's ActIndex/targid — the field the server uses to target
    /// actions/spells (0x01a_action.cpp: PAI->Engage(ActIndex)), NOT UniqueNo. Passing targid 0 makes the
    /// action a SILENT NO-OP (the historic Cast/Engage bug). Ourself -> MyIndex; a tracked entity with a
    /// nonzero Index -> that Index; else fall back to (id & 0xFFF) (targid = id & 0xFFF for FFXI entities).
    public ushort TargidOf(uint id) =>
        id == MyId ? MyIndex :
        Entities.TryGetValue(id, out var e) && e.Index != 0 ? e.Index :
        (ushort)(id & 0xFFF);

    // Who is ATTACKING us / our party, learned from 0x028 action packets (the combat actions the bot was
    // previously blind to — it could only see HP% fall, never the attacker count). Key = the mob's actor id,
    // value = (the id it actioned: our MyId or a party member, + NowMs of that action). IPerception ages this
    // out. Lets the bot SEE "2 mobs are on it" and "a mob is on the healer" instead of guessing.
    public readonly Dictionary<uint, (uint target, long ms)> Attackers = new();
    // Last CONFIRMED heal from the 0x028 action packet (msg 7 = "<target> recovers <amount> HP") — the ground
    // truth a healer checks after casting, as opposed to its own "I cast Cure" decision log.
    public (uint actor, uint target, int amount, long ms) LastHeal;

    // A browsed player-bazaar's inventory (s2c 0x105 per item, after our c2s 0x105 list request):
    // bazaar slot index -> (item id, unit price, stack count). Cleared by the buyer before each Browse.
    public readonly Dictionary<byte, (ushort item, uint price, uint num)> BazaarList = new();

    public override string ToString() =>
        $"id=0x{MyId:X} zone={ZoneId} pos=({X:F1},{Y:F1},{Z:F1}) rot={Rotation} " +
        $"HP%={Hpp} status={ServerStatus} maxHP={MaxHp} maxMP={MaxMp} " +
        $"job={MainJob}/{MainJobLevel} sub={SubJob}/{SubJobLevel} gil={Gil} entities={Entities.Count}";
}

/// A fellow party member, learned from 0x0DD group_list. Carries vitals for healing/assist decisions.
public sealed class PartyMember
{
    public uint Id;
    public byte Hpp, Mpp;
    // 0x0DD ZoneNo: 0 = the member is in OUR zone (vitals above are live); nonzero = the member is in THAT
    // other zone (the server then zeroes the HP fields, so Hpp==0 must NOT be read as "dead" — check Zone
    // first). This is the authoritative cross-zone partner location; entity views only cover ~50y.
    public ushort Zone;
    public long LastSeenMs;
}

public sealed class Entity
{
    public uint Id;
    public ushort Index;
    public string Name = "";
    public float X, Y, Z;       // X/Z horizontal, Y vertical
    public byte Rotation;
    public byte Hpp;
    public long LastSeenMs;     // for aging out stale entities

    // Type/allegiance, learned from a 0x00E carrying UPDATE_HP (persists across pos-only updates).
    public byte Allegiance;     // 0x00E @0x29: 0=MOB, 1=PLAYER, 2-4=town NPC nation, 5-6=beastmen
    public byte NamePrefix;     // @0x27: bit 0x08 set => owned by a PC (pet/trust/fellow)
    public bool TypeKnown;      // true once an UPDATE_HP packet set the type fields

    /// An attackable wild monster: allegiance MOB, not a PC-owned pet/trust.
    public bool IsMob => TypeKnown && Allegiance == 0 && (NamePrefix & 0x08) == 0;
}
