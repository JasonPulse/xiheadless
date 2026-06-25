namespace XiHeadless.Game;

/// Live game state, updated by inbound packet handlers. The brain reads this.
public sealed class WorldState
{
    public uint MyId;
    public string MyName = "";
    public ushort ZoneId;
    public float X, Y, Z;       // X/Z horizontal plane, Y vertical (height)
    public byte Rotation;       // 0-255 heading
    public bool Moving;         // set by the navigator; drives the 0x015 RunMode flag
    public long NowMs;          // monotonic clock (set by the runtime each update) for entity aging
    public int MaxHp, MaxMp;    // from 0x01b job_info
    public uint Hp, Mp, Tp;     // current absolute values (from 0x0DF group_attr); TP 0-3000
    public uint Gil;            // gil balance (from the item-65535 entry in 0x01F/0x020)
    public byte MainJob, MainJobLevel, SubJob, SubJobLevel;
    public readonly ushort[] Stats = new ushort[7]; // STR,DEX,VIT,AGI,INT,MND,CHR (base)
    public byte Hpp, Mpp;       // HP%/MP% (0x037 / 0x0DF)
    public byte ServerStatus;   // animation/status (idle, engaged, dead, ...) from 0x037
    public byte[] StatusIcons = new byte[32]; // active status-effect icon ids from 0x037
    public byte[] KnownSpellBits = System.Array.Empty<byte>(); // 0x0AA bitmap; bit N = spell N known
    public uint CurrentTargetId; // last target we engaged/acted on (for disengage etc.)
    public bool InZone;         // true once 0x00A zone-in parsed

    // Active NPC event/cutscene/menu (from 0x32/0x34). Needed to respond (0x5B EVENTEND).
    public bool EventActive;
    public uint EventNpcId;
    public ushort EventNpcIndex;
    public ushort EventId;
    public DateTime LastEventDrivenUtc;   // when a brain capability last deliberately drove an event (so the auto-completer doesn't stomp it)

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

    public override string ToString() =>
        $"id=0x{MyId:X} zone={ZoneId} pos=({X:F1},{Y:F1},{Z:F1}) rot={Rotation} " +
        $"HP%={Hpp} status={ServerStatus} maxHP={MaxHp} maxMP={MaxMp} " +
        $"job={MainJob}/{MainJobLevel} sub={SubJob}/{SubJobLevel} gil={Gil} entities={Entities.Count}";
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
